using System.Text.RegularExpressions;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-276, §4.49: checks a parsed definition against a <see cref="PackCatalog"/> and returns every problem at once, each
/// with its code and YAML path. Pure. Values the parser couldn't read arrive empty; they're reported here as required
/// (<see cref="DefinitionErrorCodes.Parse"/>) and skipped by the reference checks, and
/// <see cref="DefinitionErrorCodes.Merge"/> drops a report the parser already made at the same path.
/// </summary>
public static partial class PipelineDefinitionValidator
{
    public const int MinAttempts = 1;
    public const int MaxAttempts = 10;
    public const int MinIterations = 1;
    public const int MaxIterations = 100;

    public static IReadOnlyList<DefinitionError> Validate(PipelineDefinition definition, PackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);
        return new Run(definition, catalog).Execute();
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PipelineIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();

    private sealed partial class Run(PipelineDefinition definition, PackCatalog catalog)
    {
        private readonly List<DefinitionError> _errors = [];
        private readonly HashSet<string> _stageIds = new(definition.Stages.Select(s => s.Id), StringComparer.Ordinal);

        /// <summary>FR-343: every stage with its YAML path, each parallel group followed by its branch stages.</summary>
        private readonly List<(StageDefinition Stage, string Path)> _allStages = AllStagesWithPaths(definition);
        private readonly HashSet<string> _artifactKeys = new(definition.Artifacts.Select(a => a.Key), StringComparer.Ordinal);
        private readonly HashSet<string> _statuses = new(
            definition.Statuses.Select(s => s.Name).Concat(EnumStrings.Values<GenericRunStatus>()), StringComparer.Ordinal);
        private readonly HashSet<string> _settings = new(definition.Settings, StringComparer.Ordinal);
        private readonly HashSet<string> _guards = new(catalog.Guards, StringComparer.Ordinal);
        private readonly HashSet<string> _handlers = new(catalog.Handlers, StringComparer.Ordinal);
        private readonly HashSet<string> _tools = new(catalog.Tools, StringComparer.Ordinal);
        private readonly HashSet<string> _registeredSettings = new(catalog.Settings, StringComparer.Ordinal);
        private readonly HashSet<string> _schemas = new(catalog.Schemas, StringComparer.Ordinal);
        private readonly HashSet<string> _gateTypes = new(
            definition.AllStages.Select(s => s.Gate?.Type).OfType<string>().Append(EngineGates.Escalation), StringComparer.Ordinal);

        public List<DefinitionError> Execute()
        {
            ValidateHeader();
            ValidateArtifacts();
            ValidateStatuses();
            ValidateStatusChanges();
            ValidateConcurrency();
            ValidateLimits();
            ValidateStages();
            ValidateAgentBindings();
            ValidateStageHandlers();
            ValidateStart();
            ValidateReachability();
            ValidateFollowUps();
            ValidateProducers();
            return _errors;
        }

        private void Add(string code, string path, string message) => _errors.Add(new DefinitionError(code, path, message));

