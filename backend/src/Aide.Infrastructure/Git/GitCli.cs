using System.Diagnostics;
using System.Text;

namespace Aide.Infrastructure.Git;

/// <summary>The outcome of one git command: exit code and captured output (UTF-8).</summary>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A git command failed; <see cref="Exception.Message"/> names the command (never a token) and git's error text.</summary>
public sealed class GitCommandException : Exception
{
    public GitCommandException()
    {
    }

    public GitCommandException(string message)
        : base(message)
    {
    }

    public GitCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The <c>git</c> command-line tool (Local provider, working copies).</summary>
public interface IGitCli
{
    /// <summary>The output of <c>git --version</c>, or <c>null</c> if git isn't installed or doesn't answer.</summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>git &lt;arguments&gt;</c> in <paramref name="workingDirectory"/> safely (<see cref="GitCli"/>). A non-zero
    /// exit is returned, not thrown; git missing or a timeout throws <see cref="GitCommandException"/>.
    /// </summary>
    Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs git as a child process with an argument list (never through a shell), and with configuration that makes a
/// repository's content unable to run code in the API process: no hooks (<c>core.hooksPath</c> points at the null
/// device), no fsmonitor, no system or global config, no credential helpers or prompts, no symlinks checked out, and an
/// allow-listed environment (<see cref="AllowedEnvironment"/>), so no API secret or inherited <c>GIT_*</c> variable reaches it. The commit identity is set per command (<see cref="AuthorName"/>).
/// </summary>
internal sealed class GitCli : IGitCli
{
    public const string AuthorName = "AIDE";
    public const string AuthorEmail = "aide@aide.local";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The only variables git inherits from the API process: what it needs to start and find its own helpers (PATH, the
    /// Windows system folders and temp), a home and locale. Everything else, every <c>GIT_*</c> variable included, is
    /// dropped; the <c>GIT_*</c> settings git needs are set explicitly.
    /// </summary>
    internal static readonly HashSet<string> AllowedEnvironment = new(
        ["PATH", "HOME", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "TMPDIR", "SystemRoot", "SystemDrive", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "PATHEXT"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Git for Windows maps <c>/dev/null</c> to <c>NUL</c> itself.</summary>
    private const string NullDevice = "/dev/null";

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunCoreAsync(null, ["--version"], VersionTimeout, cancellationToken);
            return result.Succeeded ? result.StandardOutput.Trim() : null;
        }
        catch (GitCommandException)
        {
            return null;
        }
    }

    public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        return RunCoreAsync(workingDirectory, arguments, CommandTimeout, cancellationToken);
    }

    private static async Task<GitCommandResult> RunCoreAsync(string? workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        // A minimal allow-listed environment: the API's own variables (connection strings, provider keys, tokens) never
        // reach git or anything a repository could make it run.
        var inherited = start.Environment
            .Where(e => e.Value is not null && AllowedEnvironment.Contains(e.Key))
            .ToList();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
        {
            start.Environment[key] = value;
        }

        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = NullDevice;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = string.Empty;
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        string[] config =
        [
            "core.hooksPath=" + NullDevice,
            "core.fsmonitor=false",
            "core.autocrlf=false",
            "core.symlinks=false",
            "core.quotepath=false",
            "commit.gpgsign=false",
            "tag.gpgsign=false",
            "credential.helper=",
            "protocol.ext.allow=never",
            "user.name=" + AuthorName,
            "user.email=" + AuthorEmail,
        ];
        foreach (var setting in config)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }

        if (workingDirectory is not null)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("safe.directory=" + workingDirectory.Replace('\\', '/'));
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var command = arguments.Count > 0 ? arguments[0] : "git";
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitCommandException("The git command-line tool isn't installed on the AIDE server.", ex);
        }

        if (process is null)
        {
            throw new GitCommandException("git could not be started.");
        }

        using (process)
        {
            process.StandardInput.Close();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                return new GitCommandResult(process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new GitCommandException($"git {command} timed out after {timeout.TotalSeconds:0} s.");
            }
        }
    }
}
