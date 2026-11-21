using System.Text.Json;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

public sealed partial class AgentStageRunner
{
    /// <summary>
    /// Steps 7–8: validates the answer (§4.55) and commits every answered attempt with its verdict (FR-315; a
    /// <c>then: fail</c> verdict fails the run first, with <see cref="AgentStageExtension.AttemptsExhaustedAsync"/>'s failure
    /// and the host's stored error), then stores a valid output. Everything up to the commit runs with
    /// <see cref="CancellationToken.None"/> (under the default policy, through the final save too, since the run is saved
    /// there): the call was paid for, so its run and tokens are never lost to a cancellation.
    /// </summary>
    private async Task<AgentStageOutcome> FinishAsync(
        AgentAttempt attempt, AgentStageExtension extension, ArtifactDefinition? artifactDefinition, string content, string user, CancellationToken cancellationToken)
    {
        var (run, stage, agentRun) = (attempt.Run, attempt.Stage, attempt.AgentRun!);
        var findings = await ValidateAsync(attempt, extension, artifactDefinition, content, CancellationToken.None);
        var verdict = Verdict(extension.OnInvalid(stage), attempt.Request.Attempt, findings);
        AgentStageFailure? failure = null;
        if (findings is not null)
        {
            failure = verdict == AgentRunVerdict.Fail ? await extension.AttemptsExhaustedAsync(attempt, findings, CancellationToken.None) : null;

            // D-0.7.0-33: the call worked, so the run is succeeded, but the marker counts it as a failure in FR-298.
            var error = await extension.RejectedErrorAsync(attempt, findings, verdict, failure, CancellationToken.None);
            agentRun.Error = error;
            if (failure is not null)
            {
                agentRun.Status = AgentRunStatus.Failed;
            }
        }

        // The default policy saves the run with the stage's changes below, so its commit only reports the verdict.
        Func<CancellationToken, Task> save = extension.Policy.CommitRunSeparately ? store.SaveChangesAsync : _ => Task.CompletedTask;
        await extension.CommitRunAsync(attempt, new AgentRunCommit(AgentRunCommitPoint.Answered, verdict, save) { Failure = failure }, CancellationToken.None);
        if (findings is not null)
        {
            await store.SaveChangesAsync(CancellationToken.None);
            return new AgentStageOutcome(false, agentRun, null, findings, user, run, stage) { Failure = failure };
        }

        // Under the default policy the paid run is first saved with the artifact, so storing it isn't cancelled either.
        var storing = extension.Policy.CommitRunSeparately ? cancellationToken : CancellationToken.None;
        return await StoreOutputAsync(attempt, extension, artifactDefinition, content, user, redelivered: false, storing);
    }

    /// <summary>Step 8: content transform (FR-313), the host's veto, store, post-store hook (FR-316), save.</summary>
    private async Task<AgentStageOutcome> StoreOutputAsync(
        AgentAttempt attempt, AgentStageExtension extension, ArtifactDefinition? artifactDefinition, string content, string user, bool redelivered, CancellationToken cancellationToken)
    {
        var (run, stage, agentRun) = (attempt.Run, attempt.Stage, attempt.AgentRun!);
        Artifact? artifact = null;
        if (artifactDefinition is not null)
        {
            var stored = await extension.ToContentAsync(attempt, content, cancellationToken);
            if (await extension.ShouldStoreAsync(attempt, stored, cancellationToken))
            {
                artifact = await StoreAsync(run, artifactDefinition, stored, agentRun, cancellationToken);
                await extension.OnArtifactStoredAsync(attempt, artifact, cancellationToken);
            }
        }

        await store.SaveChangesAsync(cancellationToken);
        return new AgentStageOutcome(true, agentRun, artifact, null, user, run, stage) { Redelivered = redelivered };
    }

    /// <summary>
    /// FR-310: a redelivered attempt whose run already succeeded: no model call and no new run. Its stored output is
    /// validated again (the rules are deterministic, so it takes the same branch). A valid output returns the artifact the
    /// run already produced; only when that is missing (the job stopped between the run's commit and the store) is it
    /// stored now.
    /// </summary>
    private async Task<AgentStageOutcome> RedeliverAsync(AgentAttempt attempt, AgentStageExtension extension, AgentRun existing, CancellationToken cancellationToken)
    {
        attempt.AgentRun = existing;
        var (run, stage) = (attempt.Run, attempt.Stage);
        var artifactDefinition = attempt.Definition.FindArtifact(stage.Output);
        var content = AgentStagePrompt.Content(artifactDefinition?.Format ?? ArtifactFormat.Markdown, ReadOutput(existing.Output)?.Text ?? string.Empty);
        var findings = await ValidateAsync(attempt, extension, artifactDefinition, content, cancellationToken);
        if (findings is not null)
        {
            var failure = Verdict(extension.OnInvalid(stage), attempt.Request.Attempt, findings) == AgentRunVerdict.Fail
                ? await extension.AttemptsExhaustedAsync(attempt, findings, cancellationToken)
                : null;
            return new AgentStageOutcome(false, existing, null, findings, string.Empty, run, stage) { Failure = failure, Redelivered = true };
        }

        if (artifactDefinition is null)
        {
            return new AgentStageOutcome(true, existing, null, null, string.Empty, run, stage) { Redelivered = true };
        }

        if (await store.ArtifactOfAgentRunAsync(run, artifactDefinition.Key, existing.Id, cancellationToken) is { } artifact)
        {
            return new AgentStageOutcome(true, existing, artifact, null, string.Empty, run, stage) { Redelivered = true };
        }

        return await StoreOutputAsync(attempt, extension, artifactDefinition, content, string.Empty, redelivered: true, cancellationToken);
    }

    /// <summary>§4.55: what an attempt's findings lead to.</summary>
    private static AgentRunVerdict Verdict(OnInvalidPolicy? onInvalid, int attempt, string? findings) =>
        findings is null
            ? AgentRunVerdict.Valid
            : StageValidationRules.OnInvalid(onInvalid, attempt) switch
            {
                InvalidOutputAction.Retry => AgentRunVerdict.Retry,
                InvalidOutputAction.Escalate => AgentRunVerdict.Escalate,
                _ => AgentRunVerdict.Fail,
            };

    /// <summary>A stored run's output, or <c>null</c> when it can't be read.</summary>
    public static AgentStageRunOutput? ReadOutput(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentStageRunOutput>(json, OutputJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
