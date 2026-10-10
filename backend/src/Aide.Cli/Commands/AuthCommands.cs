using System.CommandLine;
using Aide.Cli.Api;
using Aide.Cli.Config;

namespace Aide.Cli.Commands;

/// <summary>`aide login` validates a personal API token with GET /api/auth/me before saving it; `aide logout` forgets it.</summary>
internal static class AuthCommands
{
    public static Command BuildLogin(CommandFactory factory)
    {
        var token = new Option<string?>("--token")
        {
            Description = "Personal API token (aide_pat_...). Omit to be prompted, or pipe it on stdin to keep it out of shell history.",
            HelpName = "token",
        };
        var command = new Command("login", "Validate a personal API token and save it (and --api-url, if given) to the config file. The saved token is only sent to the API URL it was saved for.") { token };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var value = context.Get(token);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = context.Host.Terminal.IsInteractive
                    ? context.Host.Terminal.PromptSecret("API token: ")
                    : context.Host.Terminal.ReadAllInput();
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new UsageException("No token given. Use --token <token>, or pipe the token on stdin.");
            }

            var me = await context.Api(value).GetAsync<UserDto>("api/auth/me", cancellationToken).ConfigureAwait(false);

            var store = context.ConfigStore;
            var config = store.Load();
            config.Token = value;
            config.TokenApiUrl = CliSettings.Canonical(context.ApiBaseUri());
            if (!string.IsNullOrWhiteSpace(context.ApiUrlFlag))
            {
                config.ApiUrl = context.ApiUrlFlag.Trim();
            }

            store.Save(config);

            if (context.Settings.TokenSource == SettingSource.Environment)
            {
                await context.Error.WriteLineAsync("warning: AIDE_TOKEN is set and overrides the saved token.").ConfigureAwait(false);
            }

            if (context.Json)
            {
                await context.WriteRawJsonAsync(me.Raw).ConfigureAwait(false);
            }
            else
            {
                await context.Out.WriteLineAsync(
                    $"Logged in to {context.ApiBaseUri()} as {me.Value.Name} <{me.Value.Email}> ({me.Value.SystemRole}). Token saved to {store.FilePath} (sent only to this API URL).").ConfigureAwait(false);
            }

            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildLogout(CommandFactory factory)
    {
        var command = new Command("logout", "Remove the saved token from the config file (the token stays valid until revoked on the Account page).");
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var config = context.ConfigStore.Load();
            config.Token = null;
            config.TokenApiUrl = null;
            context.ConfigStore.Save(config);
            await context.Out.WriteLineAsync("Saved token removed. Revoke it on the Account page if it may have leaked.").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }
}
