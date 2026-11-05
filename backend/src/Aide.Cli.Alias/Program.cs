using Aide.Cli;
using Aide.Cli.Hosting;

// FR-200: the deprecated `aide` alias of the `tevrin` CLI: prints the notice to stderr, then behaves identically.
return await CliApp.RunAliasAsync(args, CliHost.CreateDefault()).ConfigureAwait(false);
