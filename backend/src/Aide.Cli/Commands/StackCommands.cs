using System.CommandLine;
using Aide.Cli.Api;
using Aide.Cli.Config;

namespace Aide.Cli.Commands;

/// <summary>FR-156: `aide stack catalog|show|set|detect`.</summary>
internal static class StackCommands
{
    private const string None = "none";

    public static Command Build(CommandFactory factory)
    {
        var command = new Command("stack", "Stack compositions: the catalog, a project's stack, changing it, and detecting a repository's.");
        command.Subcommands.Add(BuildCatalog(factory));
        command.Subcommands.Add(BuildShow(factory));
        command.Subcommands.Add(BuildSet(factory));
        command.Subcommands.Add(BuildDetect(factory));
        return command;
    }

    private static Command BuildCatalog(CommandFactory factory)
    {
        var command = new Command("catalog", "List the stack catalog's layers and the options of each choice.");
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var catalog = await context.Api().GetAsync<StackCatalogDto>("api/stack/catalog", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(catalog.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(FormatCatalog(catalog.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildShow(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("show", "Show the stack composition of a project's current version, and whether it is locked.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var stack = await context.Api().GetAsync<ProjectStackDto>($"api/projects/{context.Get(id)}/stack", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(stack.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(FormatStack(stack.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildSet(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command(
            "set",
            "Change the stack of the version in development (Owner only). Only the named layers change: the rest of the current composition is kept. " +
            "Use 'none' for no frontend, backend, data or host. Changing the frontend, backend or their hosts resets the origin model to its default. " +
            "The composition is validated; exit 4 prints any STACK_INCOMPATIBLE violations, or STACK_LOCKED.")
        {
            id,
        };
        var flags = new StackFlags(stackAliases: false);
        flags.AddTo(command);

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var picks = flags.Read(context);
            if (!picks.Any)
            {
                throw new UsageException("Name at least one layer to change: --frontend, --backend, --data, --vcs, --frontend-host, --backend-host, --database-host or --local.");
            }

            var path = $"api/projects/{context.Get(id)}/stack";

            // PUT replaces the composition (a left-out layer is None), so the named layers are merged into the current one.
            var current = await context.Api().GetAsync<ProjectStackDto>(path, cancellationToken).ConfigureAwait(false);
            var updated = await context.Api().SendAsync<ProjectStackDto>(HttpMethod.Put, path, StackFlags.Merge(current.Value.Composition, picks), cancellationToken).ConfigureAwait(false);
            if (StackFlags.OriginChange(current.Value.Composition?.OriginModel, updated.Value.Composition?.OriginModel) is { } warning)
            {
                await context.Host.Error.WriteLineAsync(warning).ConfigureAwait(false);
            }

            if (context.Json)
            {
                await context.WriteRawJsonAsync(updated.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(FormatStack(updated.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildDetect(CommandFactory factory)
    {
        var repo = new Argument<string>("repo-url") { Description = "The repository's URL. With --provider, also owner/name (GitLab: a project path or id; local: a directory under the server's import root)." };
        var provider = new Option<string?>("--provider") { Description = "Git provider. Default, from the URL: github.com is github_app, gitlab.com is gitlab, bitbucket.org is bitbucket. Required for any other host." };
        provider.AcceptOnlyFromAmong("github_app", "github_pat", "gitlab", "bitbucket", "local");
        var gitRef = new Option<string?>("--ref") { Description = "Branch, tag or commit (default: the default branch)." };
        var instance = new Option<Guid?>("--instance") { Description = "GitLab instance id, for self-managed GitLab." };
        var connection = new Option<Guid?>("--connection") { Description = "Your GitLab or Bitbucket connection to use (default: your newest valid one)." };
        var command = new Command(
            "detect",
            "Suggest a stack from a connected repository's files. Nothing is stored. Exit 4 for GIT_NOT_CONNECTED. " +
            "For github_pat, the access token is read from AIDE_GIT_TOKEN, never from a flag.")
        {
            repo, provider, gitRef, instance, connection,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var (providerId, repoId) = ResolveRepo(context.Get(repo) ?? string.Empty, context.Get(provider));
            var token = providerId == "github_pat" ? context.Host.GetEnvironmentVariable(EnvironmentVariables.GitToken) : null;
            var request = new DetectStackRequest(new DetectStackGitRequest(
                providerId,
                context.Get(instance),
                repoId,
                context.Get(gitRef),
                string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
                context.Get(connection)?.ToString()));

            var detected = await context.Api().PostAsync<StackDetectionDto>("api/stack/detect", request, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(detected.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(FormatDetection(detected.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>
    /// The API's provider and repository id (<c>owner/name</c>, or a GitLab path) for a repository argument: a URL gives its
    /// path, and its provider when the host is a known one. Anything else is passed as it is, and needs <c>--provider</c>.
    /// </summary>
    internal static (string Provider, string Repo) ResolveRepo(string value, string? provider)
    {
        var repo = value.Trim();
        if (repo.Length == 0)
        {
            throw new UsageException("The repository is empty.");
        }

        if (provider != "local"
            && Uri.TryCreate(repo, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
        {
            var path = Uri.UnescapeDataString(url.AbsolutePath).Trim('/');
            path = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
            if (!path.Contains('/', StringComparison.Ordinal))
            {
                throw new UsageException($"'{url.GetLeftPart(UriPartial.Path)}' is not a repository URL; expected https://host/owner/name.");
            }

            return (provider ?? ProviderOfHost(url.IdnHost), path);
        }

        return provider is null
            ? throw new UsageException($"'{repo}' is not an http(s) URL: pass the repository's URL, or name its host with --provider.")
            : (provider, repo);
    }

    private static string ProviderOfHost(string host) => host.ToUpperInvariant() switch
    {
        "GITHUB.COM" or "WWW.GITHUB.COM" => "github_app",
        "GITLAB.COM" or "WWW.GITLAB.COM" => "gitlab",
        "BITBUCKET.ORG" or "WWW.BITBUCKET.ORG" => "bitbucket",
        _ => throw new UsageException($"Can't tell the Git provider of {host}: pass --provider (for self-managed GitLab: --provider gitlab --instance <id>)."),
    };

    /// <summary>One line per composition, for `status`.</summary>
    internal static string Summary(StackCompositionDto? composition)
    {
        if (composition is null)
        {
            return "not chosen";
        }

        var hosts = composition.Deploy;
        return string.Join(
            ", ",
            $"frontend {composition.Frontend?.Pack ?? None}",
            $"backend {composition.Backend?.Pack ?? None}",
            $"data {DataText(composition.Data)}",
            $"vcs {composition.Vcs?.Pack ?? "-"}",
            $"frontend host {hosts?.FrontendHost ?? None}",
            $"backend host {hosts?.BackendHost ?? None}",
            $"database host {hosts?.DatabaseHost ?? None}",
            $"local {hosts?.Local ?? None}");
    }

    private static string DataText(StackDataDto? data) =>
        data is null ? None : data.Mode is { } mode ? $"{data.Pack} ({mode})" : data.Pack;

    private static string ComponentText(StackComponentDto? component) =>
        component is null ? None : $"{component.Pack} at {component.Path}";

    internal static string FormatStack(ProjectStackDto stack)
    {
        var c = stack.Composition;
        var lines = new List<string>
        {
            Output.KeyValues(
            [
                ("Version", $"{stack.Version ?? "-"} ({stack.VersionStatus ?? "-"})"),
                ("Locked", stack.Locked ? $"yes ({stack.LockedReason ?? "-"})" : "no"),
                ("Catalog", stack.CatalogVersion ?? "-"),
                ("Frontend", ComponentText(c?.Frontend)),
                ("Backend", ComponentText(c?.Backend)),
                ("Data", DataText(c?.Data)),
                ("VCS", c?.Vcs?.Pack ?? "-"),
                ("Frontend host", c?.Deploy?.FrontendHost ?? None),
                ("Backend host", c?.Deploy?.BackendHost ?? None),
                ("Database host", c?.Deploy?.DatabaseHost ?? None),
                ("Local", c?.Deploy?.Local ?? None),
                ("Origin model", c?.OriginModel ?? "-"),
            ]),
        };

        if (stack.LegacyPreset is { } preset)
        {
            lines.Add($"Preset: {preset.Label}");
        }

        if (c is null)
        {
            lines.Add("No stack composition is pinned on this version.");
        }

        lines.AddRange((c?.Caveats ?? []).Select(caveat => $"Caveat {caveat.Rule} ({caveat.Layer}: {caveat.Option}): {caveat.Text}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatCatalog(StackCatalogDto catalog)
    {
        var lines = new List<string> { $"Stack catalog {catalog.Version}" };
        foreach (var layer in catalog.Layers)
        {
            lines.Add(string.Empty);
            lines.Add($"{layer.Name} ({layer.Id})");
            foreach (var choice in catalog.Choices.Where(c => c.Layer == layer.Id))
            {
                lines.Add($"  {choice.Name} ({choice.Id}):");
                lines.Add(Output.Table(
                    ["    OPTION", "NAME", "MODES"],
                    choice.Options.Select(o => (IReadOnlyList<string>)["    " + o.Id, o.Name, ModesText(o)])));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ModesText(StackOptionDto option) =>
        option.Modes is { Count: > 0 } modes ? string.Join(", ", modes.Select(m => m.Id)) : option.Mode ?? "-";

    private static string FormatDetection(StackDetectionDto detection)
    {
        var lines = new List<string>
        {
            $"Detected at {detection.Ref} ({VersionCommands.ShortSha(detection.Commit)}).",
            detection.Suggestions.Count == 0 ? "No suggestions." : Output.Table(
                ["CHOICE", "OPTION", "PATH", "MODE", "EVIDENCE"],
                detection.Suggestions.Select(s => (IReadOnlyList<string>)[s.Choice, s.Option, s.Path ?? "-", s.Mode ?? "-", string.Join(", ", s.Evidence)])),
        };

        foreach (var conflict in detection.Conflicts)
        {
            lines.Add($"Conflict in {conflict.Choice} (choose one): {string.Join(", ", conflict.Candidates.Select(c => c.Path is null ? c.Option : $"{c.Option} at {c.Path}"))}");
        }

        if (detection.Truncated)
        {
            lines.Add("The repository has too many files to read them all, so this may be incomplete.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>FR-156: the layers named on a command line (null: not named); <see cref="StackFlags"/> reads them.</summary>
internal sealed record StackPicks(
    string? Frontend,
    string? Backend,
    string? Data,
    string? DataMode,
    string? Vcs,
    string? FrontendHost,
    string? BackendHost,
    string? DatabaseHost,
    string? Local)
{
    public bool Any => new[] { Frontend, Backend, Data, Vcs, FrontendHost, BackendHost, DatabaseHost, Local }.Any(p => p is not null);
}

/// <summary>
/// FR-156: the stack options of `aide stack set` and `aide version start`, and how they apply to a composition. The API
/// takes a whole composition (a left-out layer is None), so the options are merged into the current one.
/// </summary>
internal sealed class StackFlags
{
    private readonly Option<string?> _frontend;
    private readonly Option<string?> _backend;
    private readonly Option<string?> _data;
    private readonly Option<string?> _vcs;
    private readonly Option<string?> _frontendHost;
    private readonly Option<string?> _backendHost;
    private readonly Option<string?> _databaseHost;
    private readonly Option<string?> _local;

    /// <param name="stackAliases">Also accept each option as <c>--stack-&lt;name&gt;</c>.</param>
    public StackFlags(bool stackAliases)
    {
        Option<string?> Flag(string name, string description)
        {
            var option = new Option<string?>("--" + name) { Description = description, HelpName = "option" };
            if (stackAliases)
            {
                option.Aliases.Add("--stack-" + name);
            }

            return option;
        }

        _frontend = Flag("frontend", "Frontend option, e.g. fe-nextjs, or none (see 'aide stack catalog').");
        _backend = Flag("backend", "Backend option, e.g. be-aspnet, or none.");
        _data = Flag("data", "Data option, e.g. data-prisma, or none; optionally with a mode: <option>:baas or <option>:host.");
        _vcs = Flag("vcs", "VCS option, e.g. vcs-github: must be the repository's host.");
        _frontendHost = Flag("frontend-host", "Frontend host option, e.g. deploy-vercel, or none.");
        _backendHost = Flag("backend-host", "Backend host option, e.g. deploy-render, or none.");
        _databaseHost = Flag("database-host", "Database host option, e.g. supabase, or none.");
        _local = Flag("local", "The local dev loop: compose or none.");
        _local.AcceptOnlyFromAmong("compose", "none");
    }

    public void AddTo(Command command)
    {
        foreach (var option in new[] { _frontend, _backend, _data, _vcs, _frontendHost, _backendHost, _databaseHost, _local })
        {
            command.Options.Add(option);
        }
    }

    public StackPicks Read(CommandContext context)
    {
        string? Value(Option<string?> option)
        {
            var value = context.Get(option);
            return value is not null && string.IsNullOrWhiteSpace(value) ? throw new UsageException($"{option.Name} is empty.") : value?.Trim();
        }

        var (data, mode) = ParseData(Value(_data));
        return new StackPicks(Value(_frontend), Value(_backend), data, mode, Value(_vcs), Value(_frontendHost), Value(_backendHost), Value(_databaseHost), Value(_local));
    }

    /// <summary><c>option[:baas|host]</c>.</summary>
    internal static (string? Pack, string? Mode) ParseData(string? value)
    {
        if (value is null)
        {
            return (null, null);
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (value, null);
        }

        var pack = value[..separator];
        var mode = value[(separator + 1)..];
        return pack.Length > 0 && mode is "baas" or "host"
            ? (pack, mode)
            : throw new UsageException($"--data must be <option>, <option>:baas or <option>:host (got '{value}').");
    }

    /// <summary>
    /// <paramref name="current"/> with the named layers replaced. An unchanged layer keeps its path, mode or Git host instance;
    /// a changed one takes its defaults. The origin model is left to its default when the frontend, backend or their hosts
    /// change, since it may not fit them (R10).
    /// </summary>
    internal static StackCompositionRequest Merge(StackCompositionDto? current, StackPicks picks)
    {
        var frontend = Component(current?.Frontend, picks.Frontend);
        var backend = Component(current?.Backend, picks.Backend);
        var deploy = current?.Deploy;
        var frontendHost = picks.FrontendHost ?? deploy?.FrontendHost;
        var backendHost = picks.BackendHost ?? deploy?.BackendHost;
        var databaseHost = picks.DatabaseHost ?? deploy?.DatabaseHost;
        var local = picks.Local ?? deploy?.Local;

        var keepOrigin = Same(frontend?.Pack, current?.Frontend?.Pack)
            && Same(backend?.Pack, current?.Backend?.Pack)
            && Same(frontendHost, deploy?.FrontendHost)
            && Same(backendHost, deploy?.BackendHost);

        return new StackCompositionRequest(
            frontend,
            backend,
            Data(current?.Data, picks),
            Vcs(current?.Vcs, picks.Vcs),
            (frontendHost ?? backendHost ?? databaseHost ?? local) is null ? null : new StackDeploySelection(frontendHost, backendHost, databaseHost, local),
            keepOrigin ? current?.OriginModel : null);
    }

    /// <summary>The warning when the stack's origin model changed (printed on stderr, so <c>--json</c> output stays clean); <c>null</c> otherwise.</summary>
    internal static string? OriginChange(string? before, string? after) =>
        before is null || before == after
            ? null
            : after is null
                ? $"warning: the origin model '{before}' was reset to the default for this stack."
                : $"warning: the origin model changed from '{before}' to '{after}'.";

    private static StackComponentSelection? Component(StackComponentDto? current, string? pick)
    {
        if (pick is null || pick == current?.Pack)
        {
            return current is null ? null : new StackComponentSelection(current.Pack, current.Path);
        }

        return new StackComponentSelection(pick, null);
    }

    private static StackDataSelection? Data(StackDataDto? current, StackPicks picks)
    {
        if (picks.Data is null || picks.Data == current?.Pack)
        {
            return current is null ? null : new StackDataSelection(current.Pack, picks.DataMode ?? current.Mode);
        }

        return new StackDataSelection(picks.Data, picks.DataMode);
    }

    private static StackVcsSelection? Vcs(StackVcsDto? current, string? pick)
    {
        if (pick is null || pick == current?.Pack)
        {
            return current is null ? null : new StackVcsSelection(current.Pack, current.InstanceId, current.SelfManaged ? true : null);
        }

        return new StackVcsSelection(pick, null, null);
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a == "none" ? null : a, b == "none" ? null : b, StringComparison.Ordinal);
}
