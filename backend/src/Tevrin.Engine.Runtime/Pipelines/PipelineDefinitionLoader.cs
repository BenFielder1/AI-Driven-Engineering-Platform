using System.Globalization;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>The loader's output: the model (<c>null</c> when the document couldn't be read at all) and every parse error.</summary>
public sealed record PipelineDefinitionParseResult(PipelineDefinition? Definition, IReadOnlyList<DefinitionError> Errors);

/// <summary>
/// FR-275: maps a YAML pipeline definition (snake_case keys, §4.49) to <see cref="PipelineDefinition"/>. Reading is safe
/// (<see cref="SafeYamlReader"/>: no tags, size and alias limits). Every problem is collected as <c>DEF_PARSE</c> with its
/// YAML path: an unknown key, a value of the wrong type, an unknown enum value, a missing required enum. A required
/// string that is missing or unreadable is left empty for <see cref="PipelineDefinitionValidator"/> to report. Canonical
/// JSON (<see cref="DefinitionHash.CanonicalJson"/>) is valid input and reads back to the same model.
/// </summary>
public static class PipelineDefinitionLoader
{
    private const string Root = "$";

    public static PipelineDefinitionParseResult Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        YamlNode root;
        try
        {
            root = SafeYamlReader.Read(yaml);
        }
        catch (YamlRefusedException ex)
        {
            return new PipelineDefinitionParseResult(null, [new DefinitionError(DefinitionErrorCodes.Parse, Root, ex.Message)]);
        }

        var errors = new List<DefinitionError>();
        if (root is not YamlMapping map)
        {
            errors.Add(new DefinitionError(DefinitionErrorCodes.Parse, Root, "A definition must be a mapping."));
            return new PipelineDefinitionParseResult(null, errors);
        }

