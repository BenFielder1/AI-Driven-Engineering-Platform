using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>FR-156: `aide databases`, `aide migrations`.</summary>
internal static class DatabaseCommands
{
    public static Command BuildDatabases(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("databases", "List a project's managed databases per environment: provider, status, name, region, plan, expiry and warnings.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api().GetAsync<DeploymentDatabasesDto>($"api/projects/{context.Get(id)}/settings/deployment", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.Out.WriteLineAsync(ApiJson.ToIndentedJson(JsonNode.Parse(result.Raw)?["databases"])).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var databases = result.Value.Databases;
            if (databases.Count == 0)
            {
                await context.Out.WriteLineAsync("No databases.").ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(Output.Table(
                ["ENV", "PROVIDER", "STATUS", "NAME", "REGION", "PLAN", "EXPIRES", "URL"],
                databases.Select(d => (IReadOnlyList<string>)
                [
                    d.Environment, d.Provider, d.Paused ? $"{d.Status} (paused)" : d.Status, d.Name ?? "-", d.Region ?? "-", d.Plan ?? "-", Output.Time(d.ExpiresAt), d.ResourceUrl ?? "-",
                ]))).ConfigureAwait(false);
            foreach (var database in databases)
            {
                foreach (var warning in database.Warnings ?? [])
                {
                    await context.Out.WriteLineAsync($"{database.Environment}: {warning}").ConfigureAwait(false);
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildMigrations(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("migrations", "List a project's migration runs, newest first: environment, provider, SHA, status, files, duration and time.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api().GetAsync<List<MigrationRunDto>>($"api/projects/{context.Get(id)}/migrations", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(result.Value.Count == 0 ? "No migration runs." : Output.Table(
                ["ID", "ENV", "PROVIDER", "SHA", "STATUS", "FILES", "DURATION", "STARTED"],
                result.Value.Select(r => (IReadOnlyList<string>)
                [
                    r.Id.ToString(), r.Environment, r.Provider ?? "-", VersionCommands.ShortSha(r.GitSha), StatusText(r), Output.Num(r.Files?.Count ?? 0), Milliseconds(r.TotalMs), Output.Time(r.StartedAt),
                ]))).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>The `status` line for a migration run.</summary>
    internal static string Summary(MigrationRunDto run) =>
        $"{run.Environment} {StatusText(run)} {VersionCommands.ShortSha(run.GitSha)}, {Output.Num(run.Files?.Count ?? 0)} files, {Milliseconds(run.TotalMs)} ({Output.Time(run.StartedAt)})";

    private static string StatusText(MigrationRunDto run) => run.ErrorCode is { } code ? $"{run.Status} ({code})" : run.Status;

    private static string Milliseconds(long value) => string.Create(CultureInfo.InvariantCulture, $"{value} ms");
}
