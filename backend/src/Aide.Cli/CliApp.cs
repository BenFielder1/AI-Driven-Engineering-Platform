using System.CommandLine;
using System.CommandLine.Help;
using Aide.Cli.Commands;
using Aide.Cli.Hosting;

namespace Aide.Cli;

/// <summary>Builds the command tree and runs it: parse errors → 2, API/CLI failures → their exit codes.</summary>
internal static class CliApp
{
    private const string Description = """
        Command-line client for Tevrin (SPEC FR-52, FR-93, FR-156, FR-194, FR-200).

        Authenticate once with a personal API token (create one on the Account page):
          tevrin login --token aide_pat_...

        Settings: config file at `tevrin config path` (override with TEVRIN_CONFIG); TEVRIN_API_URL and
        TEVRIN_TOKEN override the file; --api-url overrides both. Default API URL: http://localhost:8080.
        The older AIDE_CONFIG, AIDE_API_URL and AIDE_TOKEN still work when the TEVRIN_ one is unset.

        The saved token is sent only to the API URL it was saved for (by 'tevrin login'); for another URL,
        log in again or set TEVRIN_TOKEN (otherwise exit 3). Tokens are never sent over plain http except
        to localhost, 127.0.0.1 or ::1 (otherwise exit 2): use https:// for any other host.

        Output is human-readable; add --json for the API's JSON.
        Exit codes: 0 success, 1 error, 2 usage, 3 auth (401/403), 4 conflict (409).
        The release-version errors VERSION_INVALID, VERSION_NOT_GREATER, VERSION_IN_PROGRESS, TAG_EXISTS,
        RELEASE_NOT_MERGED and NOT_RELEASED also exit 4, as do STACK_INCOMPATIBLE, STACK_LOCKED,
        LLM_PROVIDER_UNAVAILABLE, STACK_PROFILE_INCOMPLETE, STACK_INFER_NO_CODE and SOURCE_ITEMS_TOO_LARGE.
        """;

    /// <summary>FR-200: the tool's command.</summary>
    public const string CommandName = "tevrin";

    /// <summary>FR-200: the deprecated alias (the v1–v5 command), kept for v6.</summary>
    public const string AliasCommandName = "aide";

    /// <summary>FR-200: what the <c>aide</c> alias prints to stderr before behaving exactly like <c>tevrin</c>.</summary>
    public const string AliasDeprecationNotice = "aide is now tevrin; this alias will be removed in a later version.";

    /// <summary>FR-200: the deprecated <c>aide</c> command: the notice on stderr, then the same run as <c>tevrin</c>.</summary>
    public static async Task<int> RunAliasAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        await host.Error.WriteLineAsync(AliasDeprecationNotice).ConfigureAwait(false);
        return await RunAsync(args, host, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        var root = Build(host);
        // FR-93: `--request @file` reads the file itself, so System.CommandLine's @response-file expansion is off.
        var parseResult = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });

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
        root.Subcommands.Add(VersionCommands.BuildVersion(factory));
        root.Subcommands.Add(VersionCommands.BuildVersions(factory));
        root.Subcommands.Add(VersionCommands.BuildDeployments(factory));
        root.Subcommands.Add(StackCommands.Build(factory));
        root.Subcommands.Add(AnalysisCommands.Build(factory));
        root.Subcommands.Add(SourcesCommands.Build(factory));
        root.Subcommands.Add(DatabaseCommands.BuildDatabases(factory));
        root.Subcommands.Add(DatabaseCommands.BuildMigrations(factory));
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
            names.Push(current is RootCommand ? CommandName : current.Name);
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
        Description = "Tevrin API base URL (overrides TEVRIN_API_URL, AIDE_API_URL and the config file).",
        Recursive = true,
        HelpName = "url",
    };
}
