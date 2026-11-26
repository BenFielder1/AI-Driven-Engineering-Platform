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
            ["approval_modes"] = d.ApprovalModes.Select(m => (object?)m.ToWire()).ToList(),
            ["start"] = Strings(d.Start),
            ["settings"] = Strings(d.Settings),
            ["artifacts"] = Keyed(d.Artifacts.Select(a => (a.Key, (object?)Artifact(a)))),
            ["statuses"] = Keyed(d.Statuses.Select(s => (s.Name, (object?)new Obj { ["generic"] = s.Generic.ToWire() }))),
            ["status_changes"] = Keyed(d.StatusChanges.Select(c => (c.Name, (object?)StatusChange(c)))),
            ["concurrency"] = Concurrency(d.Concurrency),
            ["stages"] = d.Stages.Select(s => (object?)Stage(s)).ToList(),
        };
        // FR-339: the single 'trigger' as before, or the 'triggers' list instead (absent unless declared, so existing
        // hashes are unchanged).
        if (d.Triggers is { } triggers)
        {
            root["triggers"] = triggers.Select(t => (object?)Trigger(t)).ToList();
        }
        else
        {
            root["trigger"] = Trigger(d.Trigger);
        }

        root.AddIfSet("default_approval_mode", d.DefaultApprovalMode?.ToWire());
        root.AddIfSet("rollback_status", d.RollbackStatus);

        // FR-348: omitted unless declared, so existing hashes are unchanged.
        if (d.Limits is { } limits)
        {
            root["limits"] = new Obj { ["revisions"] = limits.Revisions, ["then"] = limits.Then.ToWire() };
        }

        // FR-354: omitted unless declared, so existing hashes are unchanged.
        if (d.FollowUps.Count > 0)
        {
            root["followups"] = d.FollowUps
                .Select(f => (object?)new Obj { ["stage"] = f.Stage, ["after"] = Strings(f.After) })
                .ToList();
        }

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

    /// <summary>FR-331: <c>runs</c> only when it isn't the default <c>one</c>, so a definition without it hashes as before.</summary>
    private static Obj Concurrency(ConcurrencyDefinition concurrency)
    {
        var obj = new Obj
        {
            ["exclusive"] = concurrency.Exclusive
                .Select(e => (object?)new Obj { ["job_type"] = e.JobType, ["scope"] = e.Scope.ToWire() }).ToList(),
        };
        if (concurrency.Runs != RunConcurrency.One)
        {
            obj["runs"] = concurrency.Runs.ToWire();
        }

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

        // FR-335: omitted unless set, so a definition without input artifacts hashes as before.
        if (a.Input)
        {
            obj["input"] = true;
        }

        // FR-352: omitted for the default run scope, so a definition without project-scope artifacts hashes as before.
        if (a.Scope != ArtifactScope.Run)
        {
            obj["scope"] = a.Scope.ToWire();
        }

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

    /// <summary>A <c>validate:</c> entry; FR-350's <c>agent</c> is omitted when unset, so a guard entry hashes as before.</summary>
    private static object? Validation(StageValidation v)
    {
        var o = new Obj();
        o.AddIfSet("guard", v.Guard);
        o.AddIfSet("agent", v.Agent);
        return o;
    }

    private static Obj Stage(StageDefinition s)
    {
        var obj = new Obj
        {
            ["id"] = s.Id,
            ["title"] = s.Title,
            ["status"] = s.Status,
            ["inputs"] = Inputs(s),
            ["requires"] = Requirements(s.Requires),
            ["validate"] = s.Validate.Select(Validation).ToList(),
        };
        if (s.Agent is { } agent)
        {
            var a = new Obj { ["key"] = agent.Key, ["tools"] = Strings(agent.Tools), ["job_type"] = agent.JobType };
            a.AddIfSet("prompt", agent.Prompt);
            a.AddIfSet("model", agent.Model);
            obj["agent"] = a;
        }

        // D-0.7.0-42: omitted when empty, so a definition without secondary agents hashes as before.
        if (s.Agents.Count > 0)
        {
            obj["agents"] = s.Agents.Select(b => (object?)Binding(b)).ToList();
        }

        obj.AddIfSet("handler", s.Handler);

        // D-0.7.0-43: omitted when empty, so a definition without secondary handlers hashes as before.
        if (s.Handlers.Count > 0)
        {
            obj["handlers"] = Strings(s.Handlers);
        }

        obj.AddIfSet("output", s.Output);
        if (s.OnInvalid is { } onInvalid)
        {
            obj["on_invalid"] = OnInvalid(onInvalid);
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

        AddFlowConstructs(obj, s);
        return obj;
    }

    /// <summary>FR-343–FR-349 (0.8.0): each construct only when used, so a definition without them hashes as before.</summary>
    private static void AddFlowConstructs(Obj obj, StageDefinition s)
    {
        if (s.Parallel is { } parallel)
        {
            obj["parallel"] = new Obj
            {
                ["branches"] = parallel.Branches.Select(b => (object?)b.Select(st => (object?)Stage(st)).ToList()).ToList(),
                ["join"] = parallel.Join.ToWire(),
                ["on_branch_failure"] = parallel.OnBranchFailure.ToWire(),
            };
        }

        if (s.Veto.Count > 0)
        {
            obj["veto"] = s.Veto.Select(v =>
            {
                var o = new Obj();
                o.AddIfSet("guard", v.Guard);
                o.AddIfSet("agent", v.Agent);
                return (object?)o;
            }).ToList();
        }

        if (s.OnVeto is { } onVeto)
        {
            obj["on_veto"] = onVeto.To is { } to ? new Obj { ["to"] = to } : new Obj { ["end"] = OnVetoDefinition.Vetoed };
        }

        if (s.FanOut is { } fanOut)
        {
            var f = new Obj();
            if (fanOut.Count is { } count)
            {
                f["count"] = count;
            }
            else
            {
                f["agents"] = Strings(fanOut.Agents);
            }

            obj["fan_out"] = f;
        }

        if (s.Aggregate is { } aggregate)
        {
            var g = new Obj { ["from"] = aggregate.From };
            if (aggregate.DisagreementGuard is { } guard)
            {
                g["disagreement"] = new Obj { ["guard"] = guard };
            }

            obj["aggregate"] = g;
        }

        if (s.Routes.Count > 0)
        {
            obj["routes"] = Strings(s.Routes);
        }

        if (s.Variants is { } variants)
        {
            obj["variants"] = new Obj
            {
                ["count"] = variants.Count,
                ["select"] = variants.SelectAgent is { } agent ? new Obj { ["agent"] = agent } : "gate",
            };
        }

        // FR-351: left out when unused, so existing hashes are unchanged.
        if (s.Feedback is { } feedback)
        {
            obj["feedback"] = new Obj { ["inject"] = feedback.Inject, ["limit"] = feedback.Limit };
        }

        if (s.WritesFeedback is { } writes)
        {
            obj["writes_feedback"] = new Obj { ["agent"] = writes.Agent, ["from"] = writes.From };
        }

        AddScaleConstructs(obj, s);
    }

    /// <summary>FR-357: the plain input keys, with each <c>{ from_runs: … }</c> entry back at its position (clamped to the
    /// end), so a stage without one hashes as before.</summary>
    private static List<object?> Inputs(StageDefinition s)
    {
        var list = Strings(s.Inputs);
        foreach (var runs in s.FromRuns.OrderBy(r => r.Position))
        {
            var from = new Obj
            {
                ["pipeline"] = runs.Pipeline,
                ["artifact"] = runs.Artifact,
                ["window"] = runs.Window,
                ["status"] = runs.Status.ToWire(),
            };
            list.Insert(Math.Clamp(runs.Position, 0, list.Count), new Obj { ["from_runs"] = from });
        }

        return list;
    }

    /// <summary>FR-355, FR-356 (0.8.0): map, reduce and conversation only when used, with their defaults filled, so a
    /// definition without them hashes as before.</summary>
    private static void AddScaleConstructs(Obj obj, StageDefinition s)
    {
        if (s.Map is { } map)
        {
            var m = new Obj
            {
                ["over"] = map.Over,
                ["max_parallel"] = map.MaxParallel,
                ["max_items"] = map.MaxItems,
                ["on_item_failure"] = map.OnItemFailure.ToWire(),
                ["max_failed"] = map.MaxFailed.IsPercent ? map.MaxFailed.ToWire() : map.MaxFailed.Value,
            };
            m.AddIfSet("agent", map.Agent);
            m.AddIfSet("handler", map.Handler);
            obj["map"] = m;
        }

        if (s.Reduce is { } reduce)
        {
            var r = new Obj();
            r.AddIfSet("agent", reduce.Agent);
            r.AddIfSet("handler", reduce.Handler);
            obj["reduce"] = r;
        }

        if (s.Conversation is { } conversation)
        {
            var c = new Obj
            {
                ["participants"] = conversation.Participants
                    .Select(p => (object?)new Obj { ["agent"] = p.Agent, ["role"] = p.Role }).ToList(),
                ["opener"] = conversation.Opener,
                ["max_turns"] = conversation.TurnLimit,
            };
            if (conversation.Stop is { } stop)
            {
                var st = new Obj();
                st.AddIfSet("guard", stop.Guard);
                st.AddIfSet("phrase", stop.Phrase);
                c["stop"] = st;
            }

            obj["conversation"] = c;
        }
    }

    /// <summary>D-0.7.0-42: a secondary binding with its defaults filled (inputs, tools); prompt and output only when set.</summary>
    private static Obj OnInvalid(OnInvalidPolicy onInvalid) => new()
    {
        ["retry_with_feedback"] = onInvalid.RetryWithFeedback,
        ["max_attempts"] = onInvalid.MaxAttempts,
        ["then"] = onInvalid.Then.ToWire(),
    };

    private static Obj Binding(AgentBindingDefinition b)
    {
        var obj = new Obj { ["key"] = b.Key, ["job_type"] = b.JobType, ["inputs"] = Strings(b.Inputs), ["tools"] = Strings(b.Tools) };
        obj.AddIfSet("prompt", b.Prompt);
        obj.AddIfSet("output", b.Output);

        // D-0.7.0-44: omitted when unset, so a binding without its own validation hashes as before.
        if (b.Validate.Count > 0)
        {
            obj["validate"] = b.Validate.Select(Validation).ToList();
        }

        if (b.OnInvalid is { } onInvalid)
        {
            obj["on_invalid"] = OnInvalid(onInvalid);
        }

        return obj;
    }

    private static List<object?> Requirements(IReadOnlyList<StageRequirement> requires) =>
        requires.Select(r => (object?)(r switch
        {
            ArtifactLockedRequirement locked => new Obj { ["artifact_locked"] = locked.ArtifactKey },
            NoPendingGatesRequirement none => new Obj { ["no_pending_gates"] = new Obj { ["except"] = Strings(none.Except) } },
            GuardRequirement guard => new Obj { ["guard"] = guard.Guard },
            InputRequirement input => new Obj { ["input"] = input.ArtifactKey },
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
