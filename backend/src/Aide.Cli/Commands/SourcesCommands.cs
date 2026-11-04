using System.CommandLine;
using System.Globalization;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>FR-194: `tevrin sources list|import|reimport|remove|check` — the tracker imports of the version in development (FR-170–FR-173).</summary>
internal static class SourcesCommands
{
    internal static readonly string[] Trackers = ["jira", "linear", "github", "gitlab"];

    public static Command Build(CommandFactory factory)
    {
        var command = new Command(
            "sources",
            "Issue tracker items imported into the version in development: list, import, re-import, remove, and check them for changes. Items can change only in analysis and refinement (else exit 4, WRONG_PHASE).");
        command.Subcommands.Add(BuildList(factory));
        command.Subcommands.Add(BuildImport(factory));
        command.Subcommands.Add(BuildReimport(factory));
        command.Subcommands.Add(BuildRemove(factory));
        command.Subcommands.Add(BuildCheck(factory));
        return command;
    }

    private static Command BuildList(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("list", "List the imports of the version in development, newest first, with the current source_items snapshot.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var sources = await context.Api().GetAsync<ProjectSourcesDto>(Path(context.Get(id)), cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(sources.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(FormatList(sources.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildImport(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var connection = new Option<Guid>("--connection") { Description = "Your tracker connection's id (Account > Connections > Issue trackers).", Required = true };
        var tracker = new Option<string>("--tracker") { Description = "The tracker: jira, linear, github or gitlab.", Required = true };
        tracker.AcceptOnlyFromAmong(Trackers);
        var keys = new Option<string?>("--keys") { Description = "Comma-separated item keys, e.g. PROJ-1,PROJ-2 (at most 200)." };
        var query = new Option<string?>("--query") { Description = "A native query: JQL for Jira, search qualifiers for GitHub." };
        var container = new Option<string?>("--container") { Description = "The project, team or repository to read from (a Jira project key, Linear team:KEY, GitHub owner/repo, GitLab group/project)." };
        var comments = new Option<bool>("--comments") { Description = "Include the items' comments (off by default)." };
        var command = new Command(
            "import",
            "Import tracker items into the version in development (writer). Use --keys or --query. The import runs as a job: see 'tevrin sources list'. " +
            "Exit 4 for WRONG_PHASE, TRACKER_NOT_CONNECTED, TRACKER_PERMISSION_MISSING or SOURCE_ITEMS_TOO_LARGE.")
        {
            id, connection, tracker, keys, query, container, comments,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var keyList = ParseKeys(context.Get(keys));
            var text = context.Get(query);
            if (keyList is null == string.IsNullOrWhiteSpace(text))
            {
                throw new UsageException("Use exactly one of --keys and --query.");
            }

            var request = new SourceImportRequest(
                context.Get(tracker)!,
                context.Get(connection),
                new TrackerSelectionRequest(keyList, string.IsNullOrWhiteSpace(text) ? null : text.Trim(), Trimmed(context.Get(container))),
                new TrackerImportOptionsRequest(IncludeChildren: true, IncludeComments: context.Get(comments), IncludeAssignees: false));
            var queued = await context.Api().PostAsync<SourceImportDto>(Path(context.Get(id)), request, cancellationToken).ConfigureAwait(false);
            return await WriteQueuedAsync(context, queued, "Queued", context.Get(id)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildReimport(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var importId = ImportIdArgument();
        var command = new Command(
            "reimport",
            "Run an import's query again; the new snapshot replaces it (writer). Earlier snapshots stay readable. Exit 4 for WRONG_PHASE or TRACKER_NOT_CONNECTED.")
        {
            id, importId,
        };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var queued = await context.Api().PostAsync<SourceImportDto>($"{Path(context.Get(id))}/{context.Get(importId)}/reimport", body: null, cancellationToken).ConfigureAwait(false);
            return await WriteQueuedAsync(context, queued, "Queued the re-import", context.Get(id)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildRemove(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var importId = ImportIdArgument();
        var command = new Command(
            "remove",
            "Remove an import's items from the version (writer): a new source_items snapshot without them. Earlier locked snapshots are kept. Exit 4 for WRONG_PHASE.")
        {
            id, importId,
        };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            await context.Api().SendNoContentAsync(HttpMethod.Delete, $"{Path(context.Get(id))}/{context.Get(importId)}", cancellationToken).ConfigureAwait(false);
            await context.Out.WriteLineAsync(context.Json
                ? ApiJson.ToIndentedJson(new System.Text.Json.Nodes.JsonObject { ["importId"] = context.Get(importId).ToString(), ["removed"] = true })
                : $"Removed import {context.Get(importId)}. Earlier snapshots stay readable.").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildCheck(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command(
            "check",
            "Compare each imported item with the tracker: unchanged, changed, removed or unknown (no connection reaches it). A check never changes a snapshot.")
        {
            id,
        };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var checkedItems = await context.Api().PostAsync<SourcesCheckDto>($"{Path(context.Get(id))}/check", body: null, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(checkedItems.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var items = checkedItems.Value.Items ?? [];
            await context.Out.WriteLineAsync(items.Count == 0 ? "No imported items." : Output.Table(
                ["KEY", "TRACKER", "DRIFT", "TITLE"],
                items.Select(i => (IReadOnlyList<string>)[i.Key, i.Provider, i.Drift, i.Title]))).ConfigureAwait(false);
            if (items.Count > 0)
            {
                var changed = items.Count(i => i.Drift is "changed" or "removed");
                await context.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{changed} of {items.Count} changed or removed since import; re-import to take the changes.")).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Argument<Guid> ImportIdArgument() => new("import-id") { Description = "The import's id (shown by 'tevrin sources list')." };

    private static string Path(Guid projectId) => $"api/projects/{projectId}/sources";

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary><c>--keys</c> as a list (blanks dropped); <c>null</c> when not given; a usage error when it names none.</summary>
    internal static IReadOnlyList<string>? ParseKeys(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var keys = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        return keys.Count == 0 ? throw new UsageException("--keys names no items.") : keys;
    }

    private static async Task<int> WriteQueuedAsync(CommandContext context, ApiResult<SourceImportDto> queued, string verb, Guid projectId)
    {
        if (context.Json)
        {
            await context.WriteRawJsonAsync(queued.Raw).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        var import = queued.Value;
        await context.Out.WriteLineAsync($"{verb} import {import.Id} from {import.Provider} ({import.Status}). Next: tevrin sources list {projectId}").ConfigureAwait(false);
        return ExitCodes.Success;
    }

    /// <summary>The imports as a table, then the snapshot and draft state.</summary>
    internal static string FormatList(ProjectSourcesDto sources)
    {
        var imports = sources.Imports ?? [];
        var lines = new List<string>
        {
            imports.Count == 0 ? "No imports." : Output.Table(
                ["ID", "TRACKER", "STATUS", "ITEMS", "SELECTION", "BY", "CREATED"],
                imports.Select(i => (IReadOnlyList<string>)
                [
                    i.Id.ToString(), i.Provider, i.ErrorCode is { } code ? $"{i.Status} ({code})" : i.Status, Output.Num(i.ItemCount), SelectionText(i.Selection), Output.Actor(i.CreatedBy), Output.Time(i.CreatedAt),
                ])),
            $"Snapshot: {(sources.CurrentSnapshot is { } s ? $"source_items v{Output.Num(s.Version)} ({s.ContentHash[..Math.Min(12, s.ContentHash.Length)]})" : "none")}",
        };

        if (sources.Draft is { BehindSnapshot: true } draft)
        {
            lines.Add($"Sources changed since project request v{Output.Num(draft.Version)}: run another refinement round.");
        }

        if (!sources.CanChange)
        {
            lines.Add("Items can't change now: the project is past refinement, or this isn't the version in development.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>For `status`: "12 items from 2 imports (snapshot v2), 1 queued, 1 failed", or "none".</summary>
    internal static string Summary(ProjectSourcesDto? sources)
    {
        var imports = sources?.Imports ?? [];
        if (imports.Count == 0)
        {
            return "none";
        }

        var active = imports.Where(i => i.Status == "completed").ToList();
        var pending = imports.Count(i => i.Status is "queued" or "running");
        var failed = imports.Count(i => i.Status == "failed");
        var text = string.Create(CultureInfo.InvariantCulture, $"{active.Sum(i => i.ItemCount)} items from {active.Count} import{(active.Count == 1 ? string.Empty : "s")}");
        if (sources?.CurrentSnapshot is { } snapshot)
        {
            text += $" (snapshot v{Output.Num(snapshot.Version)})";
        }

        if (pending > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $", {pending} queued");
        }

        if (failed > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $", {failed} failed");
        }

        return text;
    }

    private static string SelectionText(TrackerSelectionDto? selection)
    {
        if (selection is null)
        {
            return "-";
        }

        var text = selection.Keys is { Count: > 0 } keys
            ? string.Join(",", keys.Take(5)) + (keys.Count > 5 ? $",… ({Output.Num(keys.Count)})" : string.Empty)
            : selection.Query ?? "all matching";
        return selection.ContainerId is { } container ? $"{container}: {text}" : text;
    }
}
