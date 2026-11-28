using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tevrin.Infrastructure.Persistence;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Characterisation;

/// <summary>
/// FR-260, §4.58: reads what a scenario left in the database for its project and writes the characterisation snapshot.
/// It reads the tables with SQL through the factory's <see cref="TevrinDbContext"/> connection, so it depends only on table
/// and column names and on the stored (wire) values, never on the C# types the engine refactor moves or renames.
/// <list type="bullet">
/// <item><c>project</c>: phase, status, checkpoint, review cycle, extra cycles, approval mode, released/development version;</item>
/// <item><c>transitions</c>: from/to phase, checkpoint, reason, actor;</item>
/// <item><c>gates</c>: type, status, decision, target checkpoint, whether a comment was given, the gated artifact, the reviewer;</item>
/// <item><c>artifacts</c>: type, version, parent version, status, content hash, checkpoint at creation, restored-from version, author;</item>
/// <item><c>jobs</c> (not the <c>notification</c> outbox, which tests never run): type, the payload's <c>trigger</c>, final status, attempts;</item>
/// <item><c>agentRuns</c>: agent, prompt version, status, phase;</item>
/// <item><c>events</c>: type, actor and the payload without timing, token, cost and lease fields.</item>
/// </list>
/// Each list is in creation order: <c>created_at</c>, then (for rows written in one transaction, which share it) a key with
/// every run-specific value blanked. Then <see cref="SnapshotNormaliser"/> replaces ids, SHAs, timestamps and literals.
/// </summary>
internal static partial class SnapshotRecorder
{
    private const string ProjectSql =
        "SELECT slug, current_phase, status, checkpoint, review_cycle, extra_review_cycles, approval_mode, released_version, development_version FROM projects WHERE id = @project";

    private const string TransitionsSql =
        "SELECT id, created_at, from_phase, to_phase, checkpoint, reason, actor FROM state_transitions WHERE project_id = @project ORDER BY created_at, id";

    private const string GatesSql =
        "SELECT id, created_at, type, status, decision, target_checkpoint, comment IS NOT NULL AS has_comment, artifact_id, reviewer_id FROM approval_gates WHERE project_id = @project ORDER BY created_at, id";

    private const string ArtifactsSql =
        "SELECT id, created_at, type, version, parent_version, status, content, content_hash, checkpoint_at_creation, restored_from_version, created_by_user_id, agent_run_id FROM artifacts WHERE project_id = @project ORDER BY created_at, id";

    private const string JobsSql =
        "SELECT id, created_at, type, payload::text AS payload, status, attempts FROM jobs WHERE project_id = @project AND type <> 'notification' ORDER BY created_at, id";

    private const string AgentRunsSql =
        "SELECT id, created_at, agent, phase, prompt_version, status FROM agent_runs WHERE project_id = @project ORDER BY created_at, id";

    private const string EventsSql =
        "SELECT id, created_at, type, actor, payload::text AS payload FROM events WHERE project_id = @project ORDER BY created_at, id";

    /// <summary>Other rows of the project whose ids may appear in payloads, so they are labelled by kind.</summary>
    private const string OtherIdsSql = """
        SELECT id, 'release_version' AS kind FROM release_versions WHERE project_id = @project
        UNION ALL SELECT id, 'step' FROM execution_steps WHERE project_id = @project
        UNION ALL SELECT id, 'release' FROM releases WHERE project_id = @project
        UNION ALL SELECT id, 'report' FROM compliance_reports WHERE project_id = @project
        UNION ALL SELECT id, 'build_check' FROM build_checks WHERE project_id = @project
        UNION ALL SELECT id, 'rollback' FROM rollback_events WHERE project_id = @project
        UNION ALL SELECT id, 'deployment' FROM deployments WHERE project_id = @project
        """;

    private const string NormalisedHashPrefix = "normalised:";

    private const string PhaseEnteredTrigger = "phase_entered";
    private const string StageEnteredTrigger = "stage_entered";

    private static readonly string[] VolatileKeyParts = ["duration", "latency", "elapsed", "token", "cost", "leaseowner", "lockedby", "workerid", "etag", "timestamp"];

