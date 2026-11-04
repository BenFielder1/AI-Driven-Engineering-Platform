using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>FR-93, FR-156: `tevrin version start`, `tevrin versions`, `tevrin deployments`.</summary>
internal static class VersionCommands
{
    /// <summary>FR-156, FR-129: what <c>tevrin version start --preset</c> prints before exiting 2 (usage).</summary>
    internal const string PresetRemoved = "presets were removed in v4; use --frontend, --backend, --data, --vcs and the host options to choose a stack (omit them to keep the current one).";

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
        // FR-156: kept only to explain its removal (hidden from help); any use exits 2.
        var preset = new Option<string?>("--preset")
        {
            Description = "Removed in v4 (presets were replaced by stack compositions).",
            HelpName = "id",
            Hidden = true,
        };
        var inferStack = new Option<bool>("--infer-stack")
        {
            Description = "Infer the new version's stack from the repository at the new base (FR-186): the version starts in analysis, and an Owner approves the Stack Analyst's profile. Not with the stack options.",
        };
        var command = new Command(
            "start",
            "Start a new release version of a released project (Owner only). The project goes back to refinement at checkpoint 1 " +
            "with the request as its idea (analysis with --infer-stack). Exit 4 for VERSION_INVALID, VERSION_NOT_GREATER, VERSION_IN_PROGRESS, TAG_EXISTS, " +
            "RELEASE_NOT_MERGED, NOT_RELEASED, STACK_INCOMPATIBLE or STACK_INFER_NO_CODE. Stack options change the named layers of the current composition; " +
            "without any, the new version keeps it.")
        {
            id, version, request, preset, inferStack,
        };

        // FR-156: the stack flags replace --preset, also under the names --stack-frontend and so on.
        var stackFlags = new StackFlags(stackAliases: true);
        stackFlags.AddTo(command);

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            if (context.Get(preset) is not null)
            {
                throw new UsageException(PresetRemoved);
            }

            var projectId = context.Get(id);
            var text = ReadRequest(context.Get(request) ?? string.Empty, context.Host.Terminal);
            var body = new JsonObject { ["version"] = context.Get(version)?.Trim(), ["request"] = text };
            var picks = stackFlags.Read(context);
            string? originWarning = null;
            if (context.Get(inferStack))
            {
                if (picks.Any)
                {
                    throw new UsageException("--infer-stack can't be combined with stack options: the Stack Analyst proposes the layers.");
                }

                body["stackSource"] = "infer";
            }
            else if (picks.Any)
            {
                var current = await context.Api().GetAsync<ProjectStackDto>($"api/projects/{projectId}/stack", cancellationToken).ConfigureAwait(false);
                var merged = StackFlags.Merge(current.Value.Composition, picks);
                body["stack"] = JsonSerializer.SerializeToNode(merged, ApiJson.Options);
                originWarning = StackFlags.OriginChange(current.Value.Composition?.OriginModel, merged.OriginModel);
            }

            var started = await context.Api().PostAsync<StartReleaseVersionResultDto>($"api/projects/{projectId}/versions", body, cancellationToken).ConfigureAwait(false);
            if (originWarning is not null)
            {
                await context.Host.Error.WriteLineAsync(originWarning).ConfigureAwait(false);
            }

