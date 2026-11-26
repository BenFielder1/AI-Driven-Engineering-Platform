using System.Text.RegularExpressions;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Engine.Domain.Pipelines;

public static partial class PipelineDefinitionValidator
{
    /// <summary>FR-355: an array path, dot-separated field names.</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPathPattern();

    /// <summary>FR-355–FR-357 (0.8.0, §4.49 amended, §4.68, §4.69): map-reduce, conversation and <c>from_runs</c> checks.</summary>
    private sealed partial class Run
    {
        /// <summary>
        /// FR-355: a map stage has no <c>agent</c> or <c>handler</c> of its own (<see cref="DefinitionErrorCodes.StageKind"/>)
        /// and an <c>output</c> (the item collection); <c>map</c> and <c>reduce</c> each name exactly one of a bound agent and
        /// a registered handler; the limits are in range; <c>over</c> names a JSON artifact the stage can read
        /// (<see cref="DefinitionErrorCodes.MapSource"/>). <c>reduce</c> needs <c>map</c>.
        /// </summary>
        private void ValidateMap(StageDefinition stage, int index, string path)
        {
            if (index == InBranch)
            {
                return; // refused on a branch stage already
            }

            if (stage.Map is not { } map)
            {
                if (stage.Reduce is not null)
                {
                    Add(DefinitionErrorCodes.StageKind, path + ".reduce", $"'reduce' needs 'map' on the same stage; '{stage.Id}' has none.");
                }

                return;
            }

            if (stage.Agent is not null || stage.Handler is not null)
            {
                Add(DefinitionErrorCodes.StageKind, path + ".map", $"Map stage '{stage.Id}' can't have an 'agent' or a 'handler'; 'map' names the item agent or handler.");
            }

            if (string.IsNullOrEmpty(stage.Output))
            {
                Add(DefinitionErrorCodes.Parse, path + ".output", $"Map stage '{stage.Id}' needs an 'output': the item outputs are stored as its versions.");
            }

            CheckWorker(stage, map.Agent, map.Handler, path + ".map", "map");
            if (stage.Reduce is { } reduce)
            {
                CheckWorker(stage, reduce.Agent, reduce.Handler, path + ".reduce", "reduce");
            }

            if (map.MaxParallel is < MapDefinition.MinMaxParallel or > MapDefinition.MaxMaxParallel)
            {
                Add(DefinitionErrorCodes.Parse, path + ".map.max_parallel", $"'max_parallel' must be {MapDefinition.MinMaxParallel}–{MapDefinition.MaxMaxParallel}.");
            }

            if (map.MaxItems is < MapDefinition.MinMaxItems or > MapDefinition.MaxMaxItems)
            {
                Add(DefinitionErrorCodes.Parse, path + ".map.max_items", $"'max_items' must be {MapDefinition.MinMaxItems}–{MapDefinition.MaxMaxItems}.");
            }

            if (map.MaxFailed.IsPercent && map.MaxFailed.Value > 100)
            {
                Add(DefinitionErrorCodes.Parse, path + ".map.max_failed", "A 'max_failed' percentage must be 0%–100%.");
            }

            CheckMapSource(stage, map, index, path + ".map.over");
        }

        /// <summary>FR-355: exactly one of <paramref name="agent"/> (bound by the stage) and <paramref name="handler"/> (registered).</summary>
        private void CheckWorker(StageDefinition stage, string? agent, string? handler, string path, string what)
        {
            if ((agent is null) == (handler is null))
            {
                Add(DefinitionErrorCodes.Parse, path, $"'{what}' needs exactly one of 'agent' and 'handler'.");
            }
            else if (agent is not null)
            {
                CheckBoundAgent(stage, agent, path + ".agent");
            }
            else if (string.IsNullOrEmpty(handler))
            {
                Add(DefinitionErrorCodes.Parse, path + ".handler", "A handler name is required.");
            }
            else if (!_handlers.Contains(handler))
            {
                Add(DefinitionErrorCodes.UnknownHandler, path + ".handler", $"No pack registered stage handler '{handler}'.");
            }
        }

