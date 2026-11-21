using Aide.Api.Agents;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Aide.UnitTests.Agents;

/// <summary>The engine's agent stage data in memory (no database).</summary>
internal sealed class MemoryAgentStageStore : IAgentStageStore
{
    public List<PipelineRun> Runs { get; } = [];

    public List<Artifact> Artifacts { get; } = [];

    public List<AgentRun> AgentRuns { get; } = [];

    public Task<PipelineRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(Runs.FirstOrDefault(r => r.Id == runId));

    public Task<IReadOnlyList<Artifact>> LatestLockedAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([.. Artifacts.Where(a => keys.Contains(a.Type) && a.Status == ArtifactStatus.Locked).GroupBy(a => a.Type).Select(g => g.MaxBy(a => a.Version)!)]);

    public Task<IReadOnlyList<ArtifactVersion>> VersionsAsync(Guid projectId, string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ArtifactVersion>>([.. Artifacts.Where(a => a.Type == key).Select(a => new ArtifactVersion(a.Version, a.Status))]);

    public Task<IReadOnlyList<Artifact>> SubjectVersionsAsync(PipelineRun run, string key, IReadOnlyCollection<int> versions, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([.. Artifacts.Where(a => a.Type == key && versions.Contains(a.Version))]);

    public Task<ApprovalGate?> PendingGateAsync(PipelineRun run, string type, CancellationToken cancellationToken) => Task.FromResult<ApprovalGate?>(null);

    public Task<AgentRun?> FindAgentRunAsync(Guid agentRunId, CancellationToken cancellationToken) => Task.FromResult(AgentRuns.FirstOrDefault(r => r.Id == agentRunId));

    public Task<Artifact?> ArtifactOfAgentRunAsync(PipelineRun run, string key, Guid agentRunId, CancellationToken cancellationToken) =>
        Task.FromResult(Artifacts.FirstOrDefault(a => a.Type == key && a.AgentRunId == agentRunId));

    public void Add(AgentRun agentRun) => AgentRuns.Add(agentRun);

    public void Add(Artifact artifact) => Artifacts.Add(artifact);

    public void Add(ApprovalGate gate)
    {
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The SDLC run data in memory: events as 0.6.1 stores them (<see cref="AideJson"/> payloads), commits, drafts.</summary>
internal sealed class MemoryRunData(MemoryAgentStageStore store) : ISdlcAgentRunData
{
    public Project Project { get; } = new() { Name = "Demo", CurrentPhase = Domain.Lifecycle.Phase.Specification, Checkpoint = 2 };

    public List<(string Type, string Payload)> Events { get; } = [];

    /// <summary>Each commit with tokens: the tokens, and the run statuses it saved.</summary>
    public List<long> TokenCommits { get; } = [];

    public bool DraftAllowed { get; set; } = true;

    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken) => Task.FromResult<Project?>(Project);

    public void AddEvent(string type, Guid projectId, object payload, DateTimeOffset at) => Events.Add((type, AideJson.Serialize(payload)));

    public async Task CommitWithTokensAsync(Guid projectId, long tokens, Func<CancellationToken, Task> save)
    {
        Project.TokensUsed += tokens;
        TokenCommits.Add(tokens);
        await save(CancellationToken.None);
    }

    public Task InvalidateCredentialAsync(AgentRun run, Guid credentialId)
    {
        Events.Add(("CONNECTION_INVALID", credentialId.ToString()));
        return Task.CompletedTask;
    }

    public Task<(Artifact? Draft, bool Added)> AddDraftAsync(Guid projectId, string type, string content, Guid agentRunId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (store.Artifacts.FirstOrDefault(a => a.AgentRunId == agentRunId && a.Type == type) is { } existing)
        {
            return Task.FromResult<(Artifact?, bool)>((existing, false));
        }

        if (!DraftAllowed)
        {
            return Task.FromResult<(Artifact?, bool)>((null, false));
        }

        var version = store.Artifacts.Where(a => a.Type == type).Select(a => a.Version).DefaultIfEmpty(0).Max() + 1;
        var draft = new Artifact
        {
            ProjectId = projectId,
            Type = type,
            Version = version,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Draft,
            AgentRunId = agentRunId,
            CreatedAt = now,
        };
        store.Artifacts.Add(draft);
        Events.Add(("ARTIFACT_CREATED", AideJson.Serialize(new { artifactId = draft.Id, type, version, agentRunId })));
        return Task.FromResult<(Artifact?, bool)>((draft, true));
    }
}

/// <summary>Always the given provider (as Fake, model <c>fake-1</c>), with a bound credential.</summary>
internal sealed class FixedResolver(ILlmProvider provider) : ILlmResolver
{
    public static readonly Guid CredentialId = Guid.CreateVersion7();

    public Task<ResolvedLlm> ResolveAsync(Guid projectId, string agent, CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedLlm(LlmProvider.Fake, "fake-1", "sk-test", CredentialId));

    public Task<LlmKey> KeyAsync(Guid projectId, LlmProvider kind, CancellationToken cancellationToken) => Task.FromResult(new LlmKey("sk-test", CredentialId));

    public ILlmProvider ProviderFor(LlmProvider kind) => provider;
}

/// <summary>A provider that throws the queued failures first, then answers with the inner provider; records each request.</summary>
internal sealed class SequenceProvider(ILlmProvider inner) : ILlmProvider
{
    public Queue<Exception> Failures { get; } = new();

    public List<LlmRequest> Requests { get; } = [];

    public LlmProvider Kind => LlmProvider.Fake;

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Failures.TryDequeue(out var failure) ? throw failure : inner.CompleteAsync(request, cancellationToken);
    }
}

/// <summary>Answers the spec generator with the scripted replies in order (the last repeats).</summary>
internal sealed class SpecReplies(params string?[] replies) : IFakeAgentResponder
{
    private int _calls;

    public string Agent => Tevrin.Pack.Sdlc.SdlcAgents.SpecGenerator;

    /// <summary>A <c>null</c> reply is the built-in Fake spec (valid).</summary>
    public string Respond(LlmRequest request) =>
        replies[Math.Min(++_calls, replies.Length) - 1] ?? new Aide.Infrastructure.Agents.FakeSpecGeneratorResponder().Respond(request);
}

internal sealed class FixedOutputLimits : ILlmOutputLimits
{
    public const int MaxTokens = 4096;

    public Task<int> ResolveAsync(LlmProvider provider, string model, string? apiKey, CancellationToken cancellationToken) => Task.FromResult(MaxTokens);
}

internal sealed class NoRepoOpener : IRepoToolOpener
{
    public Task<RepoToolContext> OpenAsync(Guid projectId, RepoScope scope, CancellationToken cancellationToken) => Task.FromResult(RepoToolContext.Empty);
}

internal sealed class NoIterationSource(MemoryRunData data) : ISdlcContextSource
{
    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken) => Task.FromResult<Project?>(data.Project);

    public Task<IterationContext> IterationAsync(Guid runId, Guid projectId, CancellationToken cancellationToken) => Task.FromResult(IterationContext.None);
}