        private void ValidateHeader()
        {
            if (string.IsNullOrEmpty(definition.Pipeline))
            {
                Add(DefinitionErrorCodes.Parse, "pipeline", "'pipeline' is required.");
            }
            else if (!PipelineIdPattern().IsMatch(definition.Pipeline))
            {
                Add(DefinitionErrorCodes.Parse, "pipeline", $"Pipeline id '{definition.Pipeline}' must be kebab-case.");
            }

            if (definition.Version < 1)
            {
                Add(DefinitionErrorCodes.Parse, "version", "'version' is required and must be a positive integer.");
            }

            if (string.IsNullOrWhiteSpace(definition.Title))
            {
                Add(DefinitionErrorCodes.Parse, "title", "'title' is required.");
            }

            ValidateTrigger();

            if (definition.ApprovalModes.Count == 0)
            {
                Add(DefinitionErrorCodes.Parse, "approval_modes", "'approval_modes' needs at least one mode.");
            }

            var seenModes = new HashSet<ApprovalMode>();
            for (var i = 0; i < definition.ApprovalModes.Count; i++)
            {
                if (!seenModes.Add(definition.ApprovalModes[i]))
                {
                    Add(DefinitionErrorCodes.DuplicateId, $"approval_modes[{i}]", $"Approval mode '{definition.ApprovalModes[i].ToWire()}' is listed twice.");
                }
            }

            if (definition.DefaultApprovalMode is not { } mode)
            {
                Add(DefinitionErrorCodes.Parse, "default_approval_mode", "'default_approval_mode' is required.");
            }
            else if (definition.ApprovalModes.Count > 0 && !definition.ApprovalModes.Contains(mode))
            {
                Add(DefinitionErrorCodes.Parse, "default_approval_mode", $"Default approval mode '{mode.ToWire()}' is not in 'approval_modes'.");
            }

            var seenSettings = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.Settings.Count; i++)
            {
                var setting = definition.Settings[i];
                var path = $"settings[{i}]";
                if (string.IsNullOrEmpty(setting))
                {
                    Add(DefinitionErrorCodes.Parse, path, "A setting name is required.");
                }
                else if (!seenSettings.Add(setting))
                {
                    Add(DefinitionErrorCodes.DuplicateId, path, $"Setting '{setting}' is listed twice.");
                }
                else if (!_registeredSettings.Contains(setting))
                {
                    Add(DefinitionErrorCodes.UnknownSetting, path, $"No pack provides setting '{setting}'.");
                }
            }

            if (definition.RollbackStatus is { } rollback)
            {
                CheckStatus(rollback, "rollback_status");
            }
        }

