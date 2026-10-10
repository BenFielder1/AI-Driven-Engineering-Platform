using System.Net;
using System.Text;
using Aide.Cli.Hosting;

namespace Aide.Cli.Tests;

/// <summary>A request the fake handler saw (body read eagerly, since the request is disposed afterwards).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>Fake HttpMessageHandler: answers from a callback, records every request, never touches the network.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, List<RecordedRequest> requests) : HttpMessageHandler
{
    public List<RecordedRequest> Requests => requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, string errorCode, string title, string? detail = null)
    {
        var detailJson = detail is null ? string.Empty : $",\"detail\":\"{detail}\"";
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                $"{{\"type\":\"about:blank\",\"title\":\"{title}\",\"status\":{(int)status}{detailJson},\"errorCode\":\"{errorCode}\",\"traceId\":\"trace-1\"}}",
                Encoding.UTF8,
                "application/problem+json"),
        };
    }
}

internal sealed class FakeTerminal : ITerminal
{
    public bool IsInteractive { get; set; }

    public Queue<string?> Answers { get; } = new();

    public string StandardInput { get; set; } = string.Empty;

    public List<string> Prompts { get; } = [];

    public string? Prompt(string prompt)
    {
        Prompts.Add(prompt);
        return Answers.Count > 0 ? Answers.Dequeue() : null;
    }

    public string? PromptSecret(string prompt) => Prompt(prompt);

    public string ReadAllInput() => StandardInput;
}

/// <summary>"Edits" the file with a function instead of launching a process.</summary>
internal sealed class FakeEditor(Func<string, string> edit) : IEditorLauncher
{
    public string? EditedPath { get; private set; }

    public string? OriginalContent { get; private set; }

    /// <summary>The file's Unix mode while the editor had it open (null on Windows).</summary>
    public UnixFileMode? ModeDuringEdit { get; private set; }

    public async Task EditAsync(string filePath, CancellationToken cancellationToken)
    {
        EditedPath = filePath;
        ModeDuringEdit = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(filePath);
        OriginalContent = await File.ReadAllTextAsync(filePath, cancellationToken);
        await File.WriteAllTextAsync(filePath, edit(OriginalContent), cancellationToken);
    }
}

/// <summary>"Edits" by failing, like an editor that exits non-zero.</summary>
internal sealed class FailingEditor : IEditorLauncher
{
    public string? EditedPath { get; private set; }

    public Task EditAsync(string filePath, CancellationToken cancellationToken)
    {
        EditedPath = filePath;
        throw new CliException("Editor 'fake' exited with code 1; nothing was saved.");
    }
}

/// <summary>A clock whose timers fire straight away (on the thread pool), so --follow polls without waiting.</summary>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record CliRun(int ExitCode, string Out, string Error);

/// <summary>Runs the CLI in-process with fakes and an isolated config folder.</summary>
internal sealed class CliTestHarness : IDisposable
{
    public const string Token = "aide_pat_0123456789abcdefghijklmnopqrstuv";

    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public CliTestHarness()
    {
        ConfigRoot = Path.Combine(Path.GetTempPath(), "aide-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigRoot);
    }

    public string ConfigRoot { get; }

    public string DefaultConfigPath => Path.Combine(ConfigRoot, "aide", "config.json");

    public Dictionary<string, string?> Environment { get; } = new();

    /// <summary>Every request any command sent, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    public FakeTerminal Terminal { get; } = new();

    public IEditorLauncher Editor { get; set; } = new FakeEditor(content => content);

    public CliTestHarness Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
        return this;
    }

    /// <summary>Saves a config file with this token (and optional API URL), bound to that URL as `aide login` would.</summary>
    public CliTestHarness LoggedIn(string token = Token, string? apiUrl = null) =>
        WriteConfig(token, apiUrl, apiUrl ?? "http://localhost:8080");

    /// <summary>Writes the config file verbatim (null → JSON null).</summary>
    public CliTestHarness WriteConfig(string? token, string? apiUrl, string? tokenApiUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultConfigPath)!);
        static string Json(string? value) => value is null ? "null" : $"\"{value}\"";
        File.WriteAllText(DefaultConfigPath, $"{{\"apiUrl\":{Json(apiUrl)},\"token\":{Json(token)},\"tokenApiUrl\":{Json(tokenApiUrl)}}}");
        return this;
    }

    /// <summary>The clock behind --follow's poll interval (default: timers fire at once).</summary>
    public TimeProvider TimeProvider { get; set; } = new ImmediateTimeProvider();

    public Task<CliRun> RunAsync(params string[] args) => RunAsync(TestContext.Current.CancellationToken, args);

    public async Task<CliRun> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            GetEnvironmentVariable = name => Environment.GetValueOrDefault(name),
            Terminal = Terminal,
            Editor = Editor,
            CreateHttpHandler = () => new FakeHttpHandler(request => _respond(request), Requests),
            UserConfigRoot = ConfigRoot,
            TimeProvider = TimeProvider,
        };
        var exitCode = await CliApp.RunAsync(args, host, cancellationToken);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(ConfigRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
