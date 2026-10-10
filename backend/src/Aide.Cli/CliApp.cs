using System.CommandLine;
using System.CommandLine.Help;
using Aide.Cli.Commands;
using Aide.Cli.Hosting;

namespace Aide.Cli;

/// <summary>Builds the command tree and runs it: parse errors → 2, API/CLI failures → their exit codes.</summary>
internal static class CliApp
{
    private const string Description = """
        Command-line client for the AIDE Platform (SPEC FR-52).

        Authenticate once with a personal API token (create one on the Account page):
          aide login --token aide_pat_...

        Settings: config file at `aide config path` (override with AIDE_CONFIG); AIDE_API_URL and
        AIDE_TOKEN override the file; --api-url overrides both. Default API URL: http://localhost:8080.

        The saved token is sent only to the API URL it was saved for (by 'aide login'); for another URL,
        log in again or set AIDE_TOKEN (otherwise exit 3). Tokens are never sent over plain http except
        to localhost, 127.0.0.1 or ::1 (otherwise exit 2): use https:// for any other host.

        Output is human-readable; add --json for the API's JSON.
        Exit codes: 0 success, 1 error, 2 usage, 3 auth (401/403), 4 conflict (409).
        """;

    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        var root = Build(host);
        var parseResult = root.Parse(args);

        if (parseResult.Errors.Count > 0 && parseResult.Action is not HelpAction)
        {
            foreach (var error in parseResult.Errors)
            {
                await host.Error.WriteLineAsync($"error: {error.Message}").ConfigureAwait(false);
            }

            var commandPath = string.Join(' ', CommandPath(parseResult.CommandResult.Command));
            await host.Error.WriteLineAsync($"Run '{commandPath} --help' for usage.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        var invocation = new InvocationConfiguration { Output = host.Out, Error = host.Error, EnableDefaultExceptionHandler = false };
        return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    internal static RootCommand Build(CliHost host)
    {
        var globals = new GlobalOptions();
        var root = new RootCommand(Description);
        root.Options.Add(globals.Json);
        root.Options.Add(globals.ApiUrl);

        var factory = new CommandFactory(host, globals);
        root.Subcommands.Add(ConfigCommands.Build(factory));
        root.Subcommands.Add(AuthCommands.BuildLogin(factory));
        root.Subcommands.Add(AuthCommands.BuildLogout(factory));
        root.Subcommands.Add(ProjectCommands.BuildProjects(factory));
        root.Subcommands.Add(ProjectCommands.BuildProject(factory));
        root.Subcommands.Add(ProjectCommands.BuildStatus(factory));
        root.Subcommands.Add(ArtifactCommands.Build(factory));
        root.Subcommands.Add(LifecycleCommands.BuildExecute(factory));
        root.Subcommands.Add(LifecycleCommands.BuildReview(factory));
        root.Subcommands.Add(LifecycleCommands.BuildRelease(factory));
        root.Subcommands.Add(LifecycleCommands.BuildRollback(factory));
        root.Subcommands.Add(LogsCommand.Build(factory));
        return root;
    }

    private static Stack<string> CommandPath(Command command)
    {
        var names = new Stack<string>();
        Symbol? current = command;
        while (current is not null)
        {
            names.Push(current is RootCommand ? "aide" : current.Name);
            current = current.Parents.FirstOrDefault();
        }

        return names;
    }
}

/// <summary>Options accepted by every command.</summary>
internal sealed class GlobalOptions
{
    public Option<bool> Json { get; } = new("--json")
    {
        Description = "Print the API response as JSON instead of human-readable text.",
        Recursive = true,
    };

    public Option<string?> ApiUrl { get; } = new("--api-url")
    {
        Description = "AIDE API base URL (overrides AIDE_API_URL and the config file).",
        Recursive = true,
        HelpName = "url",
    };
}
