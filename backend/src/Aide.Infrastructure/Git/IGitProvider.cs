using Aide.Domain.Entities;

namespace Aide.Infrastructure.Git;

/// <summary>The saved Git settings of a project, as a provider needs them (never includes the token).</summary>
public sealed record GitConnectionSettings(Guid ProjectId, string? RepoUrl, string DefaultBranch);

/// <summary>Outcome of a connection test (FR-10). <see cref="Reason"/> explains a failure in the provider's words.</summary>
public sealed record GitTestResult(bool Ok, bool Reachable, bool Writable, bool DefaultBranchExists, string? Reason)
{
    public static GitTestResult Failed(string reason, bool reachable = false, bool writable = false, bool defaultBranchExists = false) =>
        new(false, reachable, writable, defaultBranchExists, reason);
}

/// <summary>
/// One Git hosting provider (FR-35, D12): GitHub and Local in v1; GitLab can be added later (§11 Q1) by implementing
/// this interface. Tokens are passed in decrypted per call and never stored, logged or echoed in results.
/// </summary>
/// <remarks>
/// Phase 9 adds the working-copy operations here (FR-34–FR-36, FR-42, FR-43), roughly:
/// <c>PrepareWorkingCopyAsync</c> (clone or init the repo under the workspace and create the project branch),
/// <c>CommitAsync</c> (write a step's files and commit them), <c>PushAsync</c>, <c>OpenPullRequestAsync</c>,
/// <c>MergePullRequestAsync</c> (reporting conflicting files) and <c>CreateTagAsync</c>.
/// </remarks>
public interface IGitProvider
{
    GitProvider Kind { get; }

    /// <summary>Checks that the repository is reachable and writable and that the default branch exists. Changes nothing.</summary>
    Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken);
}

/// <summary>Picks the <see cref="IGitProvider"/> for a project's provider setting.</summary>
public interface IGitProviderResolver
{
    IGitProvider Resolve(GitProvider provider);
}

internal sealed class GitProviderResolver(IEnumerable<IGitProvider> providers) : IGitProviderResolver
{
    private readonly Dictionary<GitProvider, IGitProvider> _providers = providers.ToDictionary(p => p.Kind);

    public IGitProvider Resolve(GitProvider provider) =>
        _providers.TryGetValue(provider, out var implementation)
            ? implementation
            : throw new NotSupportedException($"Git provider {provider} is not available.");
}
