using System.Text.Json;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>The agent stage data in memory (the offline engine context never connects).</summary>
internal sealed class InMemoryAgentStageStore : IAgentStageStore
{
    public List<PipelineRun> Runs { get; } = [];

    public List<Artifact> Artifacts { get; } = [];

    public List<AgentRun> AgentRuns { get; } = [];

    public List<ApprovalGate> Gates { get; } = [];

    public int Saves { get; private set; }

    public Task<PipelineRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Id == runId));

    /// <summary>FR-351: the agent feedback entries (pipeline, agent key, project).</summary>
    public List<AgentFeedback> Feedback { get; } = [];

    public Task<IReadOnlyList<FeedbackEntry>> FeedbackAsync(PipelineRun run, string pipelineId, string agentKey, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>([.. Feedback
            .Where(f => f.ProjectId == run.ProjectId && f.PipelineId == pipelineId && f.AgentKey == agentKey && f.DeletedAt is null)
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id)
            .Take(max)
            .Select(f => new FeedbackEntry(f.Id, f.Source == AgentFeedbackSource.Stage ? "stage" : "human", f.Text, f.CreatedAt, Deleted: false))]);

    public Task<IReadOnlyList<Artifact>> LatestLockedAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var inSubject = AgentStageScope.InSubject(run).Compile();
        return Task.FromResult<IReadOnlyList<Artifact>>([.. Artifacts
            .Where(a => inSubject(a) && keys.Contains(a.Type) && a.Status == ArtifactStatus.Locked)
            .GroupBy(a => a.Type)
            .Select(g => g.MaxBy(a => a.Version)!)]);
    }

    public Task<IReadOnlyList<ArtifactVersion>> VersionsAsync(Guid projectId, string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ArtifactVersion>>([.. Artifacts.Where(a => a.ProjectId == projectId && a.Type == key).Select(a => new ArtifactVersion(a.Version, a.Status))]);

    public Task<IReadOnlyList<Artifact>> SubjectVersionsAsync(PipelineRun run, string key, IReadOnlyCollection<int> versions, CancellationToken cancellationToken)
    {
        var inSubject = AgentStageScope.InSubject(run).Compile();
        return Task.FromResult<IReadOnlyList<Artifact>>([.. Artifacts.Where(a => inSubject(a) && a.Type == key && versions.Contains(a.Version))]);
    }

    public Task<ApprovalGate?> PendingGateAsync(PipelineRun run, string type, CancellationToken cancellationToken) =>
        Task.FromResult(Gates.FirstOrDefault(g => g.PipelineRunId == run.Id && g.Type == type && g.Status == GateStatus.Pending));

    public Task<AgentRun?> FindAgentRunAsync(Guid agentRunId, CancellationToken cancellationToken) =>
        Task.FromResult(AgentRuns.FirstOrDefault(r => r.Id == agentRunId));

    public Task<Artifact?> ArtifactOfAgentRunAsync(PipelineRun run, string key, Guid agentRunId, CancellationToken cancellationToken)
    {
        var inSubject = AgentStageScope.InSubject(run).Compile();
        return Task.FromResult(Artifacts.FirstOrDefault(a => inSubject(a) && a.Type == key && a.AgentRunId == agentRunId));
    }

    /// <summary>FR-357: the store's half of a <c>from_runs</c> read (the SQL's filter, order and cap), over <see cref="Runs"/>
    /// and <see cref="Artifacts"/>; each call is recorded in <see cref="RunsReads"/>.</summary>
    public List<(RunsInputDefinition Input, DateTimeOffset Since)> RunsReads { get; } = [];

    public Task<IReadOnlyList<RunsInputCandidate>> RunsOutputsAsync(PipelineRun run, RunsInputDefinition input, DateTimeOffset since, CancellationToken cancellationToken)
    {
        RunsReads.Add((input, since));
        Artifact? Final(PipelineRun r) => Artifacts
            .Where(a => a.PipelineRunId == r.Id && a.ProjectId == run.ProjectId && a.Type == input.Artifact
                && (a.Variant is null || FollowUpRules.IsLabel(a.Variant)) && a.ItemIndex is null && a.Status != ArtifactStatus.Superseded)
            .OrderBy(a => a.Variant is null ? 0 : 1).ThenByDescending(a => a.Version).ThenByDescending(a => a.CreatedAt)
            .FirstOrDefault();
        return Task.FromResult<IReadOnlyList<RunsInputCandidate>>([.. Runs
            .Where(r => r.ProjectId == run.ProjectId && r.PipelineId == input.Pipeline && r.Status == input.Status && r.Id != run.Id
                && r.CompletedAt >= since && Final(r) is not null)
            .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt)
            .Take(RunsInputDefinition.MaxRuns)
            .Select(r => (Run: r, Final: Final(r)!))
            .Select(x => new RunsInputCandidate(x.Run.Id, x.Run.PipelineId, x.Run.Status, x.Run.StartedAt, x.Run.CompletedAt, x.Final.Content)
            {
                ArtifactStatus = x.Final.Status,
                ContentHash = x.Final.ContentHash,
            })]);
    }

    /// <summary>FR-346: as the database store, the run's newest non-superseded plain version of each key (integrity unchecked).</summary>
    public Task<IReadOnlyList<Artifact>> RunLatestAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([.. Artifacts
            .Where(a => a.PipelineRunId == run.Id && keys.Contains(a.Type) && a.Variant is null && a.ItemIndex is null && a.Status != ArtifactStatus.Superseded)
            .GroupBy(a => a.Type, StringComparer.Ordinal)
            .Select(g => g.MaxBy(a => a.Version)!)]);

    /// <summary>FR-345: as the database store, the run's newest non-superseded version of each variant (integrity unchecked).</summary>
    public Task<IReadOnlyList<Artifact>> RunVariantsAsync(PipelineRun run, string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([.. Artifacts
            .Where(a => a.PipelineRunId == run.Id && a.Type == key && a.Variant is not null && a.Status != ArtifactStatus.Superseded)
            .GroupBy(a => a.Variant!, StringComparer.Ordinal)
            .Select(g => g.MaxBy(a => a.Version)!)]);

    public void Add(AgentRun agentRun) => AgentRuns.Add(agentRun);

    public void Add(Artifact artifact) => Artifacts.Add(artifact);

    public void Add(ApprovalGate gate) => Gates.Add(gate);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }
}

