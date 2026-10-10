using Aide.Cli;
using Aide.Cli.Hosting;

// `aide` CLI (SPEC FR-52). All behaviour lives in CliApp so the tests can run it against fakes.
return await CliApp.RunAsync(args, CliHost.CreateDefault()).ConfigureAwait(false);
