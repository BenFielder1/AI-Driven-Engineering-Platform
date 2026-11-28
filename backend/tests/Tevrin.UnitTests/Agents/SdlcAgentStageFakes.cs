using Tevrin.Api.Agents;
using Tevrin.Domain.Entities;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Infrastructure.Persistence.Conversion;

namespace Tevrin.UnitTests.Agents;

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

    /// <summary>The next save fails as a concurrent change would.</summary>
    public bool ConflictOnNextSave { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (ConflictOnNextSave)
        {
            ConflictOnNextSave = false;
            throw new Microsoft.EntityFrameworkCore.DbUpdateException("A concurrent change.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>The SDLC run data in memory: events as 0.6.1 stores them (<see cref="TevrinJson"/> payloads), commits, drafts.</summary>
internal sealed class MemoryRunData(MemoryAgentStageStore store) : ISdlcAgentRunData
{
    public Project Project { get; } = new() { Name = "Demo", CurrentPhase = Domain.Lifecycle.Phase.Specification, Checkpoint = 2 };

    public List<(string Type, string Payload)> Events { get; } = [];

    /// <summary>Each commit with tokens: the tokens, and the run statuses it saved.</summary>
    public List<long> TokenCommits { get; } = [];

    public bool DraftAllowed { get; set; } = true;

    /// <summary>Saving the next draft collides with a concurrent change.</summary>
    public bool ConflictOnDraft { get; set; }

    public int ProjectReads { get; private set; }

    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        ProjectReads++;
        return Task.FromResult<Project?>(Project);
    }

    public void AddEvent(string type, Guid projectId, object payload, DateTimeOffset at) => Events.Add((type, TevrinJson.Serialize(payload)));

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
        store.ConflictOnNextSave = ConflictOnDraft;
        Events.Add(("ARTIFACT_CREATED", TevrinJson.Serialize(new { artifactId = draft.Id, type, version, agentRunId })));
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

/// <summary>A provider that throws the queued failures first, then answers with the inner Fake (tools too); records each request.</summary>
internal sealed class SequenceProvider(FakeLlmProvider inner) : ILlmProvider, IToolCallingLlmProvider
{
    public Queue<Exception> Failures { get; } = new();

    public List<LlmRequest> Requests { get; } = [];

    public LlmProvider Kind => LlmProvider.Fake;

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Failures.TryDequeue(out var failure) ? throw failure : inner.CompleteAsync(request, cancellationToken);
    }

    public Task<bool> SupportsToolsAsync(LlmRequest request, CancellationToken cancellationToken) => inner.SupportsToolsAsync(request, cancellationToken);

    /// <summary>How many tool-loop turns were asked for (0: every call was a plain completion).</summary>
    public int Turns { get; private set; }

    public Task<LlmToolTurn> CompleteTurnAsync(LlmToolRequest toolRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolRequest);
        Turns++;
        Requests.Add(toolRequest.Request);
        return Failures.TryDequeue(out var failure) ? throw failure : inner.CompleteTurnAsync(toolRequest, cancellationToken);
    }
}

/// <summary>Answers an agent with the scripted replies in order (the last repeats); <c>null</c> is its built-in Fake reply (valid).</summary>
internal sealed class AgentReplies(string agent, params string?[] replies) : IFakeAgentResponder
{
    private int _calls;

    public string Agent => agent;

    public string Respond(LlmRequest request) =>
        replies[Math.Min(++_calls, replies.Length) - 1] ?? BuiltIn(agent).Respond(request);

    private static IFakeAgentResponder BuiltIn(string agent) => agent switch
    {
        Tevrin.Pack.Sdlc.SdlcAgents.IdeaRefinement => new Tevrin.Infrastructure.Agents.FakeIdeaRefinementResponder(),
        Tevrin.Pack.Sdlc.SdlcAgents.StackAnalyst => new Tevrin.Infrastructure.Agents.FakeStackAnalystResponder(),
        Tevrin.Pack.Sdlc.SdlcAgents.CodeReview => new Tevrin.Infrastructure.Agents.FakeCodeReviewResponder(),
        Tevrin.Pack.Sdlc.SdlcAgents.PlanGenerator => new Tevrin.Infrastructure.Agents.FakePlanGeneratorResponder(),
        Tevrin.Pack.Sdlc.SdlcAgents.RepositoryAnalyst => new Tevrin.Infrastructure.Agents.FakeRepositoryAnalystResponder(),
        Tevrin.Pack.Sdlc.SdlcAgents.BuildFix => new Tevrin.Infrastructure.Agents.FakeBuildFixResponder(),
        Tevrin.Pack.Sdlc.SdlcAgents.CodeGenerator => new Tevrin.Infrastructure.Agents.FakeCodeGeneratorResponder(),
        _ => new Tevrin.Infrastructure.Agents.FakeSpecGeneratorResponder(),
    };
}

internal sealed class FixedOutputLimits : ILlmOutputLimits
{
    public const int MaxTokens = 4096;

    public Task<int> ResolveAsync(LlmProvider provider, string model, string? apiKey, CancellationToken cancellationToken) => Task.FromResult(MaxTokens);
}

/// <summary>The repo tools an attempt opens (default: none), and how often they were opened.</summary>
internal sealed class TestRepoOpener : IRepoToolOpener
{
    public Func<RepoToolContext> Open { get; set; } = () => RepoToolContext.Empty;

    public int Opened { get; private set; }

    public Task<RepoToolContext> OpenAsync(Guid projectId, RepoScope scope, CancellationToken cancellationToken)
    {
        Opened++;
        return Task.FromResult(Open());
    }
}

/// <summary>A fixed stage target.</summary>
internal sealed class FixedLocator(SdlcStageTarget target) : ISdlcStageLocator
{
    public Task<SdlcStageTarget> LocateAsync(Guid projectId, string agent, CancellationToken cancellationToken) => Task.FromResult(target);
}

internal sealed class NoIterationSource(MemoryRunData data) : ISdlcContextSource
{
    /// <summary>How often a context provider read the project or the iteration.</summary>
    public int Reads { get; private set; }

    public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<Project?>(data.Project);
    }

    public Task<IterationContext> IterationAsync(Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(IterationContext.None);
    }
}
