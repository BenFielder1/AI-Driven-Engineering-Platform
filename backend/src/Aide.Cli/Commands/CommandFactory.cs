using System.CommandLine;
using Aide.Cli.Api;
using Aide.Cli.Config;
using Aide.Cli.Hosting;

namespace Aide.Cli.Commands;

/// <summary>Wires command actions to a <see cref="CommandContext"/> and turns failures into exit codes.</summary>
internal sealed class CommandFactory(CliHost host, GlobalOptions globals)
{
    public CliHost Host { get; } = host;

    public GlobalOptions Globals { get; } = globals;

    public void SetHandler(Command command, Func<CommandContext, CancellationToken, Task<int>> handler)
    {
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            using var context = new CommandContext(Host, Globals, parseResult);
            try
            {
                return await handler(context, cancellationToken).ConfigureAwait(false);
            }
            catch (CliException ex)
            {
                await ErrorReporter.ReportAsync(Host.Error, ex).ConfigureAwait(false);
                return ex.ExitCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await Host.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
                return ExitCodes.Error;
            }
        });
    }
}

/// <summary>Per-invocation state: parsed values, settings, the API client and output helpers.</summary>
internal sealed class CommandContext(CliHost host, GlobalOptions globals, ParseResult parseResult) : IDisposable
{
    private ConfigStore? _configStore;
    private CliSettings? _settings;
    private AideApiClient? _client;

    public CliHost Host { get; } = host;

    public ParseResult ParseResult { get; } = parseResult;

    public TextWriter Out => Host.Out;

    public TextWriter Error => Host.Error;

    public bool Json => ParseResult.GetValue(globals.Json);

    public string? ApiUrlFlag => ParseResult.GetValue(globals.ApiUrl);

    public ConfigStore ConfigStore => _configStore ??= new ConfigStore(ConfigStore.ResolvePath(Host.GetEnvironmentVariable, Host.UserConfigRoot));

    public CliSettings Settings => _settings ??= CliSettings.Resolve(ApiUrlFlag, Host.GetEnvironmentVariable, ConfigStore.Load());

    public T? Get<T>(Argument<T> argument) => ParseResult.GetValue(argument);

    public T? Get<T>(Option<T> option) => ParseResult.GetValue(option);

    /// <summary>The API base URL, validated (a bad --api-url is a usage error; a bad stored/env value is an error).</summary>
    public Uri ApiBaseUri()
    {
        if (CliSettings.TryNormalizeApiUrl(Settings.ApiUrl, out var baseUri))
        {
            return baseUri;
        }

        var message = $"Invalid API URL '{Settings.ApiUrl}' (from {Settings.ApiUrlSource.ToString().ToLowerInvariant()}); expected http(s)://host[:port].";
        throw Settings.ApiUrlSource == SettingSource.Flag ? new UsageException(message) : new CliException(message);
    }

    /// <summary>
    /// An API client using AIDE_TOKEN or the saved token, or <paramref name="tokenOverride"/> (login). The saved token is used
    /// only for the API URL it was saved for (otherwise exit 3), and no token is ever sent over plain http to a
    /// non-loopback host (exit 2).
    /// </summary>
    public AideApiClient Api(string? tokenOverride = null)
    {
        if (_client is not null && tokenOverride is null)
        {
            return _client;
        }

        var baseUri = ApiBaseUri();
        if (!CliSettings.IsAllowedForToken(baseUri))
        {
            throw new UsageException(
                $"Refusing to send a token over plain http to {baseUri.IdnHost}; use https:// (http is allowed only for localhost, 127.0.0.1 and ::1).");
        }

        var token = tokenOverride ?? Settings.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            if (tokenOverride is null && Settings.SavedTokenWithheld)
            {
                throw new CliException(
                    $"The saved token belongs to {Settings.SavedTokenApiUrl}, not {baseUri}, so it was not sent. " +
                    $"Run 'aide login --api-url {baseUri}' to save a token for this server, or set AIDE_TOKEN.",
                    ExitCodes.Auth);
            }

            throw new CliException(
                "Not logged in. Run 'aide login --token <token>' (create a token on the Account page) or set AIDE_TOKEN.",
                ExitCodes.Auth);
        }

        _client?.Dispose();
        _client = new AideApiClient(Host.CreateHttpHandler(), baseUri, token);
        return _client;
    }

    public Task WriteRawJsonAsync(string raw) => Out.WriteLineAsync(ApiJson.Indent(raw));

    public void Dispose() => _client?.Dispose();
}
