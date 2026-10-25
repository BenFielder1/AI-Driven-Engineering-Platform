using System.CommandLine;
using System.Globalization;
using Aide.Cli.Api;
using Aide.Cli.Config;

namespace Aide.Cli.Commands;

/// <summary>`aide projects list`, `aide project init` (alias `aide project create`, FR-156), `aide status &lt;id&gt;`.</summary>
internal static class ProjectCommands
{
    private static readonly string[] Phases = ["refinement", "specification", "planning", "execution", "review", "release"];
    private static readonly string[] Statuses = ["active", "building", "reviewing", "releasing", "released", "rolled_back"];

    public static Argument<Guid> ProjectIdArgument() => new("id") { Description = "Project id (a GUID, shown by 'aide projects list')." };

    public static Command BuildProjects(CommandFactory factory)
    {
        var page = new Option<int>("--page", "-p") { Description = "Page number (from 1).", DefaultValueFactory = _ => 1, CustomParser = Parsers.IntInRange("--page", 1, int.MaxValue) };
        var limit = new Option<int>("--limit", "-l") { Description = "Results per page (1-100).", DefaultValueFactory = _ => 20, CustomParser = Parsers.IntInRange("--limit", 1, 100) };
        var search = new Option<string?>("--search", "-s") { Description = "Filter by name." };
        var phase = new Option<string?>("--phase") { Description = "Filter by phase." };
        phase.AcceptOnlyFromAmong(Phases);
        var status = new Option<string?>("--status") { Description = "Filter by status." };
        status.AcceptOnlyFromAmong(Statuses);
        var sortBy = new Option<string>("--sort-by") { Description = "Sort field.", DefaultValueFactory = _ => "updated" };
        sortBy.AcceptOnlyFromAmong("created", "updated", "name");
        var sortOrder = new Option<string>("--sort-order") { Description = "Sort direction.", DefaultValueFactory = _ => "desc" };
        sortOrder.AcceptOnlyFromAmong("asc", "desc");

        var list = new Command("list", "List the projects you are a member of.") { page, limit, search, phase, status, sortBy, sortOrder };
        factory.SetHandler(list, async (context, cancellationToken) =>
        {
            var path = AideApiClient.WithQuery(
                "api/projects",
                ("page", context.Get(page)),
                ("limit", context.Get(limit)),
                ("search", context.Get(search)),
                ("phase", context.Get(phase)),
                ("status", context.Get(status)),
                ("sortBy", context.Get(sortBy)),
                ("sortOrder", context.Get(sortOrder)));
            var result = await context.Api().GetAsync<PagedResult<ProjectSummaryDto>>(path, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var paged = result.Value;
            if (paged.Items.Count == 0)
            {
                await context.Out.WriteLineAsync(paged.Total == 0 ? "No projects." : "No projects on this page.").ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(Output.Table(
                ["ID", "NAME", "PHASE", "STATUS", "RELEASED", "IN DEVELOPMENT", "ROLE", "UPDATED"],
                paged.Items.Select(p => (IReadOnlyList<string>)
                [
                    p.Id.ToString(), p.Name, p.CurrentPhase, p.Status, p.ReleasedVersion ?? "-", p.DevelopmentVersion ?? "-", p.MyRole ?? "admin", Output.Time(p.UpdatedAt),
                ]))).ConfigureAwait(false);
            var pages = Math.Max(1, (int)Math.Ceiling(paged.Total / (double)Math.Max(1, paged.Limit)));
            await context.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Page {paged.Page} of {pages} ({paged.Total} projects)")).ConfigureAwait(false);
            return ExitCodes.Success;
        });

        var command = new Command("projects", "Project listing.");
        command.Subcommands.Add(list);
        return command;
    }

    public static Command BuildProject(CommandFactory factory)
    {
        var command = new Command("project", "Single-project operations.");
        command.Subcommands.Add(BuildInit(factory));
        return command;
    }

    private static Command BuildInit(CommandFactory factory)
    {
        var name = new Option<string?>("--name", "-n") { Description = "Project name (1-100 characters)." };
        var idea = new Option<string?>("--idea") { Description = "The idea to refine (1-10000 characters)." };
        var ideaFile = new Option<string?>("--idea-file") { Description = "Read the idea from a file ('-' for stdin)." };
        var approvalMode = new Option<string?>("--approval-mode") { Description = "advisory (default), strict or autonomous." };
        approvalMode.AcceptOnlyFromAmong("strict", "advisory", "autonomous");
        var compliance = new Option<double?>("--compliance-threshold") { Description = "Minimum compliance score to release (0.5-1)." };
        var maxFiles = new Option<int?>("--max-files-per-step") { Description = "Files an execution step may change (1-100)." };
        var maxCycles = new Option<int?>("--max-review-cycles") { Description = "Review cycles before escalation (1-10)." };
        var tokenBudget = new Option<long?>("--token-budget") { Description = "LLM token budget (at least 1000; omit for unlimited)." };
        var gitProvider = new Option<string?>("--git-provider") { Description = "local (default) or github." };
        gitProvider.AcceptOnlyFromAmong("local", "github");
        var repoUrl = new Option<string?>("--repo-url") { Description = "GitHub repository URL (https://github.com/OWNER/REPO); required for github." };
        var defaultBranch = new Option<string?>("--default-branch") { Description = "Default branch (server default: main)." };
        var noInput = new Option<bool>("--no-input") { Description = "Never prompt; fail with a usage error if something required is missing." };
        var llmProvider = new Option<string?>("--llm-provider") { Description = "LLM provider: anthropic, openai, ollama or fake (only those enabled on the server). Omit for the server's default." };
        llmProvider.AcceptOnlyFromAmong("anthropic", "openai", "ollama", "fake");
        var llmModel = new Option<string?>("--llm-model") { Description = "Default model id for --llm-provider (e.g. claude-opus-5-5); required with it." };
        var llmConnection = new Option<Guid?>("--llm-connection") { Description = "Id of your LLM connection to bind (Account > Connections). Default: your newest one for the provider, when it needs a key." };

        var command = new Command(
            "init",
            "Create a project, like the web wizard. Missing values are prompted for when stdin is a terminal. " +
            "For GitHub, the access token is read from AIDE_GIT_TOKEN (or prompted for), never from a flag.")
        {
            name, idea, ideaFile, approvalMode, compliance, maxFiles, maxCycles, tokenBudget, gitProvider, repoUrl, defaultBranch, noInput,
            llmProvider, llmModel, llmConnection,
        };

        // FR-156: `aide project create` is the v4 name of the same command.
        command.Aliases.Add("create");

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var terminal = context.Host.Terminal;
            var canPrompt = terminal.IsInteractive && !context.Get(noInput);

            string Required(string? value, string flag, string prompt)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }

                if (!canPrompt)
                {
                    throw new UsageException($"{flag} is required (no prompts: stdin is not a terminal or --no-input was given).");
                }

                var answer = terminal.Prompt(prompt)?.Trim();
                return string.IsNullOrEmpty(answer) ? throw new UsageException($"{flag} is required.") : answer;
            }

            string? Optional(string? value, string prompt)
            {
                if (value is not null || !canPrompt)
                {
                    return value;
                }

                var answer = terminal.Prompt(prompt)?.Trim();
                return string.IsNullOrEmpty(answer) ? null : answer;
            }

            if (context.Get(idea) is not null && context.Get(ideaFile) is not null)
            {
                throw new UsageException("Use either --idea or --idea-file, not both.");
            }

            var projectName = Required(context.Get(name), "--name", "Project name: ");
            var ideaText = context.Get(ideaFile) is { } file
                ? ReadText(file, terminal)
                : Required(context.Get(idea), "--idea", "Idea (one line; use --idea-file for more): ");

            var mode = Optional(context.Get(approvalMode), "Approval mode [advisory/strict/autonomous] (advisory): ")?.ToLowerInvariant();
            if (mode is not null and not ("strict" or "advisory" or "autonomous"))
            {
                throw new UsageException($"Unknown approval mode '{mode}'.");
            }

            var provider = Optional(context.Get(gitProvider), "Git provider [local/github] (local): ")?.ToLowerInvariant() ?? "local";
            if (provider is not ("local" or "github"))
            {
                throw new UsageException($"Unknown git provider '{provider}'.");
            }

            string? repo = context.Get(repoUrl);
            string? gitToken = null;
            if (provider == "github")
            {
                repo = Required(repo, "--repo-url", "GitHub repository URL: ");
                gitToken = context.Host.GetEnvironmentVariable(EnvironmentVariables.GitToken);
                if (string.IsNullOrWhiteSpace(gitToken) && canPrompt)
                {
                    gitToken = terminal.PromptSecret("GitHub access token (empty to add later in settings): ");
                }

                gitToken = string.IsNullOrWhiteSpace(gitToken) ? null : gitToken.Trim();
            }
            else if (repo is not null)
            {
                throw new UsageException("--repo-url is only used with --git-provider github.");
            }

            // FR-156, FR-159: the LLM choice is flags only (never prompted); omitted, the server's default applies.
            var chosenProvider = context.Get(llmProvider);
            var chosenModel = context.Get(llmModel);
            var chosenConnection = context.Get(llmConnection);
            CreateProjectLlmRequest? llm = null;
            if (chosenProvider is not null)
            {
                if (string.IsNullOrWhiteSpace(chosenModel))
                {
                    throw new UsageException("--llm-model is required with --llm-provider.");
                }

                llm = new CreateProjectLlmRequest(chosenProvider.ToLowerInvariant(), chosenModel.Trim(), chosenConnection);
            }
            else if (chosenModel is not null || chosenConnection is not null)
            {
                throw new UsageException("--llm-model and --llm-connection need --llm-provider.");
            }

            var request = new CreateProjectRequest(
                projectName,
                ideaText,
                mode is null ? null : Enum.Parse<ApprovalMode>(mode, ignoreCase: true),
                context.Get(compliance),
                context.Get(maxFiles),
                context.Get(maxCycles),
                context.Get(tokenBudget),
                new CreateProjectGitRequest(provider == "github" ? GitProvider.GithubPat : GitProvider.Local, repo, context.Get(defaultBranch), gitToken),
                llm);

            var created = await context.Api().PostAsync<ProjectDto>("api/projects", request, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(created.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var project = created.Value;
            await context.Out.WriteLineAsync(
                $"Created project {project.Name} ({project.Id}): phase {project.CurrentPhase}, status {project.Status}, approval mode {project.ApprovalMode}.").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Next: aide status {project.Id}").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildStatus(CommandFactory factory)
    {
        var id = ProjectIdArgument();
        var command = new Command("status", "Show a project's lifecycle state: phase, status, released and in-development versions, checkpoint, preset, artifacts, execution, review, gates and the latest deployment.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var state = await context.Api().GetAsync<ProjectStateDto>($"api/projects/{projectId}/state", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(state.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var project = await context.Api().GetAsync<ProjectDto>($"api/projects/{projectId}", cancellationToken).ConfigureAwait(false);
            await context.Out.WriteLineAsync(FormatState(project.Value, state.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    internal static string FormatState(ProjectDto project, ProjectStateDto s)
    {
        var lines = new List<string>
        {
            $"{project.Name} ({s.ProjectId})",
            string.Empty,
            Output.KeyValues(
            [
                ("Phase", s.Phase),
                ("Status", s.Status),
                ("Released", s.ReleasedVersion ?? "not released"),
                ("In development", s.DevelopmentVersion is { } development ? $"{development} (checkpoint {Output.Num(s.Checkpoint)})" : "-"),
                ("Checkpoint", Output.Num(s.Checkpoint)),
                ("Preset", VersionCommands.PresetText(project.PresetName ?? project.PresetId, project.PresetVersion)),
                ("Approval mode", project.ApprovalMode),
                ("Execution", s.ExecutionPaused ? $"{s.ExecutionStatus} (paused)" : s.ExecutionStatus),
                ("Steps", string.Create(CultureInfo.InvariantCulture,
                    $"{s.Execution.Completed}/{s.Execution.Total} completed, {s.Execution.InProgress} in progress, {s.Execution.Pending} pending, {s.Execution.Blocked} blocked, {s.Execution.Rejected} rejected")),
                ("Review", string.Create(CultureInfo.InvariantCulture,
                    $"cycle {s.Review.Cycle}/{s.Review.MaxCycles}, {s.Review.Status}, score {(s.Review.LatestScore is { } score ? score.ToString("0.00", CultureInfo.InvariantCulture) : "-")}, decision {s.Review.LatestDecision ?? "-"}, drift {s.Review.DriftLevel ?? "-"}")),
                ("Tokens", $"{Output.Num(s.TokensUsed)} used of {(s.TokenBudget is null ? "unlimited" : Output.Num(s.TokenBudget))}"),
                ("Pull request", s.PullRequest is null ? "-" : $"{s.PullRequest.HeadBranch} -> {s.PullRequest.BaseBranch} {s.PullRequest.Url ?? string.Empty}".TrimEnd()),
                ("Latest release", s.LatestRelease is null ? "-" : $"{s.LatestRelease.Tag} ({s.LatestRelease.Status})"),
                ("Latest deployment", s.LatestDeployment is not { } d ? "-"
                    : $"{d.Target} {d.ReleaseVersion} {VersionCommands.StatusText(d)} {d.Url ?? string.Empty}".TrimEnd() + $" ({Output.Time(d.CreatedAt)})"),
            ]),
            string.Empty,
            "Artifacts:",
        };

        lines.Add(Output.Table(
            ["  TYPE", "LATEST", "STATUS", "LOCKED"],
            s.Artifacts.Select(a => (IReadOnlyList<string>)["  " + a.Type, Output.Num(a.LatestVersion), a.LatestStatus ?? "-", Output.Num(a.CurrentLockedVersion)])));

        lines.Add(string.Empty);
        if (s.PendingGates.Count == 0)
        {
            lines.Add("Pending gates: none");
        }
        else
        {
            lines.Add("Pending gates:");
            lines.AddRange(s.PendingGates.Select(g => $"  {g.Type} {g.Id} since {Output.Time(g.CreatedAt)}{(g.Overdue ? " (overdue)" : string.Empty)}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Reads a file, or stdin when <paramref name="path"/> is `-`.</summary>
    internal static string ReadText(string path, Hosting.ITerminal terminal)
    {
        if (path == "-")
        {
            return terminal.ReadAllInput();
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CliException($"Could not read {path}: {ex.Message}");
        }
    }
}