        /// <summary>FR-355: <c>over</c> is <c>&lt;artifact key&gt;[.&lt;array path&gt;]</c>, the artifact a declared JSON
        /// artifact that is one of the stage's inputs or an output of an earlier stage.</summary>
        private void CheckMapSource(StageDefinition stage, MapDefinition map, int index, string path)
        {
            var key = map.SourceArtifact;
            if (string.IsNullOrEmpty(map.Over) || string.IsNullOrEmpty(key))
            {
                Add(DefinitionErrorCodes.MapSource, path, "'over' is required: '<artifact key>.<array path>'.");
                return;
            }

            if (map.Over.Contains('.', StringComparison.Ordinal) && !ItemPathPattern().IsMatch(map.ItemPath))
            {
                Add(DefinitionErrorCodes.MapSource, path, $"'{map.ItemPath}' is not an array path: field names separated by '.'.");
                return;
            }

            if (definition.FindArtifact(key) is not { } artifact)
            {
                Add(DefinitionErrorCodes.MapSource, path, $"'over' names artifact '{key}', which is not declared in 'artifacts'.");
                return;
            }

            if (artifact.Format != ArtifactFormat.Json)
            {
                Add(DefinitionErrorCodes.MapSource, path, $"'over' needs a JSON artifact; '{key}' is {artifact.Format.ToString().ToLowerInvariant()}.");
                return;
            }

            var earlierOutputs = definition.Stages.Take(index)
                .SelectMany(s => s.WithBranchStages())
                .SelectMany(s => s.Agents.Select(a => a.Output).Prepend(s.Output))
                .OfType<string>();
            if (!stage.Inputs.Contains(key, StringComparer.Ordinal) && !earlierOutputs.Contains(key, StringComparer.Ordinal))
            {
                Add(DefinitionErrorCodes.MapSource, path, $"Stage '{stage.Id}' can't read artifact '{key}': list it in 'inputs' or produce it in an earlier stage.");
            }
        }

        /// <summary>
        /// FR-356, §4.69: a conversation stage has no <c>agent</c> or <c>handler</c> of its own
        /// (<see cref="DefinitionErrorCodes.StageKind"/>) and an <c>output</c> (the transcript); 2–6 participants, each a bound
        /// agent (<see cref="DefinitionErrorCodes.FanOutAgent"/>) with a role, no role or agent twice, and the opener one of
        /// the roles (<see cref="DefinitionErrorCodes.ConversationParticipants"/>); <c>max_turns</c> 1–100; a <c>stop</c>
        /// names exactly one of a registered guard and a non-empty phrase.
        /// </summary>
        private void ValidateConversation(StageDefinition stage, int index, string path)
        {
            if (stage.Conversation is not { } conversation || index == InBranch)
            {
                return; // a branch stage's conversation is refused already
            }

            var at = path + ".conversation";
            if (stage.Agent is not null || stage.Handler is not null)
            {
                Add(DefinitionErrorCodes.StageKind, at, $"Conversation stage '{stage.Id}' can't have an 'agent' or a 'handler'; its participants do the work.");
            }

            if (string.IsNullOrEmpty(stage.Output))
            {
                Add(DefinitionErrorCodes.Parse, path + ".output", $"Conversation stage '{stage.Id}' needs an 'output': the transcript.");
            }

            var participants = conversation.Participants;
            if (participants.Count is < ConversationDefinition.MinParticipants or > ConversationDefinition.MaxParticipants)
            {
                Add(DefinitionErrorCodes.ConversationParticipants, at + ".participants",
                    $"A conversation needs {ConversationDefinition.MinParticipants}–{ConversationDefinition.MaxParticipants} participants, not {participants.Count}.");
            }

            var roles = new HashSet<string>(StringComparer.Ordinal);
            var agents = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 0; p < participants.Count; p++)
            {
                var participant = participants[p];
                var itemPath = $"{at}.participants[{p}]";
                if (string.IsNullOrEmpty(participant.Role))
                {
                    Add(DefinitionErrorCodes.ConversationParticipants, itemPath + ".role", "Each participant needs a 'role'.");
                }
                else if (!roles.Add(participant.Role))
                {
                    Add(DefinitionErrorCodes.ConversationParticipants, itemPath + ".role", $"Role '{participant.Role}' is used by two participants.");
                }

                if (!string.IsNullOrEmpty(participant.Agent) && !agents.Add(participant.Agent))
                {
                    Add(DefinitionErrorCodes.ConversationParticipants, itemPath + ".agent", $"Agent '{participant.Agent}' takes part twice; each participant is its own agent.");
                }
                else
                {
                    CheckBoundAgent(stage, participant.Agent, itemPath + ".agent");
                }
            }

