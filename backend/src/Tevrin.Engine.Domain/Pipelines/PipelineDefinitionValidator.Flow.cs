namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    /// <summary>FR-343–FR-349 (0.8.0, §4.49 amended): the flow constructs' checks.</summary>
    private sealed partial class Run
    {
        /// <summary>The <c>stage.Index</c> of a stage inside a parallel branch (it has no place in the top-level order).</summary>
        private const int InBranch = -1;

        /// <summary>FR-343: every stage with its YAML path, each parallel group followed by its branch stages.</summary>
        private static List<(StageDefinition Stage, string Path)> AllStagesWithPaths(PipelineDefinition definition)
        {
            var list = new List<(StageDefinition, string)>();
            for (var i = 0; i < definition.Stages.Count; i++)
            {
                AddWithBranches(list, definition.Stages[i], $"stages[{i}]");
            }

            return list;
        }

        private static void AddWithBranches(List<(StageDefinition, string)> list, StageDefinition stage, string path)
        {
            list.Add((stage, path));
            if (stage.Parallel is not { } parallel)
            {
                return;
            }

            for (var b = 0; b < parallel.Branches.Count; b++)
            {
                for (var k = 0; k < parallel.Branches[b].Count; k++)
                {
                    AddWithBranches(list, parallel.Branches[b][k], BranchStagePath(path, b, k));
                }
            }
        }

        private static string BranchStagePath(string groupPath, int branch, int stage) => $"{groupPath}.parallel.branches[{branch}][{stage}]";

        /// <summary>FR-348: <c>limits.revisions</c> is 1–100.</summary>
        private void ValidateLimits()
        {
            if (definition.Limits is { } limits && limits.Revisions is < RunLimits.MinRevisions or > RunLimits.MaxRevisions)
            {
                Add(DefinitionErrorCodes.Parse, "limits.revisions", $"'revisions' must be {RunLimits.MinRevisions}–{RunLimits.MaxRevisions}.");
            }
        }

        /// <summary>The flow constructs of a stage at top-level position <paramref name="index"/> (or <see cref="InBranch"/>).
        /// <paramref name="seen"/> holds the stage ids declared so far (branch stage ids are unique across the pipeline).</summary>
        private void ValidateFlow(StageDefinition stage, int index, string path, HashSet<string> seen)
        {
            ValidateConstructKinds(stage, path);
            ValidateVeto(stage, index, path);
            ValidateFanOut(stage, path);
            ValidateAggregate(stage, index, path);
            ValidateRoutes(stage, index, path);
            ValidateVariants(stage, path);
            ValidateFeedback(stage, path);
            ValidateMap(stage, index, path);
            ValidateConversation(stage, index, path);
            ValidateFromRuns(stage, path);
            ValidateParallel(stage, path, seen);
        }

        /// <summary>§4.49 amended: a stage is at most one of <c>parallel</c>, <c>fan_out</c>, <c>map</c>, <c>conversation</c> and
        /// <c>variants</c> (FR-355, FR-356 add the middle two); a parallel
        /// group has no agent or handler; <c>fan_out</c>, <c>variants</c> and <c>routes</c> need an agent stage, and
        /// <c>aggregate</c> an agent or handler stage (<see cref="DefinitionErrorCodes.StageKind"/>).</summary>
        private void ValidateConstructKinds(StageDefinition stage, string path)
        {
            var constructs = new[]
                {
                    (stage.Parallel is not null, "parallel"), (stage.FanOut is not null, "fan_out"), (stage.Map is not null, "map"),
                    (stage.Conversation is not null, "conversation"), (stage.Variants is not null, "variants"),
                }
                .Where(c => c.Item1).Select(c => c.Item2).ToList();
            if (constructs.Count > 1)
            {
                Add(DefinitionErrorCodes.StageKind, path, $"Stage '{stage.Id}' combines {string.Join(" and ", constructs.Select(c => $"'{c}'"))}; a stage uses one.");
            }

            if (stage.Parallel is not null && (stage.Agent is not null || stage.Handler is not null))
            {
                Add(DefinitionErrorCodes.StageKind, path + ".parallel", $"Parallel group '{stage.Id}' can't have an 'agent' or a 'handler'; its branches do the work.");
            }

            if (stage.Kind != StageKind.Agent)
            {
                if (stage.FanOut is not null)
                {
                    Add(DefinitionErrorCodes.StageKind, path + ".fan_out", $"'fan_out' needs an agent stage; '{stage.Id}' has no 'agent'.");
                }

                if (stage.Variants is not null)
                {
                    Add(DefinitionErrorCodes.StageKind, path + ".variants", $"'variants' needs an agent stage; '{stage.Id}' has no 'agent'.");
                }

                if (stage.Routes.Count > 0)
                {
                    Add(DefinitionErrorCodes.StageKind, path + ".routes", $"'routes' needs an agent stage; '{stage.Id}' has no 'agent'.");
                }
            }

            if (stage.Aggregate is not null && (stage.Kind == StageKind.Manual || stage.Parallel is not null))
            {
                Add(DefinitionErrorCodes.StageKind, path + ".aggregate", $"'aggregate' needs an agent or handler stage; '{stage.Id}' has neither.");
            }
        }

        /// <summary>FR-343: at least two non-empty branches of ordinary stages, ids unique across the pipeline, and no branch
        /// stage reading another branch's output, and no two branches producing one artifact (both
        /// <see cref="DefinitionErrorCodes.BranchInput"/>).</summary>
        private void ValidateParallel(StageDefinition group, string path, HashSet<string> seen)
        {
            if (group.Parallel is not { } parallel)
            {
                return;
            }

            if (parallel.Branches.Count < ParallelDefinition.MinBranches)
            {
                Add(DefinitionErrorCodes.Parse, path + ".parallel.branches", $"A parallel group needs at least {ParallelDefinition.MinBranches} branches.");
            }

            var producedBy = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var b = 0; b < parallel.Branches.Count; b++)
            {
                if (parallel.Branches[b].Count == 0)
                {
                    Add(DefinitionErrorCodes.Parse, $"{path}.parallel.branches[{b}]", "A branch needs at least one stage.");
                }

                for (var k = 0; k < parallel.Branches[b].Count; k++)
                {
                    foreach (var output in Outputs(parallel.Branches[b][k]))
                    {
                        // Two branches writing one artifact would race on its versions: refused like a cross-branch read.
                        if (!producedBy.TryAdd(output, b) && producedBy[output] != b)
                        {
                            Add(
                                DefinitionErrorCodes.BranchInput,
                                BranchStagePath(path, b, k),
                                $"Artifact '{output}' is already produced by branch {producedBy[output]}; parallel branches can't share an output.");
                        }
                    }
                }
            }

            for (var b = 0; b < parallel.Branches.Count; b++)
            {
                for (var k = 0; k < parallel.Branches[b].Count; k++)
                {
                    var stage = parallel.Branches[b][k];
                    var at = BranchStagePath(path, b, k);
                    ValidateBranchStage(stage, at, seen);
                    CheckBranchInputs(stage.Inputs, $"{at}.inputs", b, producedBy);
                    for (var j = 0; j < stage.Agents.Count; j++)
                    {
                        CheckBranchInputs(stage.Agents[j].Inputs, $"{at}.agents[{j}].inputs", b, producedBy);
                    }
                }
            }
        }

        private static IEnumerable<string> Outputs(StageDefinition stage) =>
            stage.WithBranchStages().SelectMany(s => s.Agents.Select(a => a.Output).Prepend(s.Output)).OfType<string>();

        private void CheckBranchInputs(IReadOnlyList<string> inputs, string path, int branch, Dictionary<string, int> producedBy)
        {
            for (var j = 0; j < inputs.Count; j++)
            {
                if (producedBy.TryGetValue(inputs[j], out var producer) && producer != branch)
                {
                    Add(DefinitionErrorCodes.BranchInput, $"{path}[{j}]",
                        $"Artifact '{inputs[j]}' is produced by branch {producer + 1} of the same parallel group; branches can't read each other's outputs.");
                }
            }
        }

        /// <summary>FR-343: a branch stage is an ordinary stage, without transitions: it runs after the previous stage of its
        /// branch, and the group joins at the end.</summary>
        private void ValidateBranchStage(StageDefinition stage, string path, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(stage.Id))
            {
                Add(DefinitionErrorCodes.Parse, path + ".id", "'id' is required.");
            }
            else if (!seen.Add(stage.Id) || _stageIds.Contains(stage.Id))
            {
                Add(DefinitionErrorCodes.DuplicateId, path + ".id", $"Stage '{stage.Id}' is declared twice.");
            }
            else
            {
                CheckName(stage.Id, path + ".id", "Stage id");
            }

            if (string.IsNullOrWhiteSpace(stage.Title))
            {
                Add(DefinitionErrorCodes.Parse, path + ".title", "'title' is required.");
            }

            if (string.IsNullOrEmpty(stage.Status))
            {
                Add(DefinitionErrorCodes.Parse, path + ".status", "'status' is required.");
            }
            else
            {
                CheckStatus(stage.Status, path + ".status");
            }

            var notAllowed = new[]
            {
                (stage.Parallel is not null, "parallel"), (stage.OnSuccess is not null, "on_success"), (stage.OnReject is not null, "on_reject"),
                (stage.Loop is not null, "loop"), (stage.Routes.Count > 0, "routes"), (stage.OnVeto?.To is not null, "on_veto.to"),
                (stage.Aggregate is not null, "aggregate"), (stage.Gate is not null, "gate"), (stage.Veto.Count > 0, "veto"),

                // FR-355, FR-356: map-reduce and conversations run at the top level only.
                (stage.Map is not null, "map"), (stage.Reduce is not null, "reduce"), (stage.Conversation is not null, "conversation"),
            };
            foreach (var (_, key) in notAllowed.Where(n => n.Item1))
            {
                Add(DefinitionErrorCodes.Parse, $"{path}.{key}", $"'{key}' isn't allowed on a stage inside a parallel branch.");
            }

            ValidateKind(stage, path);
            ValidateArtifactsOf(stage, path);
            ValidateRequirements(stage.Requires, path + ".requires");
            ValidateGuards(stage, path);
            if (stage.Parallel is null)
            {
                ValidateFlow(stage, InBranch, path, seen);
            }
        }

        /// <summary>FR-344: each entry is a bound guard or an agent the stage binds; <c>on_veto</c> is required with a veto (and
        /// only then), and its <c>to</c> is an earlier stage (<see cref="DefinitionErrorCodes.RouteTarget"/>).</summary>
        private void ValidateVeto(StageDefinition stage, int index, string path)
        {
            for (var k = 0; k < stage.Veto.Count; k++)
            {
                var veto = stage.Veto[k];
                var at = $"{path}.veto[{k}]";
                if ((veto.Guard is null) == (veto.Agent is null))
                {
                    Add(DefinitionErrorCodes.Parse, at, "A veto needs exactly one of 'guard' and 'agent'.");
                }
                else if (veto.Guard is { } guard)
                {
                    CheckGuard(guard, at + ".guard");
                }
                else
                {
                    CheckBoundAgent(stage, veto.Agent, at + ".agent");
                }
            }

            if (stage.Veto.Count > 0 && stage.OnVeto is null)
            {
                Add(DefinitionErrorCodes.Parse, path + ".on_veto", "A stage with 'veto' needs 'on_veto': '{ to: <earlier stage> }' or '{ end: vetoed }'.");
            }
            else if (stage.Veto.Count == 0 && stage.OnVeto is not null)
            {
                Add(DefinitionErrorCodes.Parse, path + ".on_veto", "'on_veto' needs a 'veto' list.");
            }

            if (stage.OnVeto?.To is { } to && index != InBranch)
            {
                CheckEarlierTarget(to, index, path + ".on_veto.to", "on_veto.to");
            }
        }

        /// <summary>FR-344, FR-347: <paramref name="target"/> is a stage before position <paramref name="index"/>.</summary>
        private void CheckEarlierTarget(string target, int index, string path, string what)
        {
            if (string.IsNullOrEmpty(target))
            {
                Add(DefinitionErrorCodes.Parse, path, "A stage id is required.");
                return;
            }

            var targetIndex = definition.IndexOfStage(target);
            if (targetIndex < 0 || targetIndex >= index)
            {
                var stageId = definition.Stages[index].Id;
                Add(DefinitionErrorCodes.RouteTarget, path, targetIndex < 0
                    ? $"'{what}' names '{target}', which is not a stage of this pipeline."
                    : $"'{what}' must be an earlier stage than '{stageId}', not '{target}'.");
            }
        }

        /// <summary>FR-345: <c>count</c> 2–10, or 2–10 distinct agents the stage binds in <c>agents:</c>.</summary>
        private void ValidateFanOut(StageDefinition stage, string path)
        {
            if (stage.FanOut is not { } fanOut)
            {
                return;
            }

            // A fan-out stage's join has no route to read (its branches' outputs are many) and no resume path for an
            // escalated branch yet.
            if (stage.Routes.Count > 0)
            {
                Add(DefinitionErrorCodes.StageKind, path + ".routes", $"Fan-out stage '{stage.Id}' can't declare 'routes'.");
            }

            if (stage.OnInvalid is { Then: InvalidOutcome.Escalate })
            {
                Add(DefinitionErrorCodes.StageKind, path + ".on_invalid.then", $"Fan-out stage '{stage.Id}' can't use 'on_invalid.then: escalate'; use 'fail'.");
            }

            if (fanOut.Count is { } count)
            {
                if (count is < FanOutDefinition.MinBranches or > FanOutDefinition.MaxBranches)
                {
                    Add(DefinitionErrorCodes.Parse, path + ".fan_out.count", $"'count' must be {FanOutDefinition.MinBranches}–{FanOutDefinition.MaxBranches}.");
                }

                return;
            }

            if (fanOut.Agents.Count is < FanOutDefinition.MinBranches or > FanOutDefinition.MaxBranches)
            {
                Add(DefinitionErrorCodes.Parse, path + ".fan_out.agents", $"'agents' needs {FanOutDefinition.MinBranches}–{FanOutDefinition.MaxBranches} agent keys.");
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var a = 0; a < fanOut.Agents.Count; a++)
            {
                var at = $"{path}.fan_out.agents[{a}]";
                if (!string.IsNullOrEmpty(fanOut.Agents[a]) && !keys.Add(fanOut.Agents[a]))
                {
                    Add(DefinitionErrorCodes.DuplicateId, at, $"Agent '{fanOut.Agents[a]}' is listed twice.");
                }
                else
                {
                    CheckBoundAgent(stage, fanOut.Agents[a], at);
                }
            }
        }

        /// <summary>FR-344, FR-345, FR-349: <paramref name="key"/> is bound by the stage's <c>agents:</c>
        /// (<see cref="DefinitionErrorCodes.FanOutAgent"/>).</summary>
        private void CheckBoundAgent(StageDefinition stage, string? key, string path)
        {
            if (string.IsNullOrEmpty(key))
            {
                Add(DefinitionErrorCodes.Parse, path, "An agent key is required.");
            }
            else if (!stage.Agents.Any(b => string.Equals(b.Key, key, StringComparison.Ordinal)))
            {
                Add(DefinitionErrorCodes.FanOutAgent, path, $"Agent '{key}' is not bound by stage '{stage.Id}'; declare it under 'agents:'.");
            }
        }

        /// <summary>FR-346: <c>from</c> is an earlier fan-out stage or parallel group; the disagreement guard is bound.</summary>
        private void ValidateAggregate(StageDefinition stage, int index, string path)
        {
            if (stage.Aggregate is not { } aggregate || index == InBranch)
            {
                return;
            }

            var at = path + ".aggregate.from";
            if (CheckStageRef(aggregate.From, at, required: true))
            {
                var fromIndex = definition.IndexOfStage(aggregate.From);
                var source = definition.Stages[fromIndex];
                if (fromIndex >= index)
                {
                    Add(DefinitionErrorCodes.Parse, at, $"'aggregate.from' must be an earlier stage than '{stage.Id}', not '{aggregate.From}'.");
                }
                else if (source.FanOut is null && source.Parallel is null)
                {
                    Add(DefinitionErrorCodes.Parse, at, $"Stage '{aggregate.From}' has no 'fan_out' or 'parallel' to aggregate.");
                }
            }

            if (aggregate.DisagreementGuard is { } guard)
            {
                CheckGuard(guard, path + ".aggregate.disagreement.guard");
            }
        }

        /// <summary>FR-347: every route is a distinct earlier stage (<see cref="DefinitionErrorCodes.RouteTarget"/>).</summary>
        private void ValidateRoutes(StageDefinition stage, int index, string path)
        {
            if (index == InBranch)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var r = 0; r < stage.Routes.Count; r++)
            {
                var at = $"{path}.routes[{r}]";
                if (!string.IsNullOrEmpty(stage.Routes[r]) && !seen.Add(stage.Routes[r]))
                {
                    Add(DefinitionErrorCodes.DuplicateId, at, $"Route '{stage.Routes[r]}' is listed twice.");
                }
                else
                {
                    CheckEarlierTarget(stage.Routes[r], index, at, "routes");
                }
            }
        }

        /// <summary>FR-349: <c>count</c> 2–5; an agent selector is bound by the stage's <c>agents:</c>.</summary>
        private void ValidateVariants(StageDefinition stage, string path)
        {
            if (stage.Variants is not { } variants)
            {
                return;
            }

            if (variants.Count is < VariantsDefinition.MinCount or > VariantsDefinition.MaxCount)
            {
                Add(DefinitionErrorCodes.Parse, path + ".variants.count", $"'count' must be {VariantsDefinition.MinCount}–{VariantsDefinition.MaxCount}.");
            }

            if (variants.SelectAgent is { } agent)
            {
                CheckBoundAgent(stage, agent, path + ".variants.select.agent");
            }
        }
    }
}