    public static async Task<string> RecordAsync(SdlcFlow flow, string scenario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var tables = await ProjectApi.WithDbAsync(flow.App, db => ReadAsync(db, flow.Id, cancellationToken));

        var normaliser = new SnapshotNormaliser();
        foreach (var (value, replacement) in flow.Literals)
        {
            normaliser.AddLiteral(value, replacement);
        }

        normaliser.AddLiteral(tables.Project["slug"] as string, "<slug>");
        normaliser.SetLabel(flow.Id, "project#1");
        foreach (var user in flow.Users)
        {
            normaliser.SetLabel(user.Id, $"user:{user.Role}");
            normaliser.AddLiteral(user.Email, $"{user.Role}@example.test");
        }

        var transitions = Ordered(tables.Transitions.Select(Transition));
        var gates = Ordered(tables.Gates.Select(Gate));
        var artifacts = Ordered(tables.Artifacts.Select(Artifact));
        var jobs = Ordered(tables.Jobs.Select(Job));
        var agentRuns = Ordered(tables.AgentRuns.Select(AgentRun));
        var events = Ordered(tables.Events.Select(Event));

        // Ids are numbered per kind in creation order (a job and its agent run share an id: it keeps the job's label).
        Register(normaliser, transitions, "transition");
        Register(normaliser, gates, "gate");
        Register(normaliser, artifacts, "artifact");
        Register(normaliser, jobs, "job");
        Register(normaliser, agentRuns, "agent_run");
        Register(normaliser, events, "event");
        foreach (var row in tables.Others)
        {
            normaliser.SetKind((Guid)row["id"]!, (string)row["kind"]!);
        }

        var root = new JsonObject
        {
            ["scenario"] = scenario,
            ["project"] = Project(tables.Project),
            ["transitions"] = Items(transitions),
            ["gates"] = Items(gates),
            ["artifacts"] = Items(artifacts),
            ["jobs"] = Items(jobs),
            ["agentRuns"] = Items(agentRuns),
            ["events"] = Items(events),
        };

        foreach (var text in Strings(root).Concat(artifacts.Select(a => a.Content ?? string.Empty)))
        {
            normaliser.CollectShas(text);
        }

        // Content hashes stay (the Fake provider is deterministic). Content that holds a run-specific value (an id, a commit
        // SHA, a date) is hashed after normalisation instead, marked "normalised:".
        foreach (var artifact in artifacts)
        {
            var content = artifact.Content ?? string.Empty;
            var normalised = normaliser.Normalise(content);
            if (!string.Equals(normalised, content, StringComparison.Ordinal))
            {
                artifact.Node["contentHash"] = NormalisedHashPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
            }
        }

        return SnapshotJson.Write(Normalise(root, normaliser));
    }

    // ---------- rows → entries ----------

    private sealed record Entry(long Ticks, Guid Id, JsonObject Node, string? Content = null);

    private static JsonObject Project(Dictionary<string, object?> row) => new()
    {
        ["phase"] = Value(row["current_phase"]),
        ["status"] = Value(row["status"]),
        ["checkpoint"] = Value(row["checkpoint"]),
        ["reviewCycle"] = Value(row["review_cycle"]),
        ["extraReviewCycles"] = Value(row["extra_review_cycles"]),
        ["approvalMode"] = Value(row["approval_mode"]),
        ["releasedVersion"] = Value(row["released_version"]),
        ["developmentVersion"] = Value(row["development_version"]),
    };

    private static Entry Transition(Dictionary<string, object?> row) => new(Ticks(row), (Guid)row["id"]!, new JsonObject
    {
        ["id"] = Value(row["id"]),
        ["from"] = Value(row["from_phase"]),
        ["to"] = Value(row["to_phase"]),
        ["checkpoint"] = Value(row["checkpoint"]),
        ["reason"] = Value(row["reason"]),
        ["actor"] = Value(row["actor"]),
    });

    private static Entry Gate(Dictionary<string, object?> row) => new(Ticks(row), (Guid)row["id"]!, new JsonObject
    {
        ["id"] = Value(row["id"]),
        ["type"] = Value(row["type"]),
        ["status"] = Value(row["status"]),
        ["decision"] = Value(row["decision"]),
        ["targetCheckpoint"] = Value(row["target_checkpoint"]),
        ["hasComment"] = Value(row["has_comment"]),
        ["artifact"] = Value(row["artifact_id"]),
        ["reviewer"] = Value(row["reviewer_id"]),
    });