            // FR-356: a participant's {{TRANSCRIPT}} and {{ROLE}} are the engine's; an input keyed to the same placeholder
            // (transcript, role) would be silently replaced, so it is refused, on the stage or on a participant's binding.
            foreach (var key in ConversationRules.ReservedInputs(stage.Inputs))
            {
                Add(DefinitionErrorCodes.ConversationParticipants, path + ".inputs",
                    $"Input '{key}' fills a placeholder the conversation reserves for its participants ({{{{TRANSCRIPT}}}}, {{{{ROLE}}}}).");
            }

            for (var b = 0; b < stage.Agents.Count; b++)
            {
                var binding = stage.Agents[b];
                if (!agents.Contains(binding.Key))
                {
                    continue;
                }

                // A binding's inputs default to the stage's, which are reported once above.
                foreach (var key in ConversationRules.ReservedInputs(binding.Inputs.Except(stage.Inputs, StringComparer.Ordinal)))
                {
                    Add(DefinitionErrorCodes.ConversationParticipants, $"{path}.agents[{b}].inputs",
                        $"Input '{key}' fills a placeholder the conversation reserves for its participants ({{{{TRANSCRIPT}}}}, {{{{ROLE}}}}).");
                }
            }

            if (string.IsNullOrEmpty(conversation.Opener) || !roles.Contains(conversation.Opener))
            {
                Add(DefinitionErrorCodes.ConversationParticipants, at + ".opener", string.IsNullOrEmpty(conversation.Opener)
                    ? "'opener' is required: the role that speaks first."
                    : $"'opener' '{conversation.Opener}' is not one of the participants' roles.");
            }

            if (conversation.TurnLimit is < ConversationDefinition.MinTurns or > ConversationDefinition.MaxTurns)
            {
                Add(DefinitionErrorCodes.Parse, at + ".max_turns", $"'max_turns' must be {ConversationDefinition.MinTurns}–{ConversationDefinition.MaxTurns}.");
            }

            if (conversation.Stop is { } stop)
            {
                if ((stop.Guard is null) == (stop.Phrase is null))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".stop", "'stop' needs exactly one of 'guard' and 'phrase'.");
                }
                else if (stop.Guard is { } guard)
                {
                    CheckGuard(guard, at + ".stop.guard");
                }
                else if (string.IsNullOrWhiteSpace(stop.Phrase))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".stop.phrase", "The stop phrase can't be empty.");
                }
            }
        }

        /// <summary>
        /// FR-357: each <c>from_runs</c> input names a kebab-case pipeline id and an artifact key (a declared artifact when the
        /// pipeline is this one), a <c>window</c> that is a positive duration (<c>7d</c>, <c>12h</c>, <c>30m</c>, as
        /// follow-ups' <c>after</c>) and a final run <c>status</c>.
        /// </summary>
        private void ValidateFromRuns(StageDefinition stage, string path)
        {
            foreach (var runs in stage.FromRuns)
            {
                var at = $"{path}.inputs[{runs.Position}].from_runs";
                if (string.IsNullOrEmpty(runs.Pipeline))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".pipeline", "'pipeline' is required.");
                }
                else if (!PipelineIdPattern().IsMatch(runs.Pipeline))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".pipeline", $"Pipeline id '{runs.Pipeline}' must be kebab-case.");
                }

                if (string.Equals(runs.Pipeline, definition.Pipeline, StringComparison.Ordinal))
                {
                    CheckArtifactRef(runs.Artifact, at + ".artifact");
                }
                else if (string.IsNullOrEmpty(runs.Artifact))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".artifact", "'artifact' is required.");
                }
                else
                {
                    CheckName(runs.Artifact, at + ".artifact", "Artifact key");
                }

                if (string.IsNullOrEmpty(runs.Window))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".window", "'window' is required: a duration such as 7d.");
                }
                else if (FollowUpRules.ParseDuration(runs.Window) is null)
                {
                    Add(DefinitionErrorCodes.Parse, at + ".window", $"'{runs.Window}' is not a duration like 7d, 12h or 30m greater than zero.");
                }

                if (runs.Status is not (GenericRunStatus.Completed or GenericRunStatus.Failed or GenericRunStatus.Cancelled))
                {
                    Add(DefinitionErrorCodes.Parse, at + ".status", $"'status' must be a final run status (completed, failed or cancelled), not '{runs.Status.ToWire()}'.");
                }
            }
        }
    }
}
