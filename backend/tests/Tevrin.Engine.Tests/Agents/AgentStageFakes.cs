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
