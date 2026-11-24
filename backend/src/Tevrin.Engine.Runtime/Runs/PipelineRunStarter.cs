using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-331: a request to start a run of <paramref name="PipelineId"/> in <paramref name="ProjectId"/>.
/// <paramref name="ProjectApprovalMode"/> is the project's mode (the default); <paramref name="Inputs"/> are input
/// artifacts by key; <paramref name="UserId"/> is who started it (<c>null</c> for a trigger).
/// </summary>
public sealed record StartPipelineRun(
    Guid ProjectId,
    string PipelineId,
    ApprovalMode? ApprovalMode,
    ApprovalMode ProjectApprovalMode,
    IReadOnlyDictionary<string, JsonElement>? Inputs,
    Guid? UserId);

/// <summary>
/// FR-331, D-0.8.0-18: starts engine-controlled runs, for the start endpoint and (from step c) schedule and webhook
/// triggers. The open-run check and the insert run in one transaction under a transaction-scoped advisory lock on
/// (project, pipeline) (<see cref="RunStartLock"/>), so two concurrent starts give one run.
/// </summary>
public sealed class PipelineRunStarter(
    IEngineDbContext db,
    PipelineRegistry registry,
    ArtifactSchemas schemas,
    JobEnqueuer jobs,
    TimeProvider time)
{
    /// <summary>
    /// Creates the run (no subject, pinned to the pipeline's current definition hash, at its first start stage, checkpoint 1),
    /// stores each input as version 1 of its artifact (locked, owned by the run) and queues the driver. Errors:
    /// <c>NOT_FOUND</c> (unknown pipeline), <c>RUN_CONTROLLED_BY_HOST</c>, <c>VALIDATION</c> (undeclared input key or approval
    /// mode the definition doesn't offer), <c>INPUT_INVALID</c> (details: the failing paths), <c>RUN_ALREADY_ACTIVE</c>.
    /// </summary>
    public async Task<Result<PipelineRun>> StartAsync(StartPipelineRun request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!registry.TryGet(request.PipelineId, out var loaded) || loaded.Definition is not { } definition)
        {
            return Result.Failure<PipelineRun>(new DomainError(EngineErrorCodes.NotFound, $"Pipeline '{request.PipelineId}' was not found."));
        }

        if (loaded.RunControl == RunControl.Host)
        {
            return Result.Failure<PipelineRun>(RunControlErrors.ControlledByHost());
        }

        var contents = new List<(ArtifactDefinition Artifact, string Content)>();
        foreach (var (key, value) in request.Inputs ?? new Dictionary<string, JsonElement>())
        {
            if (definition.FindArtifact(key) is not { Input: true } artifact)
            {
                return Result.Failure<PipelineRun>(new DomainError(EngineErrorCodes.Validation, $"'{key}' is not an input of this pipeline.") { Field = $"inputs.{key}" });
            }

            var content = RunInputs.Content(artifact, value, loaded.PackId, schemas);
            if (content.IsFailure)
            {
                return Result.Failure<PipelineRun>(content.Error!);
            }

            contents.Add((artifact, content.Value));
        }

        var mode = RunInputs.ApprovalModeOf(definition, request.ApprovalMode, request.ProjectApprovalMode);
        if (mode is null)
        {
            return Result.Failure<PipelineRun>(new DomainError(EngineErrorCodes.Validation, "This pipeline doesn't offer that approval mode.") { Field = "approvalMode" });
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = RunStartLock.Key(request.ProjectId, request.PipelineId);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

        var pipelineId = request.PipelineId;
        var hasOpenRun = await db.PipelineRuns.AsNoTracking().AnyAsync(
            r => r.ProjectId == request.ProjectId && r.PipelineId == pipelineId
                && r.Status != GenericRunStatus.Completed && r.Status != GenericRunStatus.Failed && r.Status != GenericRunStatus.Cancelled,
            cancellationToken);
        if (!PipelineRunRules.MayStart(definition.Concurrency, hasOpenRun))
        {
            return Result.Failure<PipelineRun>(new DomainError(
                EngineErrorCodes.RunAlreadyActive, "This project already has a run in progress for this pipeline."));
        }

        await StoredDefinitions.StoreForEngineAsync(db, loaded, now, cancellationToken);
        var state = PipelineStateMachine.Start(definition, definition.Start[0]);
        var run = new PipelineRun
        {
            ProjectId = request.ProjectId,
            PipelineId = request.PipelineId,
            DefinitionHash = loaded.Hash!,
            SubjectId = null,
            CurrentStage = state.Stage,
            PackStatus = state.PackStatus,
            Status = GenericRunStatus.Active,
            Checkpoint = state.Checkpoint,
            ApprovalMode = mode.Value,
            Iterations = state.Iterations,
            StartedAt = now,
        };
        db.PipelineRuns.Add(run);
        foreach (var (artifact, content) in contents)
        {
            db.Artifacts.Add(RunInputs.Version(run, artifact.Key, 1, null, content, request.UserId, now));
        }

        var execution = await StageExecutionWriter.EnterAsync(db, run.Id, state.Stage, state.Checkpoint, PipelineTransitionKinds.Start, now, cancellationToken);
        EngineRunMoves.QueueDriver(jobs, run, execution.Id, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(run);
    }
}

/// <summary>FR-331, FR-335: input artifacts: their content checked against the artifact's schema, and new versions.</summary>
public static class RunInputs
{
    /// <summary>
    /// The stored content of an input: for a JSON artifact the value's JSON, checked against its schema (failures:
    /// <c>INPUT_INVALID</c>, each failing path in the details as <c>&lt;pointer&gt;: &lt;message&gt;</c>); for a Markdown
    /// artifact the value must be a string.
    /// </summary>
    public static Result<string> Content(ArtifactDefinition artifact, JsonElement value, string packId, ArtifactSchemas schemas)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(schemas);
        if (artifact.Format == ArtifactFormat.Markdown)
        {
            return value.ValueKind == JsonValueKind.String
                ? Result.Success(value.GetString()!)
                : Result.Failure<string>(Invalid(artifact, ["/: The input must be a string of Markdown."]));
        }

        var content = value.GetRawText();
        if (artifact.Schema is { } schema)
        {
            var findings = schemas.Validate(packId, schema, content);
            if (findings.Count > 0)
            {
                return Result.Failure<string>(Invalid(artifact, [.. findings.Select(f => $"{f.Path}: {f.Message}")]));
            }
        }

        return Result.Success(content);
    }

    /// <summary>
    /// FR-331: the run's approval mode: the requested one, else the project's, else the definition's default. A requested
    /// mode the definition doesn't list gives <c>null</c>; the project's mode falls back to the definition's default (or
    /// first) when the definition doesn't list it.
    /// </summary>
    public static ApprovalMode? ApprovalModeOf(PipelineDefinition definition, ApprovalMode? requested, ApprovalMode projectMode)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var offered = definition.ApprovalModes;
        if (requested is { } chosen)
        {
            return offered.Count == 0 || offered.Contains(chosen) ? chosen : null;
        }

        if (offered.Count == 0 || offered.Contains(projectMode))
        {
            return projectMode;
        }

        return definition.DefaultApprovalMode ?? offered[0];
    }

    /// <summary>A locked version of input <paramref name="key"/> owned by <paramref name="run"/> (no subject: the run alone).</summary>
    public static Artifact Version(PipelineRun run, string key, int version, int? parent, string content, Guid? userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new Artifact
        {
            ProjectId = run.ProjectId,
            Type = key,
            Version = version,
            ParentVersion = parent,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Locked,
            CheckpointAtCreation = run.Checkpoint,
            ReleaseVersionId = AgentStageScope.ReleaseVersionOf(run),
            PipelineRunId = run.Id,
            CreatedByUserId = userId,
            ApprovedBy = userId,
            ApprovedAt = now,
            CreatedAt = now,
        };
    }

    private static DomainError Invalid(ArtifactDefinition artifact, IReadOnlyList<string> details) =>
        new(EngineErrorCodes.InputInvalid, $"The {artifact.Title} input doesn't match its schema.") { Details = details };
}

/// <summary>FR-330, FR-333: the run-control errors.</summary>
public static class RunControlErrors
{
    public static DomainError ControlledByHost() =>
        new(EngineErrorCodes.RunControlledByHost, "This pipeline is run from its own pages, not from here.");

    public static DomainError NotActive() =>
        new(EngineErrorCodes.RunNotActive, "This run has already finished.");
}