            if (context.Json)
            {
                await context.WriteRawJsonAsync(started.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var v = started.Value.ReleaseVersion;
            var state = started.Value.State;
            await context.Out.WriteLineAsync(
                $"Started version {v.Version}{(v.BaseTag is { } baseTag ? $" (from {baseTag})" : string.Empty)}: phase {state.Phase}, checkpoint {Output.Num(state.Checkpoint)}.").ConfigureAwait(false);
            await context.Out.WriteLineAsync(state.Phase == "analysis" ? $"Next: tevrin analysis show {projectId}" : $"Next: tevrin status {projectId}").ConfigureAwait(false);
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
        var target = new Option<string?>("--target", "-t") { Description = "Only preview or production deployments (the v2 name of --env)." };
        target.AcceptOnlyFromAmong("preview", "production");
        var environment = new Option<string?>("--env") { Description = "Only preview or production deployments." };
        environment.AcceptOnlyFromAmong("preview", "production");
        var component = new Option<string?>("--component") { Description = "Only the frontend's or the backend's deployments." };
        component.AcceptOnlyFromAmong("frontend", "backend");
        var command = new Command("deployments", "List a project's deployments, newest first: target, component, version, SHA, state, URL, time and who triggered it. 'tevrin deployments groups <id>' lists its deployment groups.") { id, target, environment, component };
        command.Subcommands.Add(BuildGroups(factory));
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var path = AideApiClient.WithQuery($"api/projects/{context.Get(id)}/deployments", ("target", context.Get(target)), ("environment", context.Get(environment)), ("component", context.Get(component)));
            var result = await context.Api().GetAsync<List<DeploymentDto>>(path, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(result.Value.Count == 0 ? "No deployments." : Output.Table(
                ["ID", "TARGET", "COMPONENT", "VERSION", "SHA", "STATUS", "URL", "CREATED", "BY"],
                result.Value.Select(d => (IReadOnlyList<string>)
                [
                    d.Id.ToString(), d.Target, d.Component ?? "-", d.ReleaseVersion, ShortSha(d.GitSha), StatusText(d), d.Url ?? "-", Output.Time(d.CreatedAt), TriggeredBy(d),
                ]))).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>FR-194: `tevrin deployments groups &lt;id&gt;` — the deployment groups of Settings → Deployment (FR-191).</summary>
    private static Command BuildGroups(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command(
            "groups",
            "List a project's deployment groups (parts on one platform sharing a project): platform, parts, status, name, connection, shared project and each part's status.")
        {
            id,
        };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api().GetAsync<DeploymentGroupsDto>($"api/projects/{context.Get(id)}/settings/deployment", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.Out.WriteLineAsync(ApiJson.ToIndentedJson(JsonNode.Parse(result.Raw)?["groups"] ?? new JsonArray())).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(FormatGroups(result.Value.Groups ?? [])).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    internal static string FormatGroups(IReadOnlyList<DeploymentGroupDto> groups)
    {
        if (groups.Count == 0)
        {
            return "No deployment groups.";
        }

        var lines = new List<string>
        {
            Output.Table(
                ["PROVIDER", "PARTS", "STATUS", "NAME", "CONNECTION", "SHARED PROJECT"],
                groups.Select(g => (IReadOnlyList<string>)
                [
                    g.Provider,
                    string.Join(", ", g.Parts),
                    g.StatusReason is { } reason ? $"{g.Status} ({reason})" : g.Status,
                    g.Name ?? "-",
                    g.Binding is { } b ? $"{b.Label}{(b.BoundBy is { } by ? $" (connected by {by.Name})" : string.Empty)}" : "-",
                    g.SharedResourceUrl ?? g.SharedResourceId ?? (g.Capability == "shared_setup" ? "one project per part" : "-"),
                ])),
        };

        foreach (var group in groups)
        {
            foreach (var part in group.PartStatuses ?? [])
            {
                var where = part.Environment is { } environment ? $"{part.Part} ({environment})" : part.Part;
                lines.Add($"{group.Provider} {where}: {part.Status}{(part.StatusReason is { } why ? $" ({why})" : string.Empty)}");
            }

            if (group.CanRetry)
            {
                lines.Add($"{group.Provider}: a part failed or is missing; retry it in Settings → Deployment.");
            }
        }

        return string.Join(Environment.NewLine, lines);
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

    /// <summary>FR-129: the retired preset a migrated version was pinned to (history), or <c>none</c>.</summary>
    internal static string PresetText(string? preset, string? version) =>
        preset is null ? "none" : $"{preset}{(version is null ? string.Empty : "@" + version)} (retired, migrated)";

    internal static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    internal static string StatusText(DeploymentDto d) => d.ErrorCode is { } code ? $"{d.Status} ({code})" : d.Status;

    internal static string TriggeredBy(DeploymentDto d) => d.TriggeredBy?.Name ?? d.Trigger;
}