/// <summary>Always the Fake provider, with no key.</summary>
internal sealed class FakeResolver(FakeLlmProvider provider) : ILlmResolver
{
    public Task<ResolvedLlm> ResolveAsync(Guid projectId, string agent, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedLlm(LlmProvider.Fake, "fake-model", null));

    public Task<LlmKey> KeyAsync(Guid projectId, LlmProvider provider, CancellationToken cancellationToken) => Task.FromResult(LlmKey.None);

    public ILlmProvider ProviderFor(LlmProvider kind) => provider;
}

/// <summary>Answers an agent with the scripted outputs in order (the last repeats) and records each user message.</summary>
internal sealed class ScriptedResponder(string agent, params string[] outputs) : IFakeAgentResponder
{
    public string Agent { get; } = agent;

    public List<string> Prompts { get; } = [];

    public string Respond(LlmRequest request)
    {
        Prompts.Add(request.Messages[^1].Content);
        return outputs[Math.Min(Prompts.Count, outputs.Length) - 1];
    }
}

/// <summary><c>fixture.report-consistent</c>: a report with <c>"consistent": false</c> fails with its findings.</summary>
internal sealed class ReportConsistentGuard : IGuard
{
    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        var output = context.Facts.Get<StageOutputFact>();
        try
        {
            using var json = JsonDocument.Parse(output?.Content ?? "{}");
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("consistent", out var consistent) && consistent.ValueKind == JsonValueKind.False)
            {
                var findings = root.TryGetProperty("findings", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(f => f.ToString()).ToArray()
                    : ["inconsistent"];
                return Task.FromResult(GuardResult.Fail(findings));
            }
        }
        catch (JsonException)
        {
            // The schema reports output that isn't JSON.
        }

        return Task.FromResult(GuardResult.Pass);
    }
}

/// <summary>Context for the fixture pipeline's prompts; counts its calls.</summary>
internal sealed class NotesContextProvider(string pipelineId = "fixture-review") : IAgentContextProvider
{
    public const string Notes = "Use British spelling.";

    public string PipelineId { get; } = pipelineId;

    public List<AgentContextRequest> Requests { get; } = [];

    public Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult<IReadOnlyList<AgentContextSection>>([new AgentContextSection("notes", Notes)]);
    }
}

/// <summary>Records every hook call; refuses runs when <see cref="Refuse"/> is set.</summary>
internal sealed class RecordingHooks : IAgentStageHooks
{
    public string? Refuse { get; set; }

    public List<string> Calls { get; } = [];

    public long Tokens { get; private set; }

    public Task<string?> CheckBudgetAsync(PipelineRun run, string agentKey, CancellationToken cancellationToken)
    {
        Calls.Add($"budget:{agentKey}");
        return Task.FromResult(Refuse);
    }

    public Task OnTokensUsedAsync(PipelineRun run, AgentRun agentRun, long tokens, CancellationToken cancellationToken)
    {
        Tokens += tokens;
        Calls.Add($"tokens:{agentRun.Agent}");
        return Task.CompletedTask;
    }

    public Task OnArtifactCreatedAsync(PipelineRun run, Artifact artifact, CancellationToken cancellationToken)
    {
        Calls.Add($"created:{artifact.Type}:{artifact.Version}");
        return Task.CompletedTask;
    }

    public Task OnArtifactSupersededAsync(PipelineRun run, Artifact superseded, int byVersion, CancellationToken cancellationToken)
    {
        Calls.Add($"superseded:{superseded.Type}:{superseded.Version}>{byVersion}");
        return Task.CompletedTask;
    }

    public Task OnGateCreatedAsync(PipelineRun run, ApprovalGate gate, string reason, CancellationToken cancellationToken)
    {
        Calls.Add($"gate:{gate.Type}:{reason}");
        return Task.CompletedTask;
    }
}

/// <summary>A provider that "streams": it marks the first-token probe, then answers or throws.</summary>
internal sealed class StreamingProvider(string answer, Exception? failure = null) : ILlmProvider
{
    public LlmProvider Kind => LlmProvider.Fake;

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        LlmFirstTokenProbe.MarkFirstToken();
        if (failure is not null)
        {
            throw failure;
        }

        return Task.FromResult(new LlmCompletion(answer, 10, 5, "end_turn"));
    }
}

/// <summary>A resolver with a key, over any provider.</summary>
internal sealed class KeyedResolver(ILlmProvider provider, string apiKey) : ILlmResolver
{
    public Task<ResolvedLlm> ResolveAsync(Guid projectId, string agent, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedLlm(LlmProvider.Anthropic, "model-x", apiKey));

    public Task<LlmKey> KeyAsync(Guid projectId, LlmProvider kind, CancellationToken cancellationToken) => Task.FromResult(new LlmKey(apiKey, null));

    public ILlmProvider ProviderFor(LlmProvider kind) => provider;
}
