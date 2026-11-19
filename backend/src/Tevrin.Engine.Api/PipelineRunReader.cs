using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-300, FR-301: read models of pipeline runs, their stages, gates and artifacts. Callers check project permission
/// first (<see cref="IRunAccessPolicy"/>). Reads never track entities.
/// </summary>
public sealed class PipelineRunReader(
    IEngineDbContext db,
    PipelineDefinitionResolver definitions,
    RunLogQueries runLog,
    IPipelineTransitionSource transitions,
    IRunActivityPolicy activity,
    TimeProvider time)
{
    public Task<PipelineRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken) =>
        db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    /// <summary>The run whose project's state the live hub reports: the newest non-final run, else the newest run.</summary>
    public Task<PipelineRun?> CurrentRunAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.PipelineRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Status == GenericRunStatus.Active ? 0 : 1)
            .ThenByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ResolvedDefinition?> DefinitionAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
    }

    /// <summary>A project's runs, newest first.</summary>
    public async Task<IReadOnlyList<PipelineRunDto>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var result = new List<PipelineRunDto>(runs.Count);
        foreach (var run in runs)
        {
            result.Add(await ToDtoAsync(run, cancellationToken));
        }

        return result;
    }

    /// <summary>FR-300: a run with its derived generic status (§4.57) and its cost and token totals.</summary>
    public async Task<PipelineRunDto> ToDtoAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var definition = (await DefinitionAsync(run, cancellationToken))?.Definition;
        var final = PipelineRunRules.IsFinal(run.Status);
        var pendingGate = !final && await db.ApprovalGates.AsNoTracking()
            .AnyAsync(g => g.PipelineRunId == run.Id && g.Status == GateStatus.Pending, cancellationToken);
        var activeJob = !final && !pendingGate && await db.Jobs.AsNoTracking()
            .AnyAsync(RunActivity.ActiveJobsOf(run.ProjectId, activity.IgnoredJobTypes), cancellationToken);
        var totals = await runLog.RunTotalsAsync(run.Id, cancellationToken);
        return new PipelineRunDto(
            run.Id,
            run.ProjectId,
            run.PipelineId,
            definition?.Title ?? run.PipelineId,
            run.DefinitionHash,
            PipelineDtoMapper.ShortHash(run.DefinitionHash),
            run.CurrentStage,
            definition?.FindStage(run.CurrentStage)?.Title,
            run.PackStatus,
            RunViewRules.DeriveStatus(run.Status, pendingGate, activeJob),
            run.Checkpoint,
            run.StartedAt,
            run.CompletedAt,
            totals.Cost,
            totals.InputTokens,
            totals.OutputTokens,
            totals.Tokens,
            totals.AgentRuns);
    }

    /// <summary>FR-300: the run's stages in definition order (none when its definition can't be resolved).</summary>
    public async Task<IReadOnlyList<PipelineStageDto>> StagesAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if ((await DefinitionAsync(run, cancellationToken))?.Definition is not { } definition)
        {
            return [];
        }

        var executions = await db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == run.Id)
            .Select(e => new StageEntry(e.StageId, e.EnteredAt, e.ExitedAt))
            .ToListAsync(cancellationToken);
        var moves = await transitions.ListAsync(run.Id, TimelineWindow.All, cancellationToken);
        var fromTransitions = RunViewRules.EntriesFromTransitions(moves.Select(t => new TransitionEntry(t.FromStage, t.ToStage, t.Kind, t.At)));
        return [.. RunViewRules.Stages(definition, run.CurrentStage, PipelineRunRules.IsFinal(run.Status), executions, fromTransitions, time.GetUtcNow())
            .Select(s => new PipelineStageDto(run.Id, s.Id, s.Title, s.State, s.Iterations, s.EnteredAt, s.ExitedAt, s.DurationMs))];
    }

    /// <summary>FR-301: the run's gates, newest first.</summary>
    public async Task<IReadOnlyList<PipelineGateDto>> GatesAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var gates = await db.ApprovalGates.AsNoTracking()
            .Where(g => g.PipelineRunId == run.Id)
            .OrderByDescending(g => g.CreatedAt)
            .ThenByDescending(g => g.Id)
            .ToListAsync(cancellationToken);
        return await ToGateDtosAsync(gates, cancellationToken);
    }

    /// <summary>FR-301, FR-302: gate DTOs, with each gate's definition and gated artifact (three queries for any number).</summary>
    public async Task<IReadOnlyList<PipelineGateDto>> ToGateDtosAsync(IReadOnlyList<ApprovalGate> gates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var runIds = gates.Select(g => g.PipelineRunId).OfType<Guid>().Distinct().ToList();
        var hashes = await db.PipelineRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.DefinitionHash })
            .ToDictionaryAsync(r => r.Id, r => r.DefinitionHash, cancellationToken);
        var byHash = new Dictionary<string, PipelineDefinition?>(StringComparer.Ordinal);
        foreach (var hash in hashes.Values.Distinct(StringComparer.Ordinal))
        {
            byHash[hash] = (await definitions.ResolveAsync(hash, cancellationToken))?.Definition;
        }

        var artifactIds = gates.Select(g => g.ArtifactId).OfType<Guid>().Distinct().ToList();
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => artifactIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Type, a.Version })
            .ToDictionaryAsync(a => a.Id, cancellationToken);
        return [.. gates.Select(g =>
        {
            var runId = g.PipelineRunId ?? Guid.Empty;
            var definition = hashes.TryGetValue(runId, out var hash) ? byHash[hash] : null;
            var artifact = g.ArtifactId is { } id && artifacts.TryGetValue(id, out var a) ? a : null;
            return PipelineDtoMapper.ToGateDto(g, runId, definition, artifact?.Type, artifact?.Version);
        })];
    }

    /// <summary>FR-301: the run's artifacts by key (definition order, then any other key), versions newest first.</summary>
    public async Task<IReadOnlyList<PipelineArtifactDto>> ArtifactsAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var definition = (await DefinitionAsync(run, cancellationToken))?.Definition;
        var versions = await db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == run.Id)
            .Select(a => new { a.Id, a.Type, a.Version, a.Status, a.ContentHash, a.CreatedAt })
            .ToListAsync(cancellationToken);
        return [.. versions
            .GroupBy(a => a.Type, StringComparer.Ordinal)
            .OrderBy(g => definition?.Artifacts.ToList().FindIndex(d => d.Key == g.Key) is { } i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PipelineArtifactDto(
                g.Key,
                TitleOf(definition, g.Key),
                [.. g.OrderByDescending(a => a.Version).Select(a => new PipelineArtifactVersionSummaryDto(a.Id, a.Version, a.Status, a.ContentHash, a.CreatedAt))]))];
    }

    /// <summary>FR-301: one version of a run's artifact, or <c>null</c>.</summary>
    public async Task<PipelineArtifactVersionDto?> ArtifactVersionAsync(PipelineRun run, string key, int version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var artifact = await FindArtifactAsync(run.Id, key, version, cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        var definition = (await DefinitionAsync(run, cancellationToken))?.Definition;
        return new PipelineArtifactVersionDto(
            artifact.Id, artifact.Type, TitleOf(definition, artifact.Type), artifact.Version, artifact.ParentVersion, artifact.Status,
            artifact.ContentHash, ContentHash.Matches(artifact.Content, artifact.ContentHash), artifact.Content, artifact.AgentRunId,
            artifact.CreatedAt, artifact.ApprovedAt);
    }

    /// <summary>FR-301: the line diff between two versions of a run's artifact (<c>NOT_FOUND</c> when either is missing;
    /// <c>VALIDATION</c> when they are too large, <see cref="LineDiff.MaxTotalLines"/>).</summary>
    public async Task<Result<PipelineArtifactDiffDto>> DiffAsync(PipelineRun run, string key, int from, int to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var older = await FindArtifactAsync(run.Id, key, from, cancellationToken);
        var newer = from == to ? older : await FindArtifactAsync(run.Id, key, to, cancellationToken);
        if (older is null || newer is null)
        {
            var missing = older is null ? from : to;
            return Result.Failure<PipelineArtifactDiffDto>(new DomainError(EngineErrorCodes.NotFound, $"Version {missing} of {key} was not found in this run."));
        }

        var diff = LineDiff.Diff(older.Content, newer.Content, $"{key} v{from}", $"{key} v{to}");
        if (diff.IsFailure)
        {
            return Result.Failure<PipelineArtifactDiffDto>(diff.Error!);
        }

        return Result.Success(new PipelineArtifactDiffDto(
            key,
            from,
            to,
            diff.Value.Unified,
            [.. diff.Value.Lines.Select(l => new PipelineDiffLineDto(l.Kind, l.OldLine, l.NewLine, l.Text, l.Kind == DiffLineKind.Skipped ? l.Skipped : null))]));
    }

    private Task<Artifact?> FindArtifactAsync(Guid runId, string key, int version, CancellationToken cancellationToken) =>
        db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.PipelineRunId == runId && a.Type == key && a.Version == version, cancellationToken);

    private static string TitleOf(PipelineDefinition? definition, string key) =>
        definition?.FindArtifact(key)?.Title ?? KeyWords.Humanise(key);
}
