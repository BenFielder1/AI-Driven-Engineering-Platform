using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Outcomes;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Api;

/// <summary>FR-353: what an outcome score is about: <c>{ agentRunId }</c> or <c>{ artifactKey, version, variant? }</c>.</summary>
public sealed record OutcomeTargetDto
{
    /// <summary>An agent run of the run.</summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>An artifact key of the run (with <see cref="Version"/>).</summary>
    [MaxLength(OutcomeScoring.ArtifactKeyMaxLength)]
    public string? ArtifactKey { get; init; }

    /// <summary>The artifact version (with <see cref="ArtifactKey"/>).</summary>
    public int? Version { get; init; }

    /// <summary>The variant label (FR-349) of the artifact version; the plain version when omitted.</summary>
    [MaxLength(OutcomeScoring.VariantMaxLength)]
    public string? Variant { get; init; }
}

/// <summary>FR-353: an external outcome score for a run.</summary>
public sealed record CreateOutcomeScoreRequest
{
    [Required]
    public OutcomeTargetDto? Target { get; init; }

    [Required]
    [MaxLength(OutcomeScoring.MetricMaxLength)]
    public string? Metric { get; init; }

    [Required]
    public OutcomeScoreKind? Kind { get; init; }

    /// <summary>The observed value; for <c>probability_brier</c> the outcome, 0 or 1.</summary>
    [Required]
    public double? Value { get; init; }

    /// <summary>The predicted probability in [0, 1] (<c>probability_brier</c> only).</summary>
    public double? Prediction { get; init; }

    [Required]
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Who or what reported the outcome (e.g. a system name).</summary>
    [Required]
    [MaxLength(OutcomeScoring.SourceMaxLength)]
    public string? Source { get; init; }
}

