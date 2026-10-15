using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Security.Cryptography;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>`aide artifact list|show|history|approve|reject|edit`.</summary>
internal static class ArtifactCommands
{
    private static readonly Dictionary<string, string> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project_request"] = "project_request",
        ["request"] = "project_request",
        ["technical_specification"] = "technical_specification",
        ["spec"] = "technical_specification",
        ["implementation_plan"] = "implementation_plan",
        ["plan"] = "implementation_plan",
        ["optimization_plan"] = "optimization_plan",
        ["optimization"] = "optimization_plan",
    };

    private const string TypeHelp = "project_request (request), technical_specification (spec), implementation_plan (plan), optimization_plan (optimization)";

    public static Command Build(CommandFactory factory)
    {
        var command = new Command("artifact", "Show, approve, reject or edit versioned artifacts.");
        command.Subcommands.Add(BuildList(factory));
        command.Subcommands.Add(BuildShow(factory));
        command.Subcommands.Add(BuildHistory(factory));
        command.Subcommands.Add(BuildDecision(factory, "approve", "Approve (lock) a draft artifact version. Owner only."));
        command.Subcommands.Add(BuildDecision(factory, "reject", "Reject a draft artifact version. Owner only."));
        command.Subcommands.Add(BuildEdit(factory));
        return command;
    }

    internal static bool TryNormalizeType(string value, out string type) => TypeAliases.TryGetValue(value.Trim(), out type!);

    private static Argument<string> TypeArgument() => new("type")
    {
        Description = $"Artifact type: {TypeHelp}.",
        CustomParser = ParseType,
    };

    private static string ParseType(ArgumentResult result)
    {
        var raw = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;
        if (TryNormalizeType(raw, out var type))
        {
            return type;
        }

        result.AddError($"Unknown artifact type '{raw}'. Use one of: {TypeHelp}.");
        return string.Empty;
    }

    private static Command BuildList(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("list", "List the latest version of each artifact type.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api().GetAsync<List<ArtifactSummaryDto>>($"api/projects/{context.Get(id)}/artifacts", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(result.Value.Count == 0 ? "No artifacts yet." : SummaryTable(result.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildHistory(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var type = TypeArgument();
        var command = new Command("history", "List every version of one artifact type.") { id, type };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api()
                .GetAsync<List<ArtifactSummaryDto>>($"api/projects/{context.Get(id)}/artifacts/{context.Get(type)}/history", cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(result.Value.Count == 0 ? "No versions." : SummaryTable(result.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildShow(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var type = TypeArgument();
        var version = new Option<int?>("--version", "-v") { Description = "A specific version (default: latest).", CustomParser = r => Parsers.IntInRange("--version", 1, int.MaxValue)(r) };
        var contentOnly = new Option<bool>("--content-only") { Description = "Print only the Markdown content." };
        var command = new Command("show", "Show an artifact version with its metadata and content.") { id, type, version, contentOnly };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var requested = context.Get(version);
            var path = requested is { } v
                ? $"api/projects/{context.Get(id)}/artifacts/{context.Get(type)}/versions/{v}"
                : $"api/projects/{context.Get(id)}/artifacts/{context.Get(type)}/latest";
            var result = await context.Api().GetAsync<ArtifactDto>(path, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var a = result.Value;
            if (!context.Get(contentOnly))
            {
                await context.Out.WriteLineAsync(Describe(a)).ConfigureAwait(false);
                await context.Out.WriteLineAsync().ConfigureAwait(false);
            }

            await context.Out.WriteLineAsync(a.Content).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildDecision(CommandFactory factory, string decision, string description)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var artifact = new Argument<string>("artifact") { Description = $"Artifact version id (GUID), or a type to use its latest version: {TypeHelp}." };
        var comment = new Option<string?>("--comment", "-m") { Description = "Comment recorded with the decision (max 4000 characters)." };
        var command = new Command(decision, description) { id, artifact, comment };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var reference = context.Get(artifact)!;
            Guid artifactId;
            if (!Guid.TryParse(reference, out artifactId))
            {
                if (!TryNormalizeType(reference, out var type))
                {
                    throw new UsageException($"'{reference}' is neither an artifact id nor an artifact type ({TypeHelp}).");
                }

                var latest = await context.Api().GetAsync<ArtifactDto>($"api/projects/{projectId}/artifacts/{type}/latest", cancellationToken).ConfigureAwait(false);
                artifactId = latest.Value.Id;
            }

            var result = await context.Api()
                .PostAsync<ArtifactDto>($"api/projects/{projectId}/artifacts/{artifactId}/{decision}", new CommentRequest(context.Get(comment)), cancellationToken)
                .ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var a = result.Value;
            await context.Out.WriteLineAsync($"{a.Type} v{a.Version} is now {a.Status}.").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildEdit(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var type = TypeArgument();
        var file = new Option<string?>("--file", "-f") { Description = "Upload this Markdown file ('-' for stdin) instead of opening an editor." };
        var force = new Option<bool>("--force") { Description = "Save even if a newer version appeared meanwhile (omits baseVersion)." };
        var command = new Command(
            "edit",
            "Create a new draft version: edit the latest content in $VISUAL/$EDITOR (or upload --file), then save it with baseVersion " +
            "so a version saved meanwhile is never silently superseded (409 → exit 4).")
        {
            id, type, file, force,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var artifactType = context.Get(type)!;
            var filePath = context.Get(file);
            if (filePath is null && !context.Host.Terminal.IsInteractive)
            {
                throw new UsageException("stdin is not a terminal, so no editor can be opened; pass --file <path> (or --file - for stdin).");
            }

            ArtifactDto? latest = null;
            try
            {
                latest = (await context.Api().GetAsync<ArtifactDto>($"api/projects/{projectId}/artifacts/{artifactType}/latest", cancellationToken).ConfigureAwait(false)).Value;
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode == "NOT_FOUND")
            {
                // No version of this type yet (or no access: the save below reports that).
            }

            string? editedFile = null;
            var keepEditedFile = false;
            ApiResult<ArtifactDto> created;
            try
            {
                string content;
                if (filePath is not null)
                {
                    content = ProjectCommands.ReadText(filePath, context.Host.Terminal);
                }
                else
                {
                    var original = latest?.Content ?? string.Empty;
                    editedFile = await CreatePrivateTempFileAsync(artifactType, original, cancellationToken).ConfigureAwait(false);

                    // From here until the save succeeds, a failure keeps the file so no edit is lost.
                    keepEditedFile = true;
                    await context.Host.Editor.EditAsync(editedFile, cancellationToken).ConfigureAwait(false);
                    content = await File.ReadAllTextAsync(editedFile, cancellationToken).ConfigureAwait(false);
                    if (content == original)
                    {
                        keepEditedFile = false;
                        await context.Error.WriteLineAsync("No changes; no draft was created.").ConfigureAwait(false);
                        return ExitCodes.Success;
                    }
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    keepEditedFile = false;
                    throw new UsageException("The content is empty; no draft was created.");
                }

                var request = new CreateArtifactDraftRequest(content, context.Get(force) ? null : latest?.Version);
                created = await context.Api().PostAsync<ArtifactDto>($"api/projects/{projectId}/artifacts/{artifactType}", request, cancellationToken).ConfigureAwait(false);
                keepEditedFile = false;
            }
            catch (Exception) when (editedFile is not null && keepEditedFile)
            {
                await context.Error.WriteLineAsync($"Your edit was kept at {editedFile} (re-upload it with --file).").ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (editedFile is not null && !keepEditedFile)
                {
                    TryDelete(editedFile);
                }
            }

            if (context.Json)
            {
                await context.WriteRawJsonAsync(created.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var a = created.Value;
            await context.Out.WriteLineAsync($"Saved {a.Type} v{a.Version} as {a.Status} (parent v{Output.Num(a.ParentVersion)}, hash {a.ContentHash}).").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>
    /// Creates the editor's temp file with an unpredictable name, failing if it already exists; owner-only (0600) on Unix.
    /// On Windows the per-user temp folder is already private to the user.
    /// </summary>
    internal static async Task<string> CreatePrivateTempFileAsync(string artifactType, string content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aide-{artifactType}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.md");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the temp folder is cleaned eventually.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static string SummaryTable(IEnumerable<ArtifactSummaryDto> items) => Output.Table(
        ["ID", "TYPE", "VERSION", "STATUS", "INTEGRITY", "CREATED", "AUTHOR"],
        items.Select(a => (IReadOnlyList<string>)
        [
            a.Id.ToString(), a.Type, Output.Num(a.Version), a.Status, a.Integrity, Output.Time(a.CreatedAt),
            a.Author?.User?.Name ?? (a.Author?.AgentRunId is not null ? "agent" : "-"),
        ]));

    private static string Describe(ArtifactDto a) => Output.KeyValues(
    [
        ("Artifact", $"{a.Type} v{a.Version} ({a.Id})"),
        ("Status", a.Status),
        ("Integrity", a.Integrity),
        ("Parent", Output.Num(a.ParentVersion)),
        ("Hash", a.ContentHash),
        ("Checkpoint", Output.Num(a.CheckpointAtCreation)),
        ("Author", a.Author?.User?.Name ?? (a.Author?.AgentRunId is { } run ? $"agent run {run}" : "-")),
        ("Created", Output.Time(a.CreatedAt)),
        ("Approved", a.ApprovedBy is null ? "-" : $"{a.ApprovedBy.Name} at {Output.Time(a.ApprovedAt)}"),
        ("Rejection", a.RejectionComment ?? "-"),
    ]);
}