    private static Entry Artifact(Dictionary<string, object?> row) => new(Ticks(row), (Guid)row["id"]!, new JsonObject
    {
        ["id"] = Value(row["id"]),
        ["type"] = Value(row["type"]),
        ["version"] = Value(row["version"]),
        ["parentVersion"] = Value(row["parent_version"]),
        ["status"] = Value(row["status"]),
        ["contentHash"] = Value(row["content_hash"]),
        ["checkpointAtCreation"] = Value(row["checkpoint_at_creation"]),
        ["restoredFromVersion"] = Value(row["restored_from_version"]),
        ["createdBy"] = Value(row["created_by_user_id"]),
        ["agentRun"] = Value(row["agent_run_id"]),
    }, row["content"] as string);

    private static Entry Job(Dictionary<string, object?> row) => new(Ticks(row), (Guid)row["id"]!, new JsonObject
    {
        ["id"] = Value(row["id"]),
        ["type"] = Value(row["type"]),
        ["trigger"] = Trigger(row["payload"] as string),
        ["status"] = Value(row["status"]),
        ["attempts"] = Value(row["attempts"]),
    });

    private static Entry AgentRun(Dictionary<string, object?> row) => new(Ticks(row), (Guid)row["id"]!, new JsonObject
    {
        ["id"] = Value(row["id"]),
        ["agent"] = Value(row["agent"]),
        ["promptVersion"] = Value(row["prompt_version"]),
        ["status"] = Value(row["status"]),
        ["phase"] = Value(row["phase"]),
    });

    private static Entry Event(Dictionary<string, object?> row) => new(Ticks(row), (Guid)row["id"]!, new JsonObject
    {
        ["id"] = Value(row["id"]),
        ["type"] = Value(row["type"]),
        ["actor"] = Value(row["actor"]),
        ["payload"] = Strip(Parse(row["payload"] as string)),
    });

    private static JsonValue? Trigger(string? payload) =>
        Parse(payload) is JsonObject obj && obj["trigger"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? JsonValue.Create(TriggerName(value.GetValue<string>()))
            : null;

    /// <summary>
    /// FR-273 (M56): new payloads write the generic <c>stage_entered</c> where 0.6.1 wrote <c>phase_entered</c> (still read with
    /// the same meaning), so the snapshot shows the generic name for both.
    /// </summary>
    private static string TriggerName(string trigger) =>
        string.Equals(trigger, PhaseEnteredTrigger, StringComparison.Ordinal) ? StageEnteredTrigger : trigger;

    private static JsonNode? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);

    /// <summary>The payload without timings, durations, tokens, costs, ETags and lease owners (§4.58).</summary>
    private static JsonNode? Strip(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Where(p => !IsVolatile(p.Key)).Select(p => KeyValuePair.Create(p.Key, StripMember(p.Key, p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Strip)]),
        _ => node.DeepClone(),
    };

    private static JsonNode? StripMember(string key, JsonNode? value) =>
        string.Equals(key, "trigger", StringComparison.Ordinal) && value is JsonValue text && text.GetValueKind() == JsonValueKind.String
            ? JsonValue.Create(TriggerName(text.GetValue<string>()))
            : Strip(value);

    private static bool IsVolatile(string key) =>
        (key.Length > 2 && key.EndsWith("At", StringComparison.Ordinal))
        || key.EndsWith("Ms", StringComparison.Ordinal)
        || key.EndsWith("Seconds", StringComparison.Ordinal)
        || key.EndsWith("Since", StringComparison.Ordinal)
        || key.EndsWith("Until", StringComparison.Ordinal)
        || key is "at" or "since" or "until" or "runAfter"
        || VolatileKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static JsonValue? Value(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        Guid id => JsonValue.Create(id.ToString()),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        short number => JsonValue.Create((int)number),
        bool flag => JsonValue.Create(flag),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static long Ticks(Dictionary<string, object?> row) => row["created_at"] switch
    {
        DateTime time => time.ToUniversalTime().Ticks,
        DateTimeOffset time => time.UtcTicks,
        _ => 0,
    };

    private static List<Entry> Ordered(IEnumerable<Entry> entries) =>
        [.. entries.OrderBy(e => e.Ticks).ThenBy(e => SnapshotNormaliser.Blind(SnapshotJson.WriteCompact(e.Node)), StringComparer.Ordinal)];

    private static void Register(SnapshotNormaliser normaliser, List<Entry> entries, string kind)
    {
        foreach (var entry in entries)
        {
            normaliser.SetKind(entry.Id, kind);
            _ = normaliser.Label(entry.Id);
        }
    }

    private static JsonArray Items(List<Entry> entries) => [.. entries.Select(e => (JsonNode?)e.Node)];

    // ---------- normalisation of the tree ----------

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    yield return key;
                    foreach (var text in Strings(value))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonArray array:
                foreach (var text in array.SelectMany(Strings))
                {
                    yield return text;
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                yield return value.GetValue<string>();
                break;
            default:
                break;
        }
    }

