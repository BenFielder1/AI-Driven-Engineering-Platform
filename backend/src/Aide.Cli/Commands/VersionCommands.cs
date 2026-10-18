using System.CommandLine;
using System.Text.Json.Nodes;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>FR-93: `aide version start`, `aide versions`, `aide deployments`.</summary>
internal static class VersionCommands
{
    /// <summary>The <c>--preset</c> value that starts the version without a preset.</summary>
    internal const string NoPreset = "none";

    public static Command BuildVersion(CommandFactory factory)
    {
        var command = new Command("version", "Release versions (FR-60): start the next version of a released project.");
        command.Subcommands.Add(BuildStart(factory));
        return command;
    }

    private static Command BuildStart(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var version = new Argument<string>("semver") { Description = "The new version, MAJOR.MINOR.PATCH[-prerelease] (a leading 'v' is stripped), above the released version." };
        var request = new Option<string>("--request", "-r")
        {
            Description = "What the version adds, improves or fixes: the text itself, or @path to read it from a file (@- for stdin).",
            Required = true,
        };
        var preset = new Option<string?>("--preset")
        {
            Description = $"Tech stack preset id of the new version (default: keep the current one; '{NoPreset}' for no preset).",
            HelpName = "id",
        };
        var command = new Command(
            "start",
            "Start a new release version of a released project (Owner only). The project goes back to refinement at checkpoint 1 " +
            "with the request as its idea. Exit 4 for VERSION_INVALID, VERSION_NOT_GREATER, VERSION_IN_PROGRESS, TAG_EXISTS, " +
            "RELEASE_NOT_MERGED or NOT_RELEASED.")
        {
            id, version, request, preset,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var text = ReadRequest(context.Get(request) ?? string.Empty, context.Host.Terminal);
            var body = new JsonObject { ["version"] = context.Get(version)?.Trim(), ["request"] = text };
            if (context.Get(preset) is { } presetId)
            {
                body["presetId"] = string.Equals(presetId.Trim(), NoPreset, StringComparison.OrdinalIgnoreCase) ? null : presetId.Trim();
            }

            var started = await context.Api().PostAsync<StartReleaseVersionResultDto>($"api/projects/{projectId}/versions", body, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(started.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var v = started.Value.ReleaseVersion;
            var state = started.Value.State;
            await context.Out.WriteLineAsync(
                $"Started version {v.Version}{(v.BaseTag is { } baseTag ? $" (from {baseTag})" : string.Empty)}: phase {state.Phase}, checkpoint {Output.Num(state.Checkpoint)}, preset {PresetText(v.PresetName ?? v.PresetId, v.PresetVersion)}.").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Next: aide status {projectId}").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildVersions(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("versions", "List a project's release versions, highest first: in development, awaiting merge, released and imported.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api().GetAsync<List<ReleaseVersionDto>>($"api/projects/{context.Get(id)}/versions", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(result.Value.Count == 0 ? "No versions." : Output.Table(
                ["VERSION", "STATUS", "TAG", "CHECKPOINT", "PRESET", "BRANCH", "STARTED", "RELEASED"],
                result.Value.Select(v => (IReadOnlyList<string>)
                [
                    v.Version,
                    v.Status,
                    v.Tag ?? "-",
                    $"{Output.Num(v.Checkpoints.Current)}/{Output.Num(v.Checkpoints.Max)}",
                    PresetText(v.PresetName ?? v.PresetId, v.PresetVersion),
                    v.BranchName ?? "-",
                    Output.Time(v.CreatedAt),
                    Output.Time(v.ReleasedAt),
                ]))).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildDeployments(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var target = new Option<string?>("--target", "-t") { Description = "Only preview or production deployments." };
        target.AcceptOnlyFromAmong("preview", "production");
        var command = new Command("deployments", "List a project's deployments, newest first: target, version, SHA, state, URL, time and who triggered it.") { id, target };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var path = AideApiClient.WithQuery($"api/projects/{context.Get(id)}/deployments", ("target", context.Get(target)));
            var result = await context.Api().GetAsync<List<DeploymentDto>>(path, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(result.Value.Count == 0 ? "No deployments." : Output.Table(
                ["ID", "TARGET", "VERSION", "SHA", "STATUS", "URL", "CREATED", "BY"],
                result.Value.Select(d => (IReadOnlyList<string>)
                [
                    d.Id.ToString(), d.Target, d.ReleaseVersion, ShortSha(d.GitSha), StatusText(d), d.Url ?? "-", Output.Time(d.CreatedAt), TriggeredBy(d),
                ]))).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>`@path` reads the file (`@-` stdin); anything else is the request text itself.</summary>
    internal static string ReadRequest(string value, Hosting.ITerminal terminal)
    {
        if (value.StartsWith('@') && string.IsNullOrWhiteSpace(value[1..]))
        {
            throw new UsageException("--request @ needs a file path (@path, or @- for stdin).");
        }

        var text = value.StartsWith('@') ? ProjectCommands.ReadText(value[1..].Trim(), terminal) : value;
        return string.IsNullOrWhiteSpace(text) ? throw new UsageException("--request is empty.") : text;
    }

    internal static string PresetText(string? preset, string? version) =>
        preset is null ? "none" : version is null ? preset : $"{preset} {version}";

    internal static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    internal static string StatusText(DeploymentDto d) => d.ErrorCode is { } code ? $"{d.Status} ({code})" : d.Status;

    internal static string TriggeredBy(DeploymentDto d) => d.TriggeredBy?.Name ?? d.Trigger;
}
