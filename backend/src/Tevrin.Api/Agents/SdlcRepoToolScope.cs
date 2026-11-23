using Tevrin.Infrastructure.RepoTools;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Agents;

/// <summary>FR-317: opens the repo snapshot of an agent run (a seam over <see cref="AgentRepoContext"/>).</summary>
public interface IRepoToolOpener
{
    Task<RepoToolContext> OpenAsync(Guid projectId, RepoScope scope, CancellationToken cancellationToken);
}

/// <summary>FR-71: <see cref="IRepoToolOpener"/> through <see cref="AgentRepoContext"/>.</summary>
public sealed class AgentRepoToolOpener(AgentRepoContext repoContext) : IRepoToolOpener
{
    public Task<RepoToolContext> OpenAsync(Guid projectId, RepoScope scope, CancellationToken cancellationToken) =>
        repoContext.OpenAsync(projectId, scope, cancellationToken);
}

/// <summary>
/// FR-317, FR-71: the repo tools of the agent attempt in progress in this DI scope. <see cref="Prepare"/> (from
/// <see cref="SdlcRepoContextProvider"/>) only records which repository the attempt's tools see; nothing is opened, so an
/// attempt that fails before the call (<c>MISSING_INPUT</c>, <c>PROVIDER_NOT_CONFIGURED</c>) never clones or fetches. The
/// snapshot is opened by <see cref="OpenAsync"/>: explicitly by the SDLC extension just before the call (0.6.1 opened it
/// after the key check), or on the first tool call. <see cref="Release"/> disposes it after the call. The three SDLC
/// tools (<see cref="SdlcRepoTool"/>) share one run's <see cref="RepoToolSession"/>: its budget and call log.
/// </summary>
public sealed class SdlcRepoToolScope(IRepoToolOpener opener) : IDisposable
{
    private (Guid ProjectId, RepoScope Scope)? _target;
    private bool _attempted;
    private RepoToolContext? _owned;
    private RepoToolSession? _session;
    private string? _releasedCalls;

    /// <summary>Whether a snapshot (or a caller's session) is open now.</summary>
    public bool IsOpen => _session is not null;

    /// <summary><c>snapshot_unavailable</c> when the attempt's snapshot couldn't be made, else <c>null</c> (known once opened).</summary>
    public string? Warning { get; private set; }

    /// <summary>The next attempt's tools see <paramref name="agent"/>'s repository (<see cref="ScopeOf"/>); releases the previous one.</summary>
    public void Prepare(Guid projectId, string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Reset();
        var scope = ScopeOf(agent);
        _target = scope == RepoScope.None ? null : (projectId, scope);
    }

    /// <summary>Uses <paramref name="session"/> (not owned: the caller manages its directory) for the next attempt.</summary>
    public void Use(RepoToolSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Reset();
        _attempted = true;
        _session = session;
    }

    /// <summary>
    /// The attempt's tools, opening the prepared snapshot once (<c>null</c>: nothing prepared, no snapshot, or released).
    /// </summary>
    public async Task<RepoToolSession?> OpenAsync(CancellationToken cancellationToken)
    {
        if (_session is not null || _attempted || _target is not { } target)
        {
            return _session;
        }

        _attempted = true;
        var context = await opener.OpenAsync(target.ProjectId, target.Scope, cancellationToken);
        _owned = context;
        _session = context.Tools;
        Warning = context.Warning;
        return _session;
    }

    /// <summary>After the call: keeps the call log and disposes the snapshot (the attempt's tools are done).</summary>
    public void Release()
    {
        _releasedCalls = _session?.ToJson() ?? _releasedCalls;
        _session = null;
        var owned = _owned;
        _owned = null;
        owned?.Dispose();
    }

    /// <summary>The attempt's <c>tool_calls</c> log (<see cref="RepoToolSession.ToJson"/>), or <c>null</c> without tools.</summary>
    public string? ToolCallsJson() => _session?.ToJson() ?? _releasedCalls;

    public void Dispose() => Release();

    private void Reset()
    {
        Release();
        _target = null;
        _attempted = false;
        _releasedCalls = null;
        Warning = null;
    }

    /// <summary>
    /// FR-71: which repository an SDLC agent's repo tools see, as its 0.6.1 job handler chose: the base commit for the
    /// analysts, refinement, spec and plan; the project branch's working tree for code generation, review and build fix.
    /// </summary>
    public static RepoScope ScopeOf(string agent) => agent switch
    {
        SdlcAgents.RepositoryAnalyst or SdlcAgents.StackAnalyst or SdlcAgents.IdeaRefinement or SdlcAgents.SpecGenerator
            or SdlcAgents.PlanGenerator => RepoScope.BaseCommit,
        SdlcAgents.CodeGenerator or SdlcAgents.CodeReview or SdlcAgents.BuildFix => RepoScope.WorkingTree,
        _ => RepoScope.None,
    };
}