        var definition = new Mapper(errors).Definition(map);
        return new PipelineDefinitionParseResult(definition, errors);
    }

    /// <summary>Parses and validates: the parse errors plus the validator's (each reported once, <see cref="DefinitionErrorCodes.Merge"/>).</summary>
    public static PipelineDefinitionParseResult ParseAndValidate(string yaml, PackCatalog catalog)
    {
        var parsed = Parse(yaml);
        if (parsed.Definition is null)
        {
            return parsed;
        }

        var errors = DefinitionErrorCodes.Merge(parsed.Errors, PipelineDefinitionValidator.Validate(parsed.Definition, catalog));
        return parsed with { Errors = errors };
    }

    private sealed class Mapper(List<DefinitionError> errors)
    {
        private static readonly string[] TopKeys =
        [
            "pipeline", "version", "title", "trigger", "approval_modes", "default_approval_mode", "start", "artifacts",
            "stages", "statuses", "status_changes", "concurrency", "settings", "rollback_status",
        ];

        private static readonly string[] StageKeys =
        [
            "id", "title", "status", "agent", "handler", "inputs", "output", "requires", "validate", "on_invalid", "gate",
            "on_success", "on_reject", "loop", "enqueue_on_enter", "agents", "handlers",
        ];

        private static readonly string[] BindingKeys = ["key", "job_type", "prompt", "inputs", "output", "tools", "validate", "on_invalid"];

        public PipelineDefinition Definition(YamlMapping map)
        {
            var obj = Object(map, string.Empty, TopKeys);
            return new PipelineDefinition
            {
                Pipeline = obj.String("pipeline"),
                Version = obj.Int("version") ?? 0,
                Title = obj.String("title"),
                Trigger = Trigger(obj),
                ApprovalModes = obj.List("approval_modes", (node, path) => Enum<ApprovalMode>(node, path)).OfType<ApprovalMode>().ToList(),
                DefaultApprovalMode = obj.Enum<ApprovalMode>("default_approval_mode"),
                Start = obj.StringList("start"),
                Settings = obj.StringList("settings"),
                Artifacts = obj.Keyed("artifacts", Artifact),
                Statuses = obj.Keyed("statuses", Status),
                StatusChanges = obj.Keyed("status_changes", StatusChange),
                RollbackStatus = obj.OptionalString("rollback_status"),
                Concurrency = Concurrency(obj),
                Stages = obj.List("stages", Stage).OfType<StageDefinition>().ToList(),
            };
        }

        private PipelineTrigger Trigger(Obj top)
        {
            if (top.Mapping("trigger") is not { } trigger)
            {
                return PipelineTrigger.Manual;
            }

            // Every key besides 'kind' is the kind's option (a schedule's cron, a webhook's path), kept as text.
            var obj = Object(trigger.Map, trigger.Path, null);
            var kind = obj.Enum<TriggerKind>("kind", required: true) ?? TriggerKind.Manual;
            var options = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in obj.Entries)
            {
                if (key == "kind")
                {
                    continue;
                }

                if (value is YamlScalar scalar && !scalar.IsNull)
                {
                    options[key] = scalar.Value;
                }
                else
                {
                    Error(Join(trigger.Path, key), "Trigger options must be single values.");
                }
            }

            return new PipelineTrigger(kind, options);
        }

        private ArtifactDefinition? Artifact(string key, YamlNode node, string path)
        {
            if (AsObject(node, path, ["title", "format", "schema", "approval", "cascade", "input", "scope"]) is not { } obj)
            {
                return new ArtifactDefinition(key, string.Empty, ArtifactFormat.Markdown, null, ArtifactApproval.Required, CascadeMode.Downstream);
            }

            return new ArtifactDefinition(
                key,
                obj.String("title"),
                obj.Enum<ArtifactFormat>("format") ?? ArtifactFormat.Markdown,
                obj.OptionalString("schema"),
                obj.Enum<ArtifactApproval>("approval") ?? ArtifactApproval.Required,
                obj.Enum<CascadeMode>("cascade") ?? CascadeMode.Downstream)
            {
                // FR-335: supplied from outside the run.
                Input = obj.Bool("input") ?? false,

                // FR-352: one version history per project, pinned at run start.
                Scope = obj.Enum<ArtifactScope>("scope") ?? ArtifactScope.Run,
            };
        }

        private StatusDefinition? Status(string name, YamlNode node, string path)
        {
            var obj = AsObject(node, path, ["generic"]);
            return new StatusDefinition(name, obj?.Enum<GenericRunStatus>("generic", required: true) ?? GenericRunStatus.Active);
        }

        private StatusChangeDefinition? StatusChange(string name, YamlNode node, string path)
        {
            if (AsObject(node, path, ["stage", "from", "to", "completes_run", "error_code", "reason", "error_message", "wrong_stage_message"]) is not { } obj)
            {
                return new StatusChangeDefinition(name, string.Empty, [], string.Empty, false, string.Empty);
            }

            return new StatusChangeDefinition(
                name,
                obj.String("stage"),
                obj.StringList("from", allowSingle: true),
                obj.String("to"),
                obj.Bool("completes_run") ?? false,
                obj.String("error_code"))
            {
                Reason = obj.OptionalString("reason"),
                ErrorMessage = obj.OptionalString("error_message"),
                WrongStageMessage = obj.OptionalString("wrong_stage_message"),
            };
        }

        private ConcurrencyDefinition Concurrency(Obj top)
        {
            if (top.Mapping("concurrency") is not { } concurrency)
            {
                return ConcurrencyDefinition.None;
            }

            var obj = Object(concurrency.Map, concurrency.Path, ["exclusive", "runs"]);
            var exclusive = obj.List("exclusive", (node, path) =>
            {
                var item = AsObject(node, path, ["job_type", "scope"]);
                return item is null
                    ? null
                    : new ExclusiveJob(item.String("job_type"), item.Enum<ConcurrencyScope>("scope", required: true) ?? ConcurrencyScope.Project);
            });
            return new ConcurrencyDefinition(exclusive.OfType<ExclusiveJob>().ToList())
            {
                // FR-331: runs per project, default one.
                Runs = obj.Enum<RunConcurrency>("runs") ?? RunConcurrency.One,
            };
        }

        private StageDefinition? Stage(YamlNode node, string path)
        {
            if (AsObject(node, path, StageKeys) is not { } obj)
            {
                return new StageDefinition { Id = string.Empty };
            }

            var inputs = obj.StringList("inputs");
            return new StageDefinition
            {
                Id = obj.String("id"),
                Title = obj.String("title"),
                Status = obj.String("status"),
                Agent = Agent(obj),
                Agents = Bindings(obj, inputs),
                Handler = obj.OptionalString("handler"),

                // D-0.7.0-43: secondary stage handlers, as agents: binds secondary agents.
                Handlers = obj.StringList("handlers"),
                Inputs = inputs,
                Output = obj.OptionalString("output"),
                Requires = Requirements(obj, "requires"),
                Validate = Validations(obj),
                OnInvalid = OnInvalid(obj),
                Gate = Gate(obj),
                OnSuccess = obj.OptionalString("on_success"),
                OnReject = OnReject(obj),
                Loop = Loop(obj),
                EnqueueOnEnter = obj.OptionalString("enqueue_on_enter"),
            };
        }

        private AgentStageDefinition? Agent(Obj stage)
        {
            if (stage.Mapping("agent") is not { } agent)
            {
                return null;
            }

            var obj = Object(agent.Map, agent.Path, ["key", "prompt", "model", "tools", "job_type"]);
            return new AgentStageDefinition(
                obj.String("key"),
                obj.OptionalString("prompt"),
                obj.OptionalString("model"),
                obj.StringList("tools"),
                obj.OptionalString("job_type") ?? EngineJobTypes.AgentRun);
        }

        /// <summary>D-0.7.0-42: <c>agents:</c>; a binding's inputs default to the stage's (<paramref name="stageInputs"/>).
        /// A missing <c>key</c> or <c>job_type</c> is left empty for the validator to report.</summary>
        private List<AgentBindingDefinition> Bindings(Obj stage, IReadOnlyList<string> stageInputs) =>
            stage.List("agents", (node, path) =>
            {
                if (AsObject(node, path, BindingKeys) is not { } obj)
                {
                    return null;
                }

                return new AgentBindingDefinition(
                    obj.String("key"),
                    obj.String("job_type"),
                    obj.OptionalString("prompt"),
                    obj.Get("inputs") is null or YamlScalar { IsNull: true } ? [.. stageInputs] : obj.StringList("inputs"),
                    obj.OptionalString("output"),
                    obj.StringList("tools"))
                {
                    // D-0.7.0-44: the binding's own output checks.
                    Validate = Validations(obj),
                    OnInvalid = OnInvalid(obj),
                };
            }).OfType<AgentBindingDefinition>().ToList();

        private List<StageValidation> Validations(Obj owner) =>
            owner.List("validate", (item, itemPath) =>
            {
                var v = AsObject(item, itemPath, ["guard"]);
                return v is null ? null : new StageValidation(v.String("guard"));
            }).OfType<StageValidation>().ToList();

        private List<StageRequirement> Requirements(Obj owner, string key) =>
            owner.List(key, (node, path) =>
            {
                if (node is not YamlMapping map || map.Entries.Count != 1)
                {
                    Error(path, "A requirement is one of { artifact_locked: <key> }, { no_pending_gates: { except: [...] } }, { guard: <name> } or { input: <key> }.");
                    return null;
                }

                var (kind, value) = (map.Entries[0].Key.Value, map.Entries[0].Value);
                var valuePath = Join(path, kind);
                switch (kind)
                {
                    case "artifact_locked":
                        return new ArtifactLockedRequirement(Text(value, valuePath) ?? string.Empty);
                    case "guard":
                        return new GuardRequirement(Text(value, valuePath) ?? string.Empty);
                    case "input":
                        // FR-335: the run waits for a version of this input artifact.
                        return new InputRequirement(Text(value, valuePath) ?? string.Empty);
                    case "no_pending_gates":
                        if (value is YamlScalar { IsNull: true })
                        {
                            return new NoPendingGatesRequirement([]);
                        }

                        var obj = AsObject(value, valuePath, ["except"]);
                        return new NoPendingGatesRequirement(obj?.StringList("except") ?? []);
                    default:
                        Error(valuePath, $"Unknown requirement '{kind}'.");
                        return (StageRequirement?)null;
                }
            }).OfType<StageRequirement>().ToList();

        private OnInvalidPolicy? OnInvalid(Obj stage)
        {
            if (stage.Mapping("on_invalid") is not { } onInvalid)
            {
                return null;
            }

            var obj = Object(onInvalid.Map, onInvalid.Path, ["retry_with_feedback", "max_attempts", "then"]);
            return new OnInvalidPolicy(
                obj.Bool("retry_with_feedback") ?? false,
                obj.Int("max_attempts") ?? 0,
                obj.Enum<InvalidOutcome>("then", required: true) ?? InvalidOutcome.Fail);
        }

        private StageGate? Gate(Obj stage)
        {
            if (stage.Mapping("gate") is not { } gate)
            {
                return null;
            }

            var obj = Object(gate.Map, gate.Path, ["type", "modes", "approves", "approval_links"]);
            var approves = obj.OptionalString("approves");
            return new StageGate(
                obj.String("type"),
                obj.List("modes", (node, path) => Enum<ApprovalMode>(node, path)).OfType<ApprovalMode>().ToList(),
                approves == "none" ? null : approves,
                obj.Bool("approval_links") ?? false);
        }

        private RejectTransition? OnReject(Obj stage)
        {
            if (stage.Get("on_reject") is not { } node || node is YamlScalar { IsNull: true })
            {
                return null;
            }

            var path = Join(stage.Path, "on_reject");
            if (node is YamlScalar scalar)
            {
                return new RejectTransition(scalar.Value, null);
            }

            var obj = AsObject(node, path, ["to", "max_iterations"]);
            return obj is null ? null : new RejectTransition(obj.String("to"), Cap(obj));
        }

        private LoopDefinition? Loop(Obj stage)
        {
            if (stage.Mapping("loop") is not { } loop)
            {
                return null;
            }

            var obj = Object(loop.Map, loop.Path, ["to", "on", "max_iterations", "requires", "title"]);
            return new LoopDefinition(obj.String("to"), obj.OptionalString("on"), Cap(obj), Requirements(obj, "requires"))
            {
                Title = obj.OptionalString("title"),
            };
        }

        private IterationCap? Cap(Obj owner)
        {
            if (owner.Get("max_iterations") is not { } node || node is YamlScalar { IsNull: true })
            {
                return null;
            }

            var path = Join(owner.Path, "max_iterations");
            if (node is YamlMapping)
            {
                var obj = AsObject(node, path, ["setting"]);
                return obj is null ? null : IterationCap.FromSetting(obj.String("setting"));
            }

            return Integer(node, path) is { } value ? IterationCap.Fixed(value) : null;
        }

        // ---- typed reads ----

        private void Error(string path, string message) => errors.Add(new DefinitionError(DefinitionErrorCodes.Parse, path, message));

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private Obj Object(YamlMapping map, string path, string[]? allowedKeys)
        {
            var obj = new Obj(this, path);
            foreach (var (keyNode, value) in map.Entries)
            {
                var key = keyNode.Value;
                if (allowedKeys is not null && !allowedKeys.Contains(key, StringComparer.Ordinal))
                {
                    Error(Join(path, key), $"Unknown key '{key}'.");
                    continue;
                }

                if (obj.Entries.Any(e => e.Key == key))
                {
                    Error(Join(path, key), $"Key '{key}' appears twice.");
                    continue;
                }

                obj.Entries.Add((key, value));
            }

            return obj;
        }

        private Obj? AsObject(YamlNode node, string path, string[] allowedKeys)
        {
            if (node is YamlMapping map)
            {
                return Object(map, path, allowedKeys);
            }

            Error(path, "Expected a mapping.");
            return null;
        }

        private string? Text(YamlNode node, string path)
        {
            switch (node)
            {
                case YamlScalar { IsNull: true }:
                    return null;
                case YamlScalar scalar:
                    return scalar.Value;
                default:
                    Error(path, "Expected a single value.");
                    return null;
            }
        }

        private int? Integer(YamlNode node, string path)
        {
            if (node is YamlScalar { Style: YamlDotNet.Core.ScalarStyle.Plain } scalar
                && int.TryParse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            Error(path, "Expected an integer.");
            return null;
        }

        private bool? Boolean(YamlNode node, string path)
        {
            if (node is YamlScalar { Style: YamlDotNet.Core.ScalarStyle.Plain } scalar)
            {
                switch (scalar.Value)
                {
                    case "true" or "True" or "TRUE":
                        return true;
                    case "false" or "False" or "FALSE":
                        return false;
                }
            }

            Error(path, "Expected true or false.");
            return null;
        }

        private TEnum? Enum<TEnum>(YamlNode node, string path)
            where TEnum : struct, System.Enum
        {
            var text = Text(node, path);
            if (text is null)
            {
                return null;
            }

            if (EnumStrings.TryParse(text, out TEnum value))
            {
                return value;
            }

            Error(path, $"'{text}' is not one of: {string.Join(", ", EnumStrings.Values<TEnum>())}.");
            return null;
        }

        private sealed class Obj(Mapper mapper, string path)
        {
            public string Path { get; } = path;

            public List<(string Key, YamlNode Value)> Entries { get; } = [];

            public YamlNode? Get(string key) => Entries.FirstOrDefault(e => e.Key == key).Value;

            private (YamlNode Node, string Path)? Present(string key) =>
                Get(key) is { } node && node is not YamlScalar { IsNull: true } ? (node, Join(Path, key)) : null;

            public string String(string key) => OptionalString(key) ?? string.Empty;

            public string? OptionalString(string key) => Present(key) is { } p ? mapper.Text(p.Node, p.Path) : null;

            public int? Int(string key) => Present(key) is { } p ? mapper.Integer(p.Node, p.Path) : null;

            public bool? Bool(string key) => Present(key) is { } p ? mapper.Boolean(p.Node, p.Path) : null;

            public TEnum? Enum<TEnum>(string key, bool required = false)
                where TEnum : struct, System.Enum
            {
                if (Present(key) is { } p)
                {
                    return mapper.Enum<TEnum>(p.Node, p.Path);
                }

                if (required)
                {
                    mapper.Error(Join(Path, key), $"'{key}' is required.");
                }

                return null;
            }

            public (YamlMapping Map, string Path)? Mapping(string key)
            {
                if (Present(key) is not { } p)
                {
                    return null;
                }

                if (p.Node is YamlMapping map)
                {
                    return (map, p.Path);
                }

                mapper.Error(p.Path, "Expected a mapping.");
                return null;
            }

            public List<T?> List<T>(string key, Func<YamlNode, string, T?> item)
            {
                if (Present(key) is not { } p)
                {
                    return [];
                }

                if (p.Node is not YamlSequence sequence)
                {
                    mapper.Error(p.Path, "Expected a list.");
                    return [];
                }

                return [.. sequence.Items.Select((node, i) => item(node, $"{p.Path}[{i}]"))];
            }

            public List<string> StringList(string key, bool allowSingle = false)
            {
                if (allowSingle && Present(key) is { Node: YamlScalar scalar })
                {
                    return [scalar.Value];
                }

                return [.. List(key, (node, path) => mapper.Text(node, path)).Select(text => text ?? string.Empty)];
            }

            /// <summary>A mapping of key → entry (artifacts, statuses, status changes), duplicates kept for the validator.</summary>
            public List<T> Keyed<T>(string key, Func<string, YamlNode, string, T?> entry)
                where T : class
            {
                if (Present(key) is not { } p)
                {
                    return [];
                }

                if (p.Node is not YamlMapping map)
                {
                    mapper.Error(p.Path, "Expected a mapping.");
                    return [];
                }

                return [.. map.Entries.Select(e => entry(e.Key.Value, e.Value, Join(p.Path, e.Key.Value))).OfType<T>()];
            }
        }
    }
}