/// <summary>FR-353: a stored outcome score. <c>score</c> is the value for <c>number</c>, the Brier score for <c>probability_brier</c>.</summary>
public sealed record OutcomeScoreDto(
    Guid Id,
    Guid PipelineRunId,
    OutcomeTargetDto Target,
    string Metric,
    OutcomeScoreKind Kind,
    double Value,
    double? Prediction,
    double Score,
    DateTimeOffset ObservedAt,
    string Source,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>FR-353: an agent key and prompt version's mean outcome score on one metric, over its scored targets.</summary>
public sealed record PromptOutcomeDto(string Metric, double MeanScore, int Count);

/// <summary>FR-353: stores and lists a run's outcome scores. Targets resolve within the run only (404 otherwise).</summary>
public sealed class PipelineRunOutcomes(IEngineDbContext db, TimeProvider time)
{
    private static DomainError TargetNotFound() => new(EngineErrorCodes.NotFound, "The score target was not found in this run.") { Field = "target" };

    /// <summary>Validates, resolves the target in <paramref name="run"/> and stores the score.</summary>
    public async Task<Result<OutcomeScoreDto>> CreateAsync(PipelineRun run, CreateOutcomeScoreRequest request, Guid? userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target is { } t ? new OutcomeTarget(t.AgentRunId, t.ArtifactKey, t.Version, t.Variant) : null;
        var valid = OutcomeScoring.Validate(new OutcomeScoreInput(
            target, request.Metric, request.Kind, request.Value, request.Prediction, request.ObservedAt, request.Source));
        if (valid.IsFailure)
        {
            return Result.Failure<OutcomeScoreDto>(valid.Error!);
        }

        var score = valid.Value;
        Guid? agentRunId = null;
        Artifact? artifact = null;
        if (score.Target.AgentRunId is { } id)
        {
            if (!await db.AgentRuns.AsNoTracking().AnyAsync(r => r.Id == id && r.PipelineRunId == run.Id, cancellationToken))
            {
                return Result.Failure<OutcomeScoreDto>(TargetNotFound());
            }

            agentRunId = id;
        }
        else
        {
            artifact = await FindArtifactAsync(run.Id, score.Target, cancellationToken);
            if (artifact is null)
            {
                return Result.Failure<OutcomeScoreDto>(TargetNotFound());
            }
        }

        var row = new OutcomeScore
        {
            PipelineRunId = run.Id,
            AgentRunId = agentRunId,
            ArtifactId = artifact?.Id,
            Metric = score.Metric,
            Kind = score.Kind,
            Value = score.Value,
            Prediction = score.Prediction,
            Score = score.Score,
            ObservedAt = score.ObservedAt.ToUniversalTime(),
            Source = score.Source,
            CreatedBy = userId,
            CreatedAt = time.GetUtcNow(),
        };
        db.OutcomeScores.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(row, artifact));
    }

    /// <summary>The run's scores, oldest first, optionally only those about one agent run or one artifact (version, variant).</summary>
    public async Task<Result<IReadOnlyList<OutcomeScoreDto>>> ListAsync(
        PipelineRun run, Guid? agentRunId, string? artifactKey, int? version, string? variant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (agentRunId is not null && (artifactKey ?? (object?)version ?? variant) is not null)
        {
            return Result.Failure<IReadOnlyList<OutcomeScoreDto>>(PipelineRequests.Invalid("agentRunId", "Filter by agentRunId or by artifactKey (and version, variant), not both."));
        }

        if (artifactKey is null && (version is not null || variant is not null))
        {
            return Result.Failure<IReadOnlyList<OutcomeScoreDto>>(PipelineRequests.Invalid("artifactKey", "version and variant filter only with artifactKey."));
        }

        var query = db.OutcomeScores.AsNoTracking().Where(s => s.PipelineRunId == run.Id);
        if (agentRunId is { } id)
        {
            query = query.Where(s => s.AgentRunId == id);
        }
        else if (artifactKey is not null)
        {
            var artifacts = db.Artifacts.AsNoTracking().Where(a => a.PipelineRunId == run.Id && a.Type == artifactKey);
            if (version is { } v)
            {
                artifacts = artifacts.Where(a => a.Version == v);
            }

            if (variant is not null)
            {
                artifacts = artifacts.Where(a => a.Variant == variant);
            }

            var ids = artifacts.Select(a => (Guid?)a.Id);
            query = query.Where(s => ids.Contains(s.ArtifactId));
        }

        var rows = await query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(cancellationToken);
        var artifactIds = rows.Where(s => s.ArtifactId != null).Select(s => s.ArtifactId!.Value).Distinct().ToList();
        var byId = artifactIds.Count == 0
            ? new Dictionary<Guid, Artifact>()
            : await db.Artifacts.AsNoTracking().Where(a => artifactIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        return Result.Success<IReadOnlyList<OutcomeScoreDto>>([.. rows.Select(s =>
            ToDto(s, s.ArtifactId is { } a && byId.TryGetValue(a, out var artifact) ? artifact : null))]);
    }

    private Task<Artifact?> FindArtifactAsync(Guid runId, OutcomeTarget target, CancellationToken cancellationToken)
    {
        var query = db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == target.ArtifactKey && a.Version == target.Version);
        query = target.Variant is null ? query.Where(a => a.Variant == null) : query.Where(a => a.Variant == target.Variant);
        return query.FirstOrDefaultAsync(cancellationToken);
    }

    private static OutcomeScoreDto ToDto(OutcomeScore s, Artifact? artifact) => new(
        s.Id,
        s.PipelineRunId,
        artifact is null
            ? new OutcomeTargetDto { AgentRunId = s.AgentRunId }
            : new OutcomeTargetDto { ArtifactKey = artifact.Type, Version = artifact.Version, Variant = artifact.Variant },
        s.Metric,
        s.Kind,
        s.Value,
        s.Prediction,
        s.Score,
        s.ObservedAt,
        s.Source,
        s.CreatedBy,
        s.CreatedAt);
}
