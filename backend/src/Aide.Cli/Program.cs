using Aide.Cli;
using Aide.Cli.Hosting;

// `tevrin` CLI (SPEC FR-52, FR-200; the deprecated `aide` alias is src/Aide.Cli.Alias). All behaviour lives in CliApp so the tests can run it against fakes.
return await CliApp.RunAsync(args, CliHost.CreateDefault()).ConfigureAwait(false);
