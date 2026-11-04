using System.CommandLine;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>FR-194: `aide analysis show|approve|reject|retry` — the Analysis page (FR-182, FR-183) of a version that infers its stack.</summary>
internal static class AnalysisCommands
{
    public static Command Build(CommandFactory factory)
    {
        var command = new Command("analysis", "The Analysis phase of a version that infers its stack: the Stack Analyst's profile, approving or rejecting it, and retrying the analyst.");
        command.Subcommands.Add(BuildShow(factory));
        command.Subcommands.Add(BuildDecision(factory, approve: true));
        command.Subcommands.Add(BuildDecision(factory, approve: false));
        command.Subcommands.Add(BuildRetry(factory));
        return command;
    }

    private static Command BuildShow(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command(
            "show",
            "Show the Analysis page of the project's current version: the Repository and Stack Analysts' state, the latest stack profile and its layers, what needs your input, and whether it can be approved.")
        {
            id,
        };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var analysis = await context.Api().GetAsync<AnalysisDto>(Path(context.Get(id)), cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(analysis.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(Format(analysis.Value)).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildDecision(CommandFactory factory, bool approve)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var comment = new Option<string?>("--comment", "-c") { Description = approve ? "Optional approval comment." : "Why: the Stack Analyst runs again with it." };
        var generalist = new Option<bool>("--generalist")
        {
            Description = "Approve with No stack (general) instead of the profile's composition (FR-181): for a technology outside the catalog. Unknown and conflicting layers still need answers.",
        };
        var command = approve
            ? new Command(
                "approve",
                "Approve the latest stack profile draft (Owner only): locks it and pins its composition. Exit 4 for STACK_PROFILE_INCOMPLETE (the unresolved layers are listed) or STACK_INCOMPATIBLE.")
            {
                id, comment, generalist,
            }
            : new Command("reject", "Reject the latest stack profile draft (Owner only); the Stack Analyst runs again with the comment and the rejected draft.")
            {
                id, comment,
            };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var analysis = await context.Api().GetAsync<AnalysisDto>(Path(projectId), cancellationToken).ConfigureAwait(false);
            var profile = analysis.Value.Profile
                ?? throw new CliException("There is no stack profile to decide yet. Run 'aide analysis show' to see the Stack Analyst's state.");
            if (profile.Status != "draft")
            {
                throw new CliException($"Stack profile v{profile.Version} is {profile.Status}, not a draft: there is nothing to {(approve ? "approve" : "reject")}.");
            }

            var text = context.Get(comment);
            object body = approve ? new ApproveStackProfileRequest(text, context.Get(generalist) ? true : null) : new CommentRequest(text);
            var decided = await context.Api()
                .PostAsync<ArtifactDto>($"api/projects/{projectId}/artifacts/{profile.ArtifactId}/{(approve ? "approve" : "reject")}", body, cancellationToken)
                .ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(decided.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(approve
                ? $"Approved stack profile v{decided.Value.Version}: the stack is pinned. Next: continue to Refinement on the Analysis page."
                : $"Rejected stack profile v{decided.Value.Version}: the Stack Analyst runs again. Next: aide analysis show {projectId}").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildRetry(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("retry", "Run the Stack Analyst again (after a failed run, or to redo the profile). Writer only; exit 4 for WRONG_PHASE or INVALID_STATE.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var queued = await context.Api().PostAsync<StackAnalysisQueuedDto>($"{Path(projectId)}/run", body: null, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(queued.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync($"Queued the Stack Analyst (job {queued.Value.JobId}). Next: aide analysis show {projectId}").ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static string Path(Guid projectId) => $"api/projects/{projectId}/analysis";

    internal static string Format(AnalysisDto a)
    {
        var lines = new List<string>
        {
            Output.KeyValues(
            [
                ("Version", $"{a.ReleaseVersion} (phase {a.Phase}, stack {(a.StackSource == "infer" ? "inferred" : a.StackSource)})"),
                ("Base", a.BaseCommitSha is { } sha ? VersionCommands.ShortSha(sha) : "no code"),
                ("Codebase summary", SummaryText(a.Summary)),
                .. a.StackSource == "infer"
                    ? new (string, string)[]
                    {
                        ("Stack Analyst", RunText(a.Run)),
                        ("Profile", a.Profile is { } p ? $"v{p.Version} {p.Status}{(p.AuthoredByAgent ? " (by the Stack Analyst)" : " (edited)")}" : "none yet"),
                        ("Approve", a.CanApprove ? "yes" : $"no{(a.ApproveBlockedReason is { } why ? $": {why}" : string.Empty)}"),
                        ("Continue", a.CanContinue ? "yes: the profile is locked" : "no"),
                    }
                    : [],
                ("Stack", StackCommands.Summary(a.Composition)),
            ]),
        };

        if (a.StackSource != "infer")
        {
            lines.Add("This version doesn't infer its stack: there is no profile to approve.");
            return string.Join(Environment.NewLine, lines);
        }

        if (a.NeedsInput is { Count: > 0 } blanks)
        {
            lines.Add(string.Empty);
            lines.Add("Needs your input:");
            lines.AddRange(blanks.Select(b => $"  {b.Layer} ({b.Status}): {BlankText(b)}"));
        }

        if (a.Profile?.Document is { } document)
        {
            var l = document.Layers;
            lines.Add(string.Empty);
            lines.Add(Output.Table(
                ["LAYER", "OPTION", "PATH", "MODE", "STATUS", "CONFIDENCE"],
                new[] { ("frontend", l.Frontend), ("backend", l.Backend), ("data", l.Data), ("vcs", l.Vcs) }
                    .Select(x => (IReadOnlyList<string>)[x.Item1, x.Item2.Option ?? "-", x.Item2.Path ?? "-", x.Item2.Mode ?? "-", x.Item2.Status, x.Item2.Confidence ?? "-"])));
            if (document.Generalist)
            {
                lines.Add("Continue with No stack (general) is chosen.");
            }
        }
        else if (a.Profile is { Errors: { Count: > 0 } errors })
        {
            lines.Add("The profile can't be read: " + string.Join("; ", errors));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string SummaryText(CodebaseSummaryStatusDto? summary) =>
        summary is null ? "-" : summary.ArtifactVersion is { } v ? $"{summary.Status} (v{Output.Num(v)})" : summary.Status;

    private static string RunText(StackAnalysisRunDto? run) =>
        run is null ? "not run"
        : run.ErrorCode is { } code ? $"{run.Status} ({code})"
        : run.Warnings is { Count: > 0 } warnings ? $"{run.Status} (warnings: {string.Join(", ", warnings)})"
        : run.Status;

    private static string BlankText(StackProfileBlankDto blank)
    {
        var parts = new List<string>();
        if (blank.Question is { } question)
        {
            parts.Add(question);
        }

        if (blank.Technology is { } technology)
        {
            parts.Add($"found {technology}, which isn't in the catalog");
        }

        if (blank.Candidates is { Count: > 0 } candidates)
        {
            parts.Add("candidates: " + string.Join(", ", candidates.Select(c => c.Path is null ? c.Option : $"{c.Option} at {c.Path}")));
        }

        return parts.Count == 0 ? "choose an option" : string.Join("; ", parts);
    }
}