    /// <summary>A copy of the tree with every key and string normalised, visited in output order (sorted keys).</summary>
    private static JsonNode? Normalise(JsonNode? node, SnapshotNormaliser normaliser)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var result = new JsonObject();
                // Members are visited by key, then value, with every run-specific value blanked, so ids used as keys (e.g. a
                // map keyed by artifact id) are labelled in an order that doesn't depend on the raw ids of the run.
                foreach (var (key, value) in obj
                    .OrderBy(p => SnapshotNormaliser.Blind(p.Key), StringComparer.Ordinal)
                    .ThenBy(p => SnapshotNormaliser.Blind(SnapshotJson.WriteCompact(p.Value)), StringComparer.Ordinal)
                    .ThenBy(p => p.Key, StringComparer.Ordinal))
                {
                    result[normaliser.Normalise(key)] = Normalise(value, normaliser);
                }

                return result;
            case JsonArray array:
                var items = array.Select(item => Normalise(item, normaliser)).ToList();
                if (items.Count > 1 && items.All(item => IdLabelKey(item) is not null))
                {
                    // D-0.7.0-22: a list made only of id labels is a set (e.g. resetStepIds, invalidated artifact ids,
                    // recipients), often read with no ORDER BY: sorted by kind, then number (users by role).
                    items = [.. items.OrderBy(item => IdLabelKey(item)!.Value.Kind, StringComparer.Ordinal)
                        .ThenBy(item => IdLabelKey(item)!.Value.Number)
                        .ThenBy(item => IdLabelKey(item)!.Value.Role, StringComparer.Ordinal)];
                }

                var list = new JsonArray();
                foreach (var item in items)
                {
                    list.Add(item);
                }

                return list;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(normaliser.Normalise(value.GetValue<string>()));
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// The sort key of a normalised id label: <c>&lt;kind&gt;#n</c> (any kind but <c>sha</c> and <c>hex</c>, whose lists, e.g.
    /// commits, keep their order) or <c>user:&lt;role&gt;</c>; <c>null</c> for anything else.
    /// </summary>
    private static (string Kind, int Number, string Role)? IdLabelKey(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetValue<string>();
        if (UserLabel().Match(text) is { Success: true } user)
        {
            return ("user:", 0, user.Groups["role"].Value);
        }

        return IdLabel().Match(text) is { Success: true } id && id.Groups["kind"].Value is not ("sha" or "hex")
            && int.TryParse(id.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (id.Groups["kind"].Value, n, string.Empty)
            : null;
    }

    [GeneratedRegex("^(?<kind>[a-z_]+)#(?<n>[0-9]+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdLabel();

    [GeneratedRegex("^user:(?<role>[a-z]+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserLabel();

    // ---------- reading ----------

    private sealed record Tables(
        Dictionary<string, object?> Project,
        List<Dictionary<string, object?>> Transitions,
        List<Dictionary<string, object?>> Gates,
        List<Dictionary<string, object?>> Artifacts,
        List<Dictionary<string, object?>> Jobs,
        List<Dictionary<string, object?>> AgentRuns,
        List<Dictionary<string, object?>> Events,
        List<Dictionary<string, object?>> Others);

    private static async Task<Tables> ReadAsync(TevrinDbContext db, Guid projectId, CancellationToken cancellationToken) => new(
        Assert.Single(await QueryAsync(db, ProjectSql, projectId, cancellationToken)),
        await QueryAsync(db, TransitionsSql, projectId, cancellationToken),
        await QueryAsync(db, GatesSql, projectId, cancellationToken),
        await QueryAsync(db, ArtifactsSql, projectId, cancellationToken),
        await QueryAsync(db, JobsSql, projectId, cancellationToken),
        await QueryAsync(db, AgentRunsSql, projectId, cancellationToken),
        await QueryAsync(db, EventsSql, projectId, cancellationToken),
        await QueryAsync(db, OtherIdsSql, projectId, cancellationToken));

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(TevrinDbContext db, string sql, Guid projectId, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "project";
        parameter.Value = projectId;
        command.Parameters.Add(parameter);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }
}
