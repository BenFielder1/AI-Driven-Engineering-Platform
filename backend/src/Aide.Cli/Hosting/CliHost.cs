namespace Aide.Cli.Hosting;

/// <summary>Everything the CLI touches outside its own code: streams, environment, network, editor, clock.</summary>
internal sealed class CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    public required ITerminal Terminal { get; init; }

    public required IEditorLauncher Editor { get; init; }

    /// <summary>Creates the handler behind the API HttpClient (a fake in tests).</summary>
    public required Func<HttpMessageHandler> CreateHttpHandler { get; init; }

    /// <summary>The per-user config root; the CLI uses `&lt;root&gt;/aide/config.json` unless AIDE_CONFIG is set.</summary>
    public required string UserConfigRoot { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public static CliHost CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        GetEnvironmentVariable = Environment.GetEnvironmentVariable,
        Terminal = new SystemTerminal(),
        Editor = new ProcessEditorLauncher(Environment.GetEnvironmentVariable),
        CreateHttpHandler = () => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) },
        UserConfigRoot = DefaultUserConfigRoot(),
    };

    /// <summary>%APPDATA% on Windows; $XDG_CONFIG_HOME or ~/.config elsewhere.</summary>
    internal static string DefaultUserConfigRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            return appData;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config");
    }
}