        private void ValidateArtifacts()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in definition.Artifacts)
            {
                var path = $"artifacts.{artifact.Key}";
                if (!seen.Add(artifact.Key))
                {
                    Add(DefinitionErrorCodes.DuplicateId, path, $"Artifact '{artifact.Key}' is declared twice.");
                    continue;
                }

                CheckName(artifact.Key, path, "Artifact key");

                // FR-352: a project-scope artifact is pinned from the project's history, not supplied to one run.
                if (artifact.Scope == ArtifactScope.Project && artifact.Input)
                {
                    Add(DefinitionErrorCodes.ScopeInput, path + ".scope", $"Artifact '{artifact.Key}' can't be both 'input: true' and 'scope: project'.");
                }
                if (string.IsNullOrWhiteSpace(artifact.Title))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".title", "'title' is required.");
                }

                if (artifact.Schema is { } schema)
                {
                    if (!_schemas.Contains(schema))
                    {
                        Add(DefinitionErrorCodes.SchemaNotFound, path + ".schema", $"The pack doesn't embed schema '{schema}'.");
                    }
                    else if (catalog.InvalidSchemas.TryGetValue(schema, out var reason))
                    {
                        Add(DefinitionErrorCodes.SchemaNotFound, path + ".schema", $"Schema '{schema}' isn't valid JSON Schema: {reason}");
                    }
                }
            }
        }

        private void ValidateStatuses()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var status in definition.Statuses)
            {
                var path = $"statuses.{status.Name}";
                if (!seen.Add(status.Name))
                {
                    Add(DefinitionErrorCodes.DuplicateId, path, $"Status '{status.Name}' is declared twice.");
                    continue;
                }

                CheckName(status.Name, path, "Status name");
            }
        }

        private void ValidateStatusChanges()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in definition.StatusChanges)
            {
                var path = $"status_changes.{change.Name}";
                if (!seen.Add(change.Name))
                {
                    Add(DefinitionErrorCodes.DuplicateId, path, $"Status change '{change.Name}' is declared twice.");
                    continue;
                }

                CheckName(change.Name, path, "Status change name");
                CheckStageRef(change.Stage, path + ".stage", required: true);
                if (change.From.Count == 0)
                {
                    Add(DefinitionErrorCodes.Parse, path + ".from", "'from' needs at least one status.");
                }

                for (var i = 0; i < change.From.Count; i++)
                {
                    CheckStatus(change.From[i], $"{path}.from[{i}]");
                }

                CheckStatus(change.To, path + ".to");
                if (change.WrongStageMessage is { } wrongStage && string.IsNullOrWhiteSpace(wrongStage))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".wrong_stage_message", "'wrong_stage_message' can't be empty.");
                }

                if (string.IsNullOrEmpty(change.ErrorCode))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".error_code", "'error_code' is required.");
                }
                else if (!ErrorCodePattern().IsMatch(change.ErrorCode))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".error_code", $"Error code '{change.ErrorCode}' must be UPPER_SNAKE_CASE.");
                }
            }
        }

        /// <summary>
        /// FR-284, FR-336, FR-337: a <c>schedule</c> or <c>webhook</c> trigger takes only <c>input</c> and <c>on_active</c>,
        /// and a <c>manual</c> trigger takes no option; any other option is <see cref="DefinitionErrorCodes.TriggerNotSupported"/>.
        /// A webhook needs <c>input</c>; <c>input</c> names an artifact declared <c>input: true</c>. FR-339: each entry of a
        /// <c>triggers:</c> list follows the same rules; the list is not empty and has at most one webhook and one schedule.
        /// </summary>
        private void ValidateTrigger()
        {
            if (definition.Triggers is not { } triggers)
            {
                ValidateTrigger(definition.Trigger, "trigger");
                return;
            }

            if (triggers.Count == 0)
            {
                Add(DefinitionErrorCodes.Parse, "triggers", "'triggers' needs at least one trigger.");
            }

            for (var i = 0; i < triggers.Count; i++)
            {
                var path = $"triggers[{i}]";
                var kind = triggers[i].Kind;
                if (kind is TriggerKind.Schedule or TriggerKind.Webhook && triggers.Take(i).Any(t => t.Kind == kind))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".kind", $"A pipeline declares at most one {kind.ToWire()} trigger.");
                }

                ValidateTrigger(triggers[i], path);
            }
        }

        private void ValidateTrigger(PipelineTrigger trigger, string path)
        {
            var triggered = trigger.Kind is TriggerKind.Schedule or TriggerKind.Webhook;
            foreach (var key in trigger.Options.Keys)
            {
                var known = key is PipelineTrigger.InputOption or PipelineTrigger.OnActiveOption;
                if (!triggered)
                {
                    Add(DefinitionErrorCodes.TriggerNotSupported, $"{path}.{key}",
                        $"A '{trigger.Kind.ToWire()}' trigger takes no options; '{key}' needs a schedule or webhook trigger.");
                }
                else if (!known)
                {
                    Add(DefinitionErrorCodes.TriggerNotSupported, $"{path}.{key}",
                        $"Trigger option '{key}' is not supported; a {trigger.Kind.ToWire()} trigger takes 'input' and 'on_active'.");
                }
            }

            if (!triggered)
            {
                return;
            }

            if (trigger.Options.TryGetValue(PipelineTrigger.OnActiveOption, out var onActive) &&
                !EnumStrings.TryParse(onActive, out TriggerOnActive _))
            {
                Add(DefinitionErrorCodes.Parse, $"{path}.on_active", $"'on_active' must be 'submit' or 'skip', not '{onActive}'.");
            }

            if (trigger.Input is not { } input)
            {
                if (trigger.Kind == TriggerKind.Webhook)
                {
                    Add(DefinitionErrorCodes.Parse, $"{path}.input", "A webhook trigger needs 'input': the artifact its body is stored as.");
                }

                return;
            }

            CheckInputArtifact(input, $"{path}.input");
        }

        /// <summary>FR-335: <paramref name="key"/> is a declared artifact marked <c>input: true</c>.</summary>
        private void CheckInputArtifact(string key, string path)
        {
            if (string.IsNullOrEmpty(key) || !_artifactKeys.Contains(key))
            {
                CheckArtifactRef(key, path);
            }
            else if (definition.FindArtifact(key) is { Input: false })
            {
                Add(DefinitionErrorCodes.Parse, path, $"Artifact '{key}' is not an input; declare it with 'input: true'.");
            }
        }

        private void ValidateConcurrency()
        {
            var exclusive = definition.Concurrency.Exclusive;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < exclusive.Count; i++)
            {
                var path = $"concurrency.exclusive[{i}].job_type";
                if (!string.IsNullOrEmpty(exclusive[i].JobType) && !seen.Add(exclusive[i].JobType))
                {
                    Add(DefinitionErrorCodes.DuplicateId, path, $"Job type '{exclusive[i].JobType}' is listed twice.");
                    continue;
                }

                CheckJobType(exclusive[i].JobType, path);

                // D-0.7.0-37: jobs carry no run id, so run-scoped exclusivity can't be enforced yet.
                if (exclusive[i].Scope == ConcurrencyScope.Run)
                {
                    Add(DefinitionErrorCodes.Parse, $"concurrency.exclusive[{i}].scope", "run-scoped concurrency is not supported in 0.7.0");
                }
            }
        }

        private void ValidateStages()
        {
            if (definition.Stages.Count == 0)
            {
                Add(DefinitionErrorCodes.Parse, "stages", "'stages' needs at least one stage.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.Stages.Count; i++)
            {
                var stage = definition.Stages[i];
                var path = $"stages[{i}]";
                if (string.IsNullOrEmpty(stage.Id))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".id", "'id' is required.");
                }
                else if (!seen.Add(stage.Id))
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

                ValidateKind(stage, path);
                ValidateArtifactsOf(stage, path);
                ValidateRequirements(stage.Requires, path + ".requires");
                ValidateGuards(stage, path);
                ValidateGate(stage, path);
                ValidateTransitions(stage, i, path);
                ValidateFlow(stage, i, path, seen);
            }
        }

        /// <summary>FR-295, D-0.7.0-33: an agent stage's prompt (<c>agent.prompt</c>, else the agent key) must be one the
        /// pack registered, and its required placeholders must be filled by the stage's inputs or the pack's context
        /// (<see cref="AgentPromptPlaceholders.Problem"/>); skipped when the catalog doesn't list prompts
        /// (<see cref="PackCatalog.Prompts"/> null).</summary>
        /// <summary>FR-354: a follow-up stage's prompt also gets its label as <c>{{VARIANT}}</c> and the run's outcome scores
        /// as <c>{{OUTCOME_SCORES}}</c>.</summary>
        private IReadOnlyCollection<string>? WithFollowUp(StageDefinition stage, IReadOnlyCollection<string>? supplied) =>
            FollowUpRules.IsFollowUpStage(definition, stage.Id)
                ? [.. supplied ?? [], VariantRules.LabelPlaceholder, FollowUpRules.OutcomeScoresPlaceholder]
                : supplied;

        private void CheckPrompt(StageDefinition stage, AgentStageDefinition agent, string path, IReadOnlyCollection<string>? supplied = null)
        {
            if (catalog.Prompts is not { } prompts || string.IsNullOrEmpty(agent.Key))
            {
                return;
            }

            var name = agent.Prompt ?? agent.Key;
            var at = agent.Prompt is null ? path + ".key" : path + ".prompt";
            if (!prompts.TryGetValue(name, out var source))
            {
                Add(DefinitionErrorCodes.UnknownPrompt, at, $"No pack registered prompt '{name}'.");
            }
            else if (AgentPromptPlaceholders.Problem(stage, source, catalog.ContextNames, WithFollowUp(stage, supplied)) is { } problem)
            {
                Add(DefinitionErrorCodes.UnknownPrompt, at, $"Prompt '{name}' can't be rendered for stage '{stage.Id}': {problem}.");
            }
        }

        private void ValidateKind(StageDefinition stage, string path)
        {
            if (stage.Agent is not null && stage.Handler is not null)
            {
                Add(DefinitionErrorCodes.StageKind, path, $"Stage '{stage.Id}' has both an 'agent' and a 'handler'; a stage has one kind.");
            }

            if (stage.Agent is { } agent)
            {
                if (string.IsNullOrEmpty(agent.Key))
                {
                    Add(DefinitionErrorCodes.Parse, path + ".agent.key", "'key' is required.");
                }

                for (var t = 0; t < agent.Tools.Count; t++)
                {
                    var tool = agent.Tools[t];
                    if (!string.IsNullOrEmpty(tool) && !_tools.Contains(tool))
                    {
                        Add(DefinitionErrorCodes.UnknownTool, $"{path}.agent.tools[{t}]", $"No pack registered tool '{tool}'.");
                    }
                }

                CheckJobType(agent.JobType, path + ".agent.job_type");
                CheckPrompt(stage, agent, path + ".agent");
            }

            if (stage.EnqueueOnEnter is { } enqueue)
            {
                CheckJobType(enqueue, path + ".enqueue_on_enter");
            }

            if (stage.Handler is { } handler && !string.IsNullOrEmpty(handler) && !_handlers.Contains(handler))
            {
                Add(DefinitionErrorCodes.UnknownHandler, path + ".handler", $"No pack registered stage handler '{handler}'.");
            }
        }

        private void ValidateArtifactsOf(StageDefinition stage, string path)
        {
            for (var j = 0; j < stage.Inputs.Count; j++)
            {
                CheckArtifactRef(stage.Inputs[j], $"{path}.inputs[{j}]");
            }

            if (stage.Output is { } output)
            {
                CheckArtifactRef(output, path + ".output");
                CheckNotProjectScoped(output, path + ".output");
            }
        }

        /// <summary>FR-352: a project-scope artifact is edited by people through the artifact endpoints, never produced by a run.</summary>
        private void CheckNotProjectScoped(string key, string path)
        {
            if (definition.FindArtifact(key) is { Scope: ArtifactScope.Project })
            {
                Add(DefinitionErrorCodes.ScopeInput, path,
                    $"Artifact '{key}' has 'scope: project': people edit and lock it through the artifact endpoints, so no stage, gate or handler of a run may produce it.");
            }
        }

        private void ValidateRequirements(IReadOnlyList<StageRequirement> requires, string path)
        {
            for (var j = 0; j < requires.Count; j++)
            {
                var itemPath = $"{path}[{j}]";
                switch (requires[j])
                {
                    case ArtifactLockedRequirement locked:
                        CheckArtifactRef(locked.ArtifactKey, itemPath + ".artifact_locked");
                        break;
                    case GuardRequirement guard:
                        CheckGuard(guard.Guard, itemPath + ".guard");
                        break;
                    case InputRequirement input:
                        CheckInputArtifact(input.ArtifactKey, itemPath + ".input");
                        break;
                    case NoPendingGatesRequirement none:
                        for (var k = 0; k < none.Except.Count; k++)
                        {
                            if (!_gateTypes.Contains(none.Except[k]))
                            {
                                Add(DefinitionErrorCodes.Parse, $"{itemPath}.no_pending_gates.except[{k}]",
                                    $"Gate type '{none.Except[k]}' is not a gate in this definition (or '{EngineGates.Escalation}').");
                            }
                        }

                        break;
                }
            }
        }

        private void ValidateGuards(StageDefinition stage, string path)
        {
            for (var j = 0; j < stage.Validate.Count; j++)
            {
                var validation = stage.Validate[j];
                if (validation.Agent is null)
                {
                    CheckGuard(validation.Guard!, $"{path}.validate[{j}].guard");
                }
                else if (validation.Guard is not null)
                {
                    Add(DefinitionErrorCodes.Parse, $"{path}.validate[{j}]", "A 'validate' entry needs exactly one of 'guard' and 'agent'.");
                }
                else
                {
                    // FR-350: a validator agent is one the stage binds under 'agents:'.
                    CheckBoundAgent(stage, validation.Agent, $"{path}.validate[{j}].agent");
                }
            }

            if (stage.OnInvalid is { } onInvalid && onInvalid.MaxAttempts is < MinAttempts or > MaxAttempts)
            {
                Add(DefinitionErrorCodes.Parse, path + ".on_invalid.max_attempts",
                    $"'max_attempts' must be {MinAttempts}–{MaxAttempts}, not {onInvalid.MaxAttempts}.");
            }

            if (stage.OnInvalid is not null && stage.Validate.Count == 0 && definition.FindArtifact(stage.Output)?.Schema is null)
            {
                Add(DefinitionErrorCodes.Parse, path + ".on_invalid",
                    "'on_invalid' has nothing to validate: the stage has no 'validate' guards and its output has no schema.");
            }
        }

        private void ValidateGate(StageDefinition stage, string path)
        {
            if (stage.Gate is not { } gate)
            {
                return;
            }

            if (string.IsNullOrEmpty(gate.Type))
            {
                Add(DefinitionErrorCodes.Parse, path + ".gate.type", "'type' is required.");
            }
            else if (gate.Type == EngineGates.Escalation)
            {
                Add(DefinitionErrorCodes.Parse, path + ".gate.type",
                    $"'{EngineGates.Escalation}' gates are engine-owned and never guard entry to a stage; use another gate type.");
            }
            else
            {
                CheckName(gate.Type, path + ".gate.type", "Gate type");
            }

            if (gate.Modes.Count == 0)
            {
                Add(DefinitionErrorCodes.Parse, path + ".gate.modes", "'modes' needs at least one approval mode.");
            }

            for (var m = 0; m < gate.Modes.Count; m++)
            {
                if (definition.ApprovalModes.Count > 0 && !definition.ApprovalModes.Contains(gate.Modes[m]))
                {
                    Add(DefinitionErrorCodes.Parse, $"{path}.gate.modes[{m}]", $"Approval mode '{gate.Modes[m].ToWire()}' is not in 'approval_modes'.");
                }
            }

            if (gate.Approves is { } approves)
            {
                CheckArtifactRef(approves, path + ".gate.approves");
                CheckNotProjectScoped(approves, path + ".gate.approves");
            }
        }

        private void ValidateTransitions(StageDefinition stage, int index, string path)
        {
            if (stage.OnSuccess is { } onSuccess)
            {
                if (CheckStageRef(onSuccess, path + ".on_success", required: true) && definition.IndexOfStage(onSuccess) <= index)
                {
                    Add(DefinitionErrorCodes.UncappedLoop, path + ".on_success",
                        $"'on_success' goes back to '{onSuccess}', a cycle with no cap; use 'loop' with 'max_iterations'.");
                }
            }

            if (stage.OnReject is { } onReject)
            {
                var target = definition.IndexOfStage(onReject.To);
                if (CheckStageRef(onReject.To, path + ".on_reject", required: true))
                {
                    if (target <= index && onReject.MaxIterations is null)
                    {
                        Add(DefinitionErrorCodes.UncappedLoop, path + ".on_reject",
                            $"'on_reject' goes back to '{onReject.To}' without 'max_iterations'.");
                    }
                }

                CheckCap(onReject.MaxIterations, path + ".on_reject.max_iterations");
            }

            if (stage.Loop is { } loop)
            {
                var loopPath = path + ".loop";
                if (CheckStageRef(loop.To, loopPath + ".to", required: true) && definition.IndexOfStage(loop.To) >= index)
                {
                    Add(DefinitionErrorCodes.LoopForward, loopPath + ".to", $"'loop.to' must be an earlier stage than '{stage.Id}', not '{loop.To}'.");
                }

                if (loop.MaxIterations is null)
                {
                    Add(DefinitionErrorCodes.UncappedLoop, loopPath, "A 'loop' needs 'max_iterations'.");
                }

                CheckCap(loop.MaxIterations, loopPath + ".max_iterations");
                ValidateRequirements(loop.Requires, loopPath + ".requires");
            }
        }

        private void CheckCap(IterationCap? cap, string path)
        {
            if (cap is null)
            {
                return;
            }

            if (cap.Setting is { } setting)
            {
                if (!_settings.Contains(setting))
                {
                    Add(DefinitionErrorCodes.UnknownSetting, path + ".setting", $"Setting '{setting}' is not declared in 'settings'.");
                }
            }
            else if (cap.Value is not { } value || value is < MinIterations or > MaxIterations)
            {
                Add(DefinitionErrorCodes.Parse, path, $"'max_iterations' must be {MinIterations}–{MaxIterations} or {{ setting: <name> }}.");
            }
        }

        private void ValidateStart()
        {
            if (definition.Start.Count == 0)
            {
                Add(DefinitionErrorCodes.Parse, "start", "'start' needs at least one stage.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < definition.Start.Count; i++)
            {
                var path = $"start[{i}]";
                if (CheckStageRef(definition.Start[i], path, required: true) && !seen.Add(definition.Start[i]))
                {
                    Add(DefinitionErrorCodes.DuplicateId, path, $"Start stage '{definition.Start[i]}' is listed twice.");
                }
            }
        }

        /// <summary>Every stage must be reachable from a start stage via next/on_success, on_reject or loop.to.</summary>
        private void ValidateReachability()
        {
            var stages = definition.Stages;
            var visited = new bool[stages.Count];
            var queue = new Queue<int>();
            foreach (var start in definition.Start)
            {
                var index = definition.IndexOfStage(start);
                if (index >= 0 && !visited[index])
                {
                    visited[index] = true;
                    queue.Enqueue(index);
                }
            }

            if (queue.Count == 0)
            {
                return; // no valid start stage: already reported, and every stage would read as unreachable
            }

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var stage = stages[index];
                string?[] targets =
                [
                    stage.OnSuccess ?? definition.NextInFlow(index),
                    stage.OnReject?.To,
                    stage.Loop?.To,
                ];
                foreach (var target in targets)
                {
                    var next = definition.IndexOfStage(target);
                    if (next >= 0 && !visited[next])
                    {
                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < stages.Count; i++)
            {
                if (!visited[i] && definition.IndexOfStage(stages[i].Id) == i && !FollowUpRules.IsFollowUpStage(definition, stages[i].Id)
                    && reported.Add(stages[i].Id))
                {
                    Add(DefinitionErrorCodes.UnreachableStage, $"stages[{i}]", $"No path from a start stage reaches stage '{stages[i].Id}'.");
                }
            }
        }

        /// <summary>An artifact some stage reads must be some stage's output or produced by a pack handler.</summary>
        private void ValidateProducers()
        {
            // FR-335: an input artifact is supplied from outside the run, so it needs no producer; FR-352: nor does a
            // project-scope artifact, which people edit and the run pins at its start.
            var produced = new HashSet<string>(
                catalog.ProducedArtifacts.Concat(definition.Artifacts.Where(a => a.Input || a.Scope == ArtifactScope.Project).Select(a => a.Key)),
                StringComparer.Ordinal);

            // FR-352: a pack handler doesn't produce a project-scope artifact either (people edit it).
            foreach (var artifact in definition.Artifacts.Where(a => a.Scope == ArtifactScope.Project && catalog.ProducedArtifacts.Contains(a.Key)))
            {
                Add(DefinitionErrorCodes.ScopeInput, $"artifacts.{artifact.Key}.scope",
                    $"Artifact '{artifact.Key}' has 'scope: project', but a pack handler produces it; people edit and lock it through the artifact endpoints.");
            }

            foreach (var stage in definition.AllStages)
            {
                if (stage.Output is { } output)
                {
                    produced.Add(output);
                }

                // D-0.7.0-42: a secondary agent's output is produced too.
                produced.UnionWith(stage.Agents.Select(b => b.Output).OfType<string>());
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (stage, at) in _allStages)
            {
                var inputs = stage.Inputs;
                for (var j = 0; j < inputs.Count; j++)
                {
                    var key = inputs[j];
                    if (_artifactKeys.Contains(key) && !produced.Contains(key) && reported.Add(key))
                    {
                        Add(DefinitionErrorCodes.ArtifactNoProducer, $"{at}.inputs[{j}]",
                            $"Artifact '{key}' is read but no stage outputs it and no pack handler produces it.");
                    }
                }

                var bindings = stage.Agents;
                for (var b = 0; b < bindings.Count; b++)
                {
                    for (var j = 0; j < bindings[b].Inputs.Count; j++)
                    {
                        var key = bindings[b].Inputs[j];
                        if (_artifactKeys.Contains(key) && !produced.Contains(key) && reported.Add(key))
                        {
                            Add(DefinitionErrorCodes.ArtifactNoProducer, $"{at}.agents[{b}].inputs[{j}]",
                                $"Artifact '{key}' is read but no stage outputs it and no pack handler produces it.");
                        }
                    }
                }
            }
        }

        private bool CheckStageRef(string? stageId, string path, bool required)
        {
            if (string.IsNullOrEmpty(stageId))
            {
                if (required)
                {
                    Add(DefinitionErrorCodes.Parse, path, "A stage id is required.");
                }

                return false;
            }

            if (!_stageIds.Contains(stageId))
            {
                Add(DefinitionErrorCodes.UnknownStage, path, $"Stage '{stageId}' is not declared.");
                return false;
            }

            return true;
        }

        private void CheckArtifactRef(string key, string path)
        {
            if (string.IsNullOrEmpty(key))
            {
                Add(DefinitionErrorCodes.Parse, path, "An artifact key is required.");
            }
            else if (!_artifactKeys.Contains(key))
            {
                Add(DefinitionErrorCodes.UnknownArtifact, path, $"Artifact '{key}' is not declared in 'artifacts'.");
            }
        }

        private void CheckStatus(string status, string path)
        {
            if (string.IsNullOrEmpty(status))
            {
                Add(DefinitionErrorCodes.Parse, path, "A status is required.");
            }
            else if (!_statuses.Contains(status))
            {
                Add(DefinitionErrorCodes.UnknownStatus, path, $"Status '{status}' is not declared in 'statuses'.");
            }
        }

        private void CheckGuard(string guard, string path)
        {
            if (string.IsNullOrEmpty(guard))
            {
                Add(DefinitionErrorCodes.Parse, path, "A guard name is required.");
            }
            else if (!_guards.Contains(guard))
            {
                Add(DefinitionErrorCodes.UnknownGuard, path, $"No pack registered guard '{guard}'.");
            }
        }

        private void CheckJobType(string jobType, string path)
        {
            if (string.IsNullOrEmpty(jobType))
            {
                Add(DefinitionErrorCodes.Parse, path, "A job type is required.");
            }
            else if (!catalog.HasJobType(jobType))
            {
                Add(DefinitionErrorCodes.UnknownJobType, path, $"No pack registered job type '{jobType}'.");
            }
        }

        private void CheckName(string name, string path, string what)
        {
            if (!NamePattern().IsMatch(name))
            {
                Add(DefinitionErrorCodes.Parse, path, $"{what} '{name}' must be lower-case letters, digits, '_' or '-', starting with a letter.");
            }
        }
    }
}
