using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-349, §4.67: multiple output variants. A <c>variants: { count: n }</c> stage runs as a fan-out of its own agent with
/// branches <c>v1…vn</c> (<see cref="FanOutRules.Branches"/>), each storing the stage's output labelled with its variant.
/// When every variant has completed, one is selected: <c>select: gate</c> raises a <c>variant_select</c> gate (its payload
/// the labels and artifact ids, never the content) and the stage waits for the decider's <c>choice</c>; a selector agent
/// gets every variant (labelled, as <c>{{&lt;OUTPUT&gt;_ALL}}</c>) and answers <c>{ choice, reason }</c>. The chosen
/// variant is promoted: its content is copied to a new plain (non-variant) version of the output, which downstream stages
/// read; the variants stay as they are, read-only. Then the stage's transition runs.
/// </summary>
public sealed partial class EngineRunDriver
{
    /// <summary>
    /// FR-349: the variants' join. Selects, promotes and transitions (saved there; true), or raises or waits on the
    /// <c>variant_select</c> gate (the caller saves; false).
    /// </summary>
    private async Task<bool> SelectVariantAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution fanOutEntry, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var output = stage.Output
            ?? throw new JobFailedException($"Variants stage '{stage.Id}' has no output.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        var store = new EngineAgentStageStore(db);
        var stored = await store.RunVariantsAsync(run, output, cancellationToken);
        var labels = VariantRules.Labels(stage);
        var variants = labels
            .Select(l => stored.FirstOrDefault(a => string.Equals(a.Variant, l, StringComparison.Ordinal)))
            .OfType<Artifact>()
            .ToList();
        var refs = variants.Select(a => new VariantRef(a.Variant!, a.Id)).ToList();

        string choice;
        if (stage.Variants!.SelectAgent is { } selector)
        {
            choice = await AskSelectorAsync(job, run, stage, selector, output, variants, cancellationToken);
        }
        else
        {
            var enteredAt = fanOutEntry.EnteredAt;
            var runId = run.Id;
            var gates = await db.ApprovalGates.AsNoTracking()
                .Where(g => g.PipelineRunId == runId && g.Type == EngineGates.VariantSelect && g.CreatedAt >= enteredAt)
                .ToListAsync(cancellationToken);
            if (gates.FirstOrDefault(g => g.Status == GateStatus.Approved) is not { } approved)
            {
                if (gates.Count == 0)
                {
                    // The run row is written with the gate (the caller's save), so a concurrent move makes it fail on xmin.
                    EngineRunMoves.Touch(db, run);
                    db.ApprovalGates.Add(new ApprovalGate
                    {
                        ProjectId = run.ProjectId,
                        ReleaseVersionId = run.SubjectId,
                        PipelineRunId = run.Id,
                        Type = EngineGates.VariantSelect,
                        Status = GateStatus.Pending,
                        Payload = VariantRules.GatePayload(stage.Id, refs),
                        CreatedAt = time.GetUtcNow(),
                    });
                    LogVariantGateRaised(logger, run.Id, stage.Id, refs.Count);
                }

                // Pending: the decision re-queues the driver. Rejected: the decision already failed the run.
                return false;
            }

            choice = VariantRules.PayloadChoice(approved.Payload) ?? string.Empty;
        }

        if (VariantRules.Chosen(stage, refs, choice) is not { } chosen)
        {
            throw new JobFailedException(
                $"The variant choice '{choice}' for {stage.Id} is not one of {string.Join(", ", labels)}.",
                transient: false,
                EngineErrorCodes.VariantChoiceInvalid);
        }

        var promoted = await PromoteAsync(store, run, variants.Single(a => a.Id == chosen.ArtifactId), cancellationToken);
        LogVariantPromoted(logger, run.Id, stage.Id, chosen.Label, promoted.Version);
        try
        {
            await TransitionAsync(job, run, stage, fanOutEntry, definition, promoted.Content, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is System.Data.Common.DbException { SqlState: "23505" })
        {
            // A concurrent promotion took the same version number (unique index) or moved the run first (xmin).
            throw Moved(ex);
        }

        return true;
    }

    /// <summary>FR-349: the selector agent's run (its own run key, so a redelivery reuses it) over every variant; its choice.</summary>
    private async Task<string> AskSelectorAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, string selector, string output, IReadOnlyList<Artifact> variants, CancellationToken cancellationToken)
    {
        var items = variants.Select(a => new AggregateItem(a.Variant!, a.Content));
        var selectRun = await agents.RunAsync(
            new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
            {
                AgentKey = selector,
                AgentRunId = AgentRunRules.RunId(job.Id, VariantRules.SelectRunKey(selector)),
                JobAttempt = job.Attempt,
                JobId = job.Id,
                PromptValues = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [FanOutRules.AllPlaceholder(output)] = FanOutRules.RenderList(items),
                },
            },
            cancellationToken);
        selectRun.ThrowIfFailed();
        if (selectRun.End != AgentStageEnd.Stored)
        {
            throw new JobFailedException($"The {selector} selector of {stage.Id} did not finish.", transient: false, EngineErrorCodes.Validation);
        }

        var selection = VariantRules.ReadSelection(OutputText(selectRun.Last))
            ?? throw new JobFailedException(
                $"The {selector} selector of {stage.Id} must answer {{ \"choice\": \"v1\", \"reason\": \"...\" }}.",
                transient: false,
                EngineErrorCodes.VariantChoiceInvalid);
        return selection.Choice;
    }

    /// <summary>
    /// FR-349: the chosen variant becomes the stage's output: a new plain version (no variant) with its content, status
    /// and agent run, numbered after the output's versions; older plain drafts in the subject are superseded as for any
    /// new version. The variant rows are not changed (locked artifacts are immutable).
    /// </summary>
    private async Task<Artifact> PromoteAsync(EngineAgentStageStore store, PipelineRun run, Artifact chosen, CancellationToken cancellationToken)
    {
        var plan = ArtifactRules.NewDraft(await store.VersionsAsync(run.ProjectId, chosen.Type, cancellationToken));
        foreach (var draft in (await store.SubjectVersionsAsync(run, chosen.Type, plan.SupersededVersions, cancellationToken))
            .Where(d => ArtifactRules.CanTransition(d.Status, ArtifactStatus.Superseded)))
        {
            draft.Status = ArtifactStatus.Superseded;
        }

        var now = time.GetUtcNow();
        var promoted = new Artifact
        {
            ProjectId = run.ProjectId,
            Type = chosen.Type,
            Version = plan.Version,
            ParentVersion = plan.ParentVersion,
            Content = chosen.Content,
            ContentHash = chosen.ContentHash,
            Status = chosen.Status,
            ApprovedBy = chosen.ApprovedBy,
            ApprovedAt = chosen.Status == ArtifactStatus.Locked ? now : null,
            CheckpointAtCreation = run.Checkpoint,
            ReleaseVersionId = chosen.ReleaseVersionId,
            PipelineRunId = run.Id,
            AgentRunId = chosen.AgentRunId,
            CreatedAt = now,
        };
        db.Artifacts.Add(promoted);
        return promoted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} raised a variant_select gate on {StageId} ({VariantCount} variants)")]
    private static partial void LogVariantGateRaised(ILogger logger, Guid pipelineRunId, string stageId, int variantCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} promoted variant {Variant} of {StageId} to version {Version}")]
    private static partial void LogVariantPromoted(ILogger logger, Guid pipelineRunId, string stageId, string variant, int version);
}
