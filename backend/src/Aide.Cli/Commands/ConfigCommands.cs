using System.CommandLine;
using System.Text.Json.Nodes;
using Aide.Cli.Api;
using Aide.Cli.Config;

namespace Aide.Cli.Commands;

/// <summary>`aide config set|get|path` — the token is never printed in full.</summary>
internal static class ConfigCommands
{
    private const string ApiUrlKey = "api-url";
    private const string TokenKey = "token";
    private static readonly string[] Keys = [ApiUrlKey, TokenKey];

    public static Command Build(CommandFactory factory)
    {
        var command = new Command("config", "Show or change CLI settings (API URL, token).");
        command.Subcommands.Add(BuildSet(factory));
        command.Subcommands.Add(BuildGet(factory));
        command.Subcommands.Add(BuildPath(factory));
        return command;
    }

    private static Command BuildSet(CommandFactory factory)
    {
        var key = new Argument<string>("key") { Description = "api-url or token" };
        key.AcceptOnlyFromAmong(Keys);
        var value = new Argument<string?>("value")
        {
            Description = "The new api-url (an empty string clears it). For token, omit it: you are prompted (hidden) or it is read from stdin.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command(
            "set",
            "Save a setting to the config file. Prefer 'aide login' for the token (it validates it); a token is never accepted as an argument.")
        {
            key, value,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var name = context.Get(key)!;
            var given = context.Get(value);
            var config = context.ConfigStore.Load();
            if (name == ApiUrlKey)
            {
                if (given is null)
                {
                    throw new UsageException("Missing value: aide config set api-url <url> (an empty string clears it).");
                }

                await SetApiUrlAsync(context, config, given.Trim()).ConfigureAwait(false);
            }
            else
            {
                SetToken(context, config, given);
            }

            context.ConfigStore.Save(config);
            var shown = name == TokenKey
                ? config.Token is null ? TokenMask.Mask(null) : $"{TokenMask.Mask(config.Token)} (for {config.TokenApiUrl})"
                : config.ApiUrl ?? "(default)";
            await context.Out.WriteLineAsync($"Saved {name} = {shown} to {context.ConfigStore.FilePath}").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static async Task SetApiUrlAsync(CommandContext context, CliConfig config, string newValue)
    {
        if (!string.IsNullOrEmpty(newValue) && !CliSettings.TryNormalizeApiUrl(newValue, out _))
        {
            throw new UsageException($"Invalid API URL '{newValue}'; expected http(s)://host[:port].");
        }

        if (config.Token is not null)
        {
            // Pin a legacy (unbound) token to the URL it was used with, so changing the URL never redirects it.
            config.TokenApiUrl ??= CliSettings.TryNormalizeApiUrl(config.ApiUrl ?? CliSettings.DefaultApiUrl, out var previous)
                ? CliSettings.Canonical(previous)
                : null;
        }

        config.ApiUrl = string.IsNullOrEmpty(newValue) ? null : newValue;
        var effective = config.ApiUrl ?? CliSettings.DefaultApiUrl;
        if (config.Token is not null && (config.TokenApiUrl is null || !CliSettings.SameApi(config.TokenApiUrl, effective)))
        {
            await context.Error.WriteLineAsync(
                $"note: the saved token belongs to {config.TokenApiUrl ?? "(unknown)"} and will not be sent to {effective}; run 'aide login' for it.").ConfigureAwait(false);
        }
    }

    /// <summary>A token on the command line lands in shell history, so it is only read from a hidden prompt or stdin.</summary>
    private static void SetToken(CommandContext context, CliConfig config, string? given)
    {
        string? token;
        if (given is not null)
        {
            if (!string.IsNullOrWhiteSpace(given))
            {
                throw new UsageException(
                    "Refusing a token as a command-line argument (it would be kept in shell history). " +
                    "Use 'aide login' (validates it), or run 'aide config set token' with no value to type it at a hidden prompt or pipe it on stdin.");
            }

            token = null;
        }
        else
        {
            token = (context.Host.Terminal.IsInteractive
                ? context.Host.Terminal.PromptSecret("API token: ")
                : context.Host.Terminal.ReadAllInput())?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                throw new UsageException("No token given. Type it at the prompt or pipe it on stdin ('aide logout' removes the saved token).");
            }
        }

        config.Token = token;
        config.TokenApiUrl = token is null ? null : CliSettings.Canonical(context.ApiBaseUri());
    }

    private static Command BuildGet(CommandFactory factory)
    {
        var key = new Argument<string?>("key") { Description = "api-url or token (omit to show all)", Arity = ArgumentArity.ZeroOrOne };
        key.AcceptOnlyFromAmong(Keys);
        var command = new Command("get", "Show effective settings and where each comes from (the token is masked).") { key };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var settings = context.Settings;
            var name = context.Get(key);
            var apiUrl = (Value: settings.ApiUrl, Source: Describe(settings.ApiUrlSource));
            var token = settings.SavedTokenWithheld
                ? (Value: TokenMask.Mask(context.ConfigStore.Load().Token), Source: $"config file, saved for {settings.SavedTokenApiUrl}; not sent to {settings.ApiUrl}")
                : (Value: TokenMask.Mask(settings.Token), Source: Describe(settings.TokenSource));

            if (context.Json)
            {
                var json = new JsonObject { ["path"] = context.ConfigStore.FilePath };
                if (name is null or ApiUrlKey)
                {
                    json["apiUrl"] = apiUrl.Value;
                    json["apiUrlSource"] = apiUrl.Source;
                }

                if (name is null or TokenKey)
                {
                    json["token"] = token.Value;
                    json["tokenSource"] = token.Source;
                }

                await context.Out.WriteLineAsync(ApiJson.ToIndentedJson(json)).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            switch (name)
            {
                case ApiUrlKey:
                    await context.Out.WriteLineAsync(apiUrl.Value).ConfigureAwait(false);
                    break;
                case TokenKey:
                    await context.Out.WriteLineAsync(token.Value).ConfigureAwait(false);
                    break;
                default:
                    await context.Out.WriteLineAsync(Output.KeyValues(
                    [
                        (ApiUrlKey, $"{apiUrl.Value} ({apiUrl.Source})"),
                        (TokenKey, $"{token.Value} ({token.Source})"),
                        ("config file", context.ConfigStore.FilePath),
                    ])).ConfigureAwait(false);
                    break;
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildPath(CommandFactory factory)
    {
        var command = new Command("path", "Print the config file path (set AIDE_CONFIG to use another file).");
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var path = context.ConfigStore.FilePath;
            await context.Out.WriteLineAsync(context.Json ? ApiJson.ToIndentedJson(new JsonObject { ["path"] = path }) : path).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static string Describe(SettingSource source) => source switch
    {
        SettingSource.Flag => "flag",
        SettingSource.Environment => "environment",
        SettingSource.File => "config file",
        _ => "default",
    };
}
