using System.Globalization;
using System.Text;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-277, §4.50: the definition hash, SHA-256 (lower-case hex) of the definition's canonical JSON. The canonical JSON is
/// the parsed model written with keys sorted (ordinal), no insignificant whitespace, LF line endings and UTF-8, in the
/// same shape and key names as the YAML, so the loader reads it back to the same model (M58 stores it in
/// <c>pipeline_definitions</c> and pins runs to it). Comments, key order and YAML formatting don't change the hash; any
/// value does. Optional values the loader defaults are written out, and absent optional values are omitted.
/// <para>
/// The JSON is written by this class, not a JSON library, so its bytes don't depend on a .NET version's Unicode tables.
/// In strings only <c>"</c> (as <c>\"</c>), <c>\</c> (as <c>\\</c>) and U+0000–U+001F (always <c>\u00xx</c> with
/// lower-case hex, never short forms such as <c>\n</c>) are escaped; every other code point is written as raw UTF-8.
/// Integers are invariant decimal; booleans are <c>true</c>/<c>false</c>.
/// </para>
/// </summary>
public static class DefinitionHash
{
    public static string Compute(PipelineDefinition definition) => ContentHash.Compute(CanonicalJson(definition));

    public static string CanonicalJson(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var json = new StringBuilder();
        Write(json, ToTree(definition));
        return json.ToString();
    }

    private static Obj ToTree(PipelineDefinition d)
    {
        var root = new Obj
        {
            ["pipeline"] = d.Pipeline,
            ["version"] = d.Version,
            ["title"] = d.Title,
            ["trigger"] = Trigger(d.Trigger),
            ["approval_modes"] = d.ApprovalModes.Select(m => (object?)m.ToWire()).ToList(),
            ["start"] = Strings(d.Start),
            ["settings"] = Strings(d.Settings),
            ["artifacts"] = Keyed(d.Artifacts.Select(a => (a.Key, (object?)Artifact(a)))),
            ["statuses"] = Keyed(d.Statuses.Select(s => (s.Name, (object?)new Obj { ["generic"] = s.Generic.ToWire() }))),
            ["status_changes"] = Keyed(d.StatusChanges.Select(c => (c.Name, (object?)StatusChange(c)))),
            ["concurrency"] = new Obj
            {
                ["exclusive"] = d.Concurrency.Exclusive
                    .Select(e => (object?)new Obj { ["job_type"] = e.JobType, ["scope"] = e.Scope.ToWire() }).ToList(),
            },
            ["stages"] = d.Stages.Select(s => (object?)Stage(s)).ToList(),
        };
        root.AddIfSet("default_approval_mode", d.DefaultApprovalMode?.ToWire());
        root.AddIfSet("rollback_status", d.RollbackStatus);
        return root;
    }

    private static Obj Trigger(PipelineTrigger trigger)
    {
        var obj = new Obj();
        foreach (var (key, value) in trigger.Options)
        {
            obj[key] = value;
        }

        obj["kind"] = trigger.Kind.ToWire();
        return obj;
    }

    private static Obj Artifact(ArtifactDefinition a)
    {
        var obj = new Obj
        {
            ["title"] = a.Title,
            ["format"] = a.Format.ToWire(),
            ["approval"] = a.Approval.ToWire(),
            ["cascade"] = a.Cascade.ToWire(),
        };
        obj.AddIfSet("schema", a.Schema);
        return obj;
    }

    private static Obj StatusChange(StatusChangeDefinition c)
    {
        var obj = new Obj
        {
            ["stage"] = c.Stage,
            ["from"] = Strings(c.From),
            ["to"] = c.To,
            ["completes_run"] = c.CompletesRun,
            ["error_code"] = c.ErrorCode,
            ["reason"] = c.EffectiveReason,
            ["error_message"] = c.EffectiveErrorMessage,
        };
        obj.AddIfSet("wrong_stage_message", c.WrongStageMessage);
        return obj;
    }

