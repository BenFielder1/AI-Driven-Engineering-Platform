using System.CommandLine;
using System.Globalization;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>`aide execute`, `aide review`, `aide release`, `aide rollback`.</summary>
internal static class LifecycleCommands
{
    public static Command BuildExecute(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var pause = new Option<bool>("--pause") { Description = "Pause execution after the running step." };
        var resume = new Option<bool>("--resume") { Description = "Resume paused execution." };
        var command = new Command("execute", "Start execution of the approved plan (or --pause / --resume it). Contributor or Owner.") { id, pause, resume };
        command.Validators.Add(result =>
        {
            if (result.GetValue(pause) && result.GetValue(resume))
            {
                result.AddError("--pause and --resume cannot be used together.");
            }
        });

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var action = context.Get(pause) ? "pause" : context.Get(resume) ? "resume" : "start";
            var result = await context.Api()
                .PostAsync<ExecutionStateDto>($"api/projects/{context.Get(id)}/execution/{action}", body: null, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var s = result.Value;
            var verb = action switch { "pause" => "Pause requested", "resume" => "Execution resumed", _ => "Execution started" };
            await context.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{verb}: {s.Status}{(s.Paused ? " (paused)" : string.Empty)}, step {Output.Num(s.CurrentStepNumber)}, {s.Counts.Completed}/{s.Counts.Total} steps completed.")).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildReview(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var command = new Command("review", "Start a review cycle (compliance, drift, code review). Contributor or Owner.") { id };
        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var result = await context.Api().PostAsync<ReviewStartedDto>($"api/projects/{context.Get(id)}/review", body: null, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            await context.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture, $"Review cycle {result.Value.Cycle} queued (job {result.Value.JobId}).")).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildRelease(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var comment = new Option<string?>("--comment", "-m") { Description = "Comment recorded when approving the pre-release gate." };
        var command = new Command(
            "release",
            "Release a project. Owner only. Approves the pending pre_release gate (which merges and tags); " +
            "if there is none, retries a failed release.")
        {
            id, comment,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var projectId = context.Get(id);
            var gates = await context.Api().GetAsync<List<GateDto>>($"api/projects/{projectId}/gates?status=pending", cancellationToken).ConfigureAwait(false);
            var preRelease = gates.Value.FirstOrDefault(g => g.Type == "pre_release");
            if (preRelease is not null)
            {
                var approved = await context.Api()
                    .PostAsync<GateDto>($"api/projects/{projectId}/gates/{preRelease.Id}/approve", new CommentRequest(context.Get(comment)), cancellationToken)
                    .ConfigureAwait(false);
                if (context.Json)
                {
                    await context.WriteRawJsonAsync(approved.Raw).ConfigureAwait(false);
                    return ExitCodes.Success;
                }

                await context.Out.WriteLineAsync($"Pre-release gate {approved.Value.Id} approved; the release is queued. Follow it with 'aide status {projectId}'.").ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var release = await context.Api().PostAsync<ReleaseDto>($"api/projects/{projectId}/release", body: null, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(release.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var r = release.Value;
            await context.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Release retry queued: tag {r.Tag}, checkpoint {r.Checkpoint}, attempt {r.Attempt}, status {r.Status}.")).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command BuildRollback(CommandFactory factory)
    {
        var id = ProjectCommands.ProjectIdArgument();
        var version = new Argument<int>("checkpoint") { Description = "The checkpoint of the version in development to roll back to.", CustomParser = Parsers.IntInRange("checkpoint", 1, int.MaxValue) };
        var reason = new Option<string>("--reason", "-r") { Description = "Why (1-500 characters; recorded in the audit trail).", Required = true };
        var command = new Command(
            "rollback",
            "Roll a project back to an earlier checkpoint of the version in development. Owner only. Later artifacts are invalidated, never deleted; limited to one per minute (429).")
        {
            id, version, reason,
        };

        factory.SetHandler(command, async (context, cancellationToken) =>
        {
            var why = context.Get(reason)?.Trim();
            if (string.IsNullOrEmpty(why))
            {
                throw new UsageException("--reason must not be empty.");
            }

            var result = await context.Api()
                .PostAsync<RollbackDto>($"api/projects/{context.Get(id)}/rollback", new RollbackRequest(context.Get(version), why), cancellationToken)
                .ConfigureAwait(false);
            if (context.Json)
            {
                await context.WriteRawJsonAsync(result.Raw).ConfigureAwait(false);
                return ExitCodes.Success;
            }

            var r = result.Value;
            await context.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Rolled back from checkpoint {r.FromCheckpoint} to checkpoint {r.ToCheckpoint}; phase is now {r.RestoredPhase}. " +
                $"Invalidated {r.InvalidatedArtifactIds?.Count ?? 0} artifact(s), reset {r.ResetStepIds?.Count ?? 0} step(s), cancelled {r.CancelledGateIds?.Count ?? 0} gate(s).")).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }
}
