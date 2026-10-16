using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Aide.Infrastructure.Git;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>A request the fake GitHub API received.</summary>
public sealed record GitHubRequest(string Method, string Path, string Query, string? Authorization, string Body);

/// <summary>
/// The GitHub REST API, answered in memory by (method, path) routes; unknown routes are 404. Every request is recorded. No
/// test reaches github.com: this replaces the primary handler of the <c>github</c> HttpClient.
/// </summary>
public sealed class FakeGitHubApi : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<GitHubRequest, (HttpStatusCode Status, string Body)>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<GitHubRequest> _requests = new();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _headers = new(StringComparer.Ordinal);

    public IReadOnlyList<GitHubRequest> Requests => [.. _requests];

    public FakeGitHubApi On(HttpMethod method, string path, Func<GitHubRequest, (HttpStatusCode Status, string Body)> respond)
    {
        ArgumentNullException.ThrowIfNull(method);
        _routes[$"{method.Method} {path}"] = respond;
        return this;
    }

    public FakeGitHubApi On(HttpMethod method, string path, HttpStatusCode status, string body) => On(method, path, _ => (status, body));

    /// <summary>As <see cref="On(HttpMethod, string, HttpStatusCode, string)"/>, with response headers (e.g. rate limits).</summary>
    public FakeGitHubApi On(HttpMethod method, string path, HttpStatusCode status, string body, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(method);
        _headers[$"{method.Method} {path}"] = headers;
        return On(method, path, _ => (status, body));
    }

    public int Count(HttpMethod method, string path) => _requests.Count(r => r.Method == method.Method && r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var received = new GitHubRequest(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            Uri.UnescapeDataString(request.RequestUri.Query),
            request.Headers.Authorization?.ToString(),
            body);
        _requests.Enqueue(received);
        var (status, text) = _routes.TryGetValue($"{received.Method} {received.Path}", out var route)
            ? route(received)
            : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };
        if (_headers.TryGetValue($"{received.Method} {received.Path}", out var headers))
        {
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}

/// <summary>Wraps the real git CLI and records every command with the per-command configuration it was given.</summary>
public sealed class RecordingGitCli(IGitCli inner) : IGitCli
{
    private readonly ConcurrentQueue<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Config)> _calls = new();

    public IReadOnlyList<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Config)> Calls => [.. _calls];

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => inner.GetVersionAsync(cancellationToken);

    public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _calls.Enqueue((arguments, null));
        return inner.RunAsync(workingDirectory, arguments, cancellationToken);
    }

    public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? configEnvironment, CancellationToken cancellationToken)
    {
        _calls.Enqueue((arguments, configEnvironment));
        return inner.RunAsync(workingDirectory, arguments, configEnvironment, cancellationToken);
    }
}

/// <summary>
/// A local bare repository standing in for GitHub's <c>acme/widgets</c> (FR-35 tests): <c>GitHub:GitBaseUrl</c> points at
/// its parent folder as a <c>file://</c> URL, allowed only through the tests-only <c>GitHub:AllowInsecureGitBaseUrl</c>.
/// </summary>
public sealed class BareRemote : IDisposable
{
    public const string Owner = "acme";
    public const string Repo = "widgets";
    public const string RepoUrl = "https://github.com/acme/widgets";

    private BareRemote(string root)
    {
        Root = root;
    }

    /// <summary>The folder standing in for the GitHub host.</summary>
    public string Root { get; }

    public string BarePath => Path.Combine(Root, Owner, Repo + ".git");

    /// <summary><c>GitHub:GitBaseUrl</c>: <c>file:///…/</c>.</summary>
    public string BaseUrl => new Uri(Root + Path.DirectorySeparatorChar).AbsoluteUri;

    /// <summary>A bare repository whose <c>main</c> has one commit adding README.md.</summary>
    public static async Task<BareRemote> CreateAsync(CancellationToken cancellationToken)
    {
        var remote = new BareRemote(Path.Combine(Path.GetTempPath(), "aide-tests-remotes", Guid.NewGuid().ToString("N")));
        var seed = Path.Combine(remote.Root, "seed");
        Directory.CreateDirectory(seed);
        await TestGit.RunAsync(seed, cancellationToken, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# Widgets\n", cancellationToken);
        await TestGit.RunAsync(seed, cancellationToken, "add", "README.md");
        await TestGit.RunAsync(seed, cancellationToken, "commit", "-q", "-m", "Initial import");
        Directory.CreateDirectory(Path.Combine(remote.Root, Owner));
        await TestGit.RunAsync(remote.Root, cancellationToken, "clone", "-q", "--bare", seed, remote.BarePath);
        return remote;
    }

    /// <summary>The app with this remote as the GitHub git host, <paramref name="api"/> as the REST API and git calls recorded.</summary>
    public WebApplicationFactory<Program> App(WebApplicationFactory<Program> factory, FakeGitHubApi api, out RecordingGitCli git)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var recorder = new RecordingGitCli[1];
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("GitHub:GitBaseUrl", BaseUrl);
            b.UseSetting("GitHub:AllowInsecureGitBaseUrl", "true");
            b.ConfigureTestServices(services =>
            {
                services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => api);
                var real = services.Last(d => d.ServiceType == typeof(IGitCli));
                services.Remove(real);
                services.AddSingleton<IGitCli>(sp =>
                {
                    var inner = real.ImplementationInstance as IGitCli
                        ?? (IGitCli)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!);
                    return recorder[0] = new RecordingGitCli(inner);
                });
            });
        });
        _ = app.Services.GetRequiredService<IGitCli>();
        git = recorder[0];
        return app;
    }

    public void Dispose() => TestGit.DeleteTree(Root);
}

/// <summary>Runs git for test set-up and inspection (system config ignored, fixed identity).</summary>
public static class TestGit
{
    public static async Task<string> RunAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var argument in (string[])["-c", "safe.directory=*", "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "core.autocrlf=false", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim().ReplaceLineEndings("\n");
    }

    public static void DeleteTree(string path)
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
}
