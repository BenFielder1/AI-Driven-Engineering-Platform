using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>The initial commit's push failed; <see cref="Exception.Message"/> is git's error with every secret redacted.</summary>
public sealed class BitbucketInitialPushException : Exception
{
    public BitbucketInitialPushException()
    {
    }

    public BitbucketInitialPushException(string message)
        : base(message)
    {
    }

    public BitbucketInitialPushException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>git couldn't authenticate (the token was refused).</summary>
    public bool Unauthorized { get; init; }
}

/// <summary>FR-135: gives a new, empty Bitbucket repository its default branch.</summary>
public interface IBitbucketRepoInitializer
{
    /// <summary>
    /// Pushes one commit adding <c>README.md</c> to <c>main</c> of <paramref name="fullName"/> (<c>workspace/slug</c>), with
    /// the connection's token in the per-command <c>http.extraHeader</c> only. Throws
    /// <see cref="BitbucketInitialPushException"/> when the push fails.
    /// </summary>
    Task PushReadmeAsync(string fullName, string name, string? description, GitCredentialKind credential, string token, CancellationToken cancellationToken);
}

/// <summary>
/// FR-135, FR-136: Bitbucket has no auto-init, so AIDE makes the first commit itself in a throw-away repository (a temp
/// folder, removed afterwards) and pushes it to <c>main</c> over HTTPS (<see cref="GitHostRemotes"/>): the token goes only in
/// the push's <c>http.extraHeader</c> (Basic <c>x-token-auth</c> for OAuth, <c>x-bitbucket-api-token-auth</c> for API
/// tokens), never on disk, in the remote URL, in a log or to an LLM. git runs with AIDE's hardened <see cref="IGitCli"/>.
/// </summary>
public sealed partial class BitbucketRepoInitializer(IGitCli git, IOptionsMonitor<BitbucketOptions> options, ILogger<BitbucketRepoInitializer> logger) : IBitbucketRepoInitializer
{
    public const string DefaultBranch = "main";
    public const string CommitMessage = "Initial commit";

    public async Task PushReadmeAsync(string fullName, string name, string? description, GitCredentialKind credential, string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullName);
        ArgumentException.ThrowIfNullOrEmpty(token);
        var current = options.CurrentValue;
        var remote = GitHostRemotes.Create(
            new Uri(BitbucketRules.WebBaseUrl, UriKind.Absolute),
            fullName,
            credential,
            token,
            new GitTransportOverride(current.GitBaseUrlOverride, current.AllowInsecureGitBaseUrl));

        var folder = Path.Combine(Path.GetTempPath(), "aide-repo-init", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await RunAsync(folder, ["init", "-q", "-b", DefaultBranch], cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), Readme(name, description), cancellationToken);
            await RunAsync(folder, ["add", "README.md"], cancellationToken);
            await RunAsync(folder, ["commit", "-q", "--no-verify", "-m", CommitMessage], cancellationToken);
            await RunAsync(folder, ["remote", "add", GitRemote.Name, remote.Url], cancellationToken);
            var push = await git.RunAsync(
                folder,
                ["push", "--porcelain", "--no-verify", GitRemote.Name, $"refs/heads/{DefaultBranch}:refs/heads/{DefaultBranch}"],
                remote.ConfigEnvironment,
                cancellationToken);
            if (!push.Succeeded)
            {
                var error = remote.Redact(push.StandardError).Trim();
                LogPushFailed(logger, push.ExitCode);
                throw new BitbucketInitialPushException(error.Length == 0 ? "git push failed." : error)
                {
                    Unauthorized = error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("401", StringComparison.Ordinal)
                        || error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase),
                };
            }

            LogPushed(logger);
        }
        finally
        {
            DeleteTree(folder);
        }
    }

    /// <summary>The README: the repository name as a heading, then the description when there is one.</summary>
    public static string Readme(string name, string? description) =>
        string.IsNullOrWhiteSpace(description) ? $"# {name.Trim()}\n" : $"# {name.Trim()}\n\n{description.Trim()}\n";

    private async Task RunAsync(string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(folder, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            throw new BitbucketInitialPushException($"git {arguments[0]} failed: {result.StandardError.Trim()}");
        }
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket repository initialised: README pushed to main")]
    private static partial void LogPushed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushing the initial Bitbucket commit failed (git exit {ExitCode})")]
    private static partial void LogPushFailed(ILogger logger, int exitCode);
}