    private static Obj Stage(StageDefinition s)
    {
        var obj = new Obj
        {
            ["id"] = s.Id,
            ["title"] = s.Title,
            ["status"] = s.Status,
            ["inputs"] = Strings(s.Inputs),
            ["requires"] = Requirements(s.Requires),
            ["validate"] = s.Validate.Select(v => (object?)new Obj { ["guard"] = v.Guard }).ToList(),
        };
        if (s.Agent is { } agent)
        {
            var a = new Obj { ["key"] = agent.Key, ["tools"] = Strings(agent.Tools), ["job_type"] = agent.JobType };
            a.AddIfSet("prompt", agent.Prompt);
            a.AddIfSet("model", agent.Model);
            obj["agent"] = a;
        }

        obj.AddIfSet("handler", s.Handler);
        obj.AddIfSet("output", s.Output);
        if (s.OnInvalid is { } onInvalid)
        {
            obj["on_invalid"] = new Obj
            {
                ["retry_with_feedback"] = onInvalid.RetryWithFeedback,
                ["max_attempts"] = onInvalid.MaxAttempts,
                ["then"] = onInvalid.Then.ToWire(),
            };
        }

        if (s.Gate is { } gate)
        {
            var g = new Obj
            {
                ["type"] = gate.Type,
                ["modes"] = gate.Modes.Select(m => (object?)m.ToWire()).ToList(),
                ["approval_links"] = gate.ApprovalLinks,
            };
            g.AddIfSet("approves", gate.Approves);
            obj["gate"] = g;
        }

        obj.AddIfSet("on_success", s.OnSuccess);
        obj.AddIfSet("enqueue_on_enter", s.EnqueueOnEnter);
        if (s.OnReject is { } onReject)
        {
            var r = new Obj { ["to"] = onReject.To };
            r.AddIfSet("max_iterations", Cap(onReject.MaxIterations));
            obj["on_reject"] = r;
        }

        if (s.Loop is { } loop)
        {
            var l = new Obj { ["to"] = loop.To, ["requires"] = Requirements(loop.Requires), ["title"] = loop.EffectiveTitle };
            l.AddIfSet("on", loop.On);
            l.AddIfSet("max_iterations", Cap(loop.MaxIterations));
            obj["loop"] = l;
        }

        return obj;
    }

    private static List<object?> Requirements(IReadOnlyList<StageRequirement> requires) =>
        requires.Select(r => (object?)(r switch
        {
            ArtifactLockedRequirement locked => new Obj { ["artifact_locked"] = locked.ArtifactKey },
            NoPendingGatesRequirement none => new Obj { ["no_pending_gates"] = new Obj { ["except"] = Strings(none.Except) } },
            GuardRequirement guard => new Obj { ["guard"] = guard.Guard },
            _ => throw new InvalidOperationException($"Unknown requirement {r.GetType().Name}."),
        })).ToList();

    private static object? Cap(IterationCap? cap) => cap switch
    {
        null => null,
        { Setting: { } setting } => new Obj { ["setting"] = setting },
        { Value: { } value } => value,
        _ => null,
    };

    private static List<object?> Strings(IEnumerable<string> values) => values.Select(v => (object?)v).ToList();

    /// <summary>A keyed mapping. Kept as pairs so a duplicate key (an invalid definition) still hashes deterministically.</summary>
    private static Obj Keyed(IEnumerable<(string Key, object? Value)> entries)
    {
        var obj = new Obj();
        foreach (var (key, value) in entries)
        {
            obj.Pairs.Add(new KeyValuePair<string, object?>(key, value));
        }

        return obj;
    }

    private static void Write(StringBuilder json, object? value)
    {
        switch (value)
        {
            case null:
                json.Append("null");
                break;
            case string text:
                WriteString(json, text);
                break;
            case int number:
                json.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case bool flag:
                json.Append(flag ? "true" : "false");
                break;
            case Obj obj:
                json.Append('{');
                var first = true;
                foreach (var pair in obj.Pairs.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        json.Append(',');
                    }

                    first = false;
                    WriteString(json, pair.Key);
                    json.Append(':');
                    Write(json, pair.Value);
                }

                json.Append('}');
                break;
            case List<object?> list:
                json.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    Write(json, list[i]);
                }

                json.Append(']');
                break;
            default:
                throw new InvalidOperationException($"Unexpected canonical value {value.GetType().Name}.");
        }
    }

    /// <summary>A JSON string, line endings normalised to LF first. Escapes only <c>"</c>, <c>\</c> and U+0000–U+001F
    /// (<c>\u00xx</c>, lower-case hex); everything else is written as is (raw UTF-8 once encoded).</summary>
    private static void WriteString(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var c in ContentHash.NormaliseLineEndings(text))
        {
            switch (c)
            {
                case '"':
                    json.Append(@"\""");
                    break;
                case '\\':
                    json.Append(@"\\");
                    break;
                case < ' ':
                    json.Append(@"\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        json.Append('"');
    }

    /// <summary>A JSON object as ordered pairs; the writer sorts them.</summary>
    private sealed class Obj
    {
        public List<KeyValuePair<string, object?>> Pairs { get; } = [];

        public object? this[string key]
        {
            get => Pairs.LastOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal)).Value;
            set
            {
                Pairs.RemoveAll(p => string.Equals(p.Key, key, StringComparison.Ordinal));
                Pairs.Add(new KeyValuePair<string, object?>(key, value));
            }
        }

        public void AddIfSet(string key, object? value)
        {
            if (value is not null)
            {
                this[key] = value;
            }
        }
    }
}
