using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;

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
    PipelineRegistryHost registryHost,
    TimeProvider time)
{
    public Task<PipelineRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken) =>
        db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    /// <summary>The run whose project's state the live hub reports: the newest non-final run, else the newest run.</summary>
    public Task<PipelineRun?> CurrentRunAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.PipelineRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            // Non-final first (PipelineRunRules.IsFinal, written out so EF translates it): active and waiting_on_input (FR-335).
            .OrderBy(r => r.Status == GenericRunStatus.Completed || r.Status == GenericRunStatus.Failed || r.Status == GenericRunStatus.Cancelled ? 1 : 0)
            .ThenByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ResolvedDefinition?> DefinitionAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
    }

    /// <summary>
    /// §4.57, FR-332: whether a job makes the run <c>running</c>. A host-controlled run (the SDLC) counts its project's
    /// active jobs except the ignored types and the engine-run jobs (<see cref="EngineJobTypes.RunScoped"/>, which belong to
    /// other runs); an engine-controlled run counts only the queued or running <c>stage.run</c> jobs that name it.
    /// </summary>
    private async Task<bool> HasActiveJobAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        if (registryHost.Registry?.RunControlOf(run.PipelineId) == RunControl.Host)
        {
            return await db.Jobs.AsNoTracking()
                .AnyAsync(RunActivity.ActiveJobsOf(run.ProjectId, [.. activity.IgnoredJobTypes, .. EngineJobTypes.RunScoped]), cancellationToken);
        }

        var payloads = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == run.ProjectId && j.Type == EngineJobTypes.StageRun
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .Select(j => j.Payload)
            .ToListAsync(cancellationToken);
        return payloads.Any(p => StageRunPayload.Parse(p)?.RunId == run.Id);
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
        var activeJob = !final && !pendingGate && await HasActiveJobAsync(run, cancellationToken);
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
            totals.AgentRuns)
        {
            PinnedArtifacts = [.. run.PinnedArtifacts.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new PipelineRunPinDto(p.Key, p.Value.ArtifactId, p.Value.Version))],
            FollowUps = definition is null ? [] : await FollowUpsAsync(run, definition, cancellationToken),
        };
    }

    /// <summary>
    /// FR-354, FR-363: the run's follow-ups in declared order. A completed run scheduled each at its completion
    /// (<see cref="FollowUpRules.Schedule"/>, the due times the driver queued); before that they have no due time. A
    /// follow-up that has run has its stage execution (matched by stage and label).
    /// </summary>
    private async Task<IReadOnlyList<PipelineRunFollowUpDto>> FollowUpsAsync(PipelineRun run, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.FollowUps.Count == 0)
        {
            return [];
        }

        var due = run.Status == GenericRunStatus.Completed && run.CompletedAt is { } completedAt
            ? FollowUpRules.Schedule(definition, completedAt)
            : [.. from f in definition.FollowUps
                  from after in f.After
                  where FollowUpRules.ParseDuration(after) is not null
                  select new ScheduledFollowUp(f.Stage, FollowUpRules.Label(after), default)];
        var runId = run.Id;
        var ran = await db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId && e.FollowUp != null)
            .Select(e => new { e.Id, e.StageId, e.FollowUp, e.EnteredAt, e.ExitedAt, e.Outcome })
            .ToListAsync(cancellationToken);
        var scheduled = run.Status == GenericRunStatus.Completed && run.CompletedAt is not null;
        return [.. due.Select(f =>
        {
            var execution = ran.Where(e => e.StageId == f.Stage && e.FollowUp == f.Label).OrderByDescending(e => e.EnteredAt).FirstOrDefault();
            return new PipelineRunFollowUpDto(
                f.Stage, f.Label, scheduled ? f.DueAt : null, execution?.Id, execution?.EnteredAt, execution?.ExitedAt, execution?.Outcome);
        })];
    }

    /// <summary>FR-300: the run's stages in definition order (none when its definition can't be resolved).</summary>
    public async Task<IReadOnlyList<PipelineStageDto>> StagesAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if ((await DefinitionAsync(run, cancellationToken))?.Definition is not { } definition)
        {
            return [];
        }

        // FR-355: a map's item executions aren't entries into its stage.
        var executions = await db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == run.Id && e.ParentExecutionId == null)
            .Select(e => new StageEntry(e.StageId, e.EnteredAt, e.ExitedAt))
            .ToListAsync(cancellationToken);
        var moves = await transitions.ListAsync(run.Id, TimelineWindow.All, cancellationToken);
        var fromTransitions = RunViewRules.EntriesFromTransitions(moves.Select(t => new TransitionEntry(t.FromStage, t.ToStage, t.Kind, t.At)));
        var maps = await MapProgressAsync(run, definition, cancellationToken);
        var chosen = await ChosenVariantsAsync(run, definition, cancellationToken);
        return [.. RunViewRules.Stages(definition, run.CurrentStage, PipelineRunRules.IsFinal(run.Status), executions, fromTransitions, time.GetUtcNow())
            .Select(s => new PipelineStageDto(run.Id, s.Id, s.Title, s.State, s.Iterations, s.EnteredAt, s.ExitedAt, s.DurationMs)
            {
                Map = maps.GetValueOrDefault(s.Id),
                ChosenVariant = chosen.GetValueOrDefault(s.Id),
            })];
    }

    /// <summary>FR-355, FR-363: each map stage's progress over its newest entry's items (one grouped query).</summary>
    private async Task<Dictionary<string, PipelineMapProgressDto>> MapProgressAsync(PipelineRun run, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var mapStages = definition.Stages.Where(s => s.Map is not null).Select(s => s.Id).ToList();
        if (mapStages.Count == 0)
        {
            return [];
        }

        var runId = run.Id;
        var entries = await db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId && e.ParentExecutionId == null && mapStages.Contains(e.StageId))
            .Select(e => new { e.Id, e.StageId, e.EnteredAt })
            .ToListAsync(cancellationToken);
        var newest = entries.GroupBy(e => e.StageId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => e.EnteredAt).ThenByDescending(e => e.Id).First())
            .ToList();
        var ids = newest.Select(e => e.Id).ToList();
        var counts = await ItemCounts(db.StageExecutions.AsNoTracking().Where(e => e.ParentExecutionId != null && ids.Contains(e.ParentExecutionId.Value)))
            .ToDictionaryAsync(c => c.ExecutionId, cancellationToken);
        return newest.Where(e => counts.ContainsKey(e.Id)).ToDictionary(e => e.StageId, e => counts[e.Id], StringComparer.Ordinal);
    }

    /// <summary>
    /// FR-363 live: map execution <paramref name="executionId"/>'s progress (D-0.8.0-24), with its run, project and stage;
    /// <c>null</c> when it isn't a run's stage execution. A map with no items yet reports zero counts.
    /// </summary>
    public async Task<PipelineMapProgressMessage?> MapProgressMessageAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var map = await db.StageExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ParentExecutionId == null)
            .Join(db.PipelineRuns.AsNoTracking(), e => e.PipelineRunId, r => r.Id, (e, r) => new { e.StageId, e.PipelineRunId, r.ProjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (map is null)
        {
            return null;
        }

        var progress = await ItemCounts(db.StageExecutions.AsNoTracking().Where(e => e.ParentExecutionId == executionId))
            .FirstOrDefaultAsync(cancellationToken) ?? new PipelineMapProgressDto(executionId, 0, 0, 0);
        return new PipelineMapProgressMessage(map.ProjectId, map.PipelineRunId, map.StageId, progress);
    }

    /// <summary>D-0.8.0-24: map progress grouped from item rows (<see cref="MapRules.StatusOf"/>, written out so EF translates it).</summary>
    private static IQueryable<PipelineMapProgressDto> ItemCounts(IQueryable<StageExecution> items) =>
        items.GroupBy(e => e.ParentExecutionId!.Value)
            .Select(g => new PipelineMapProgressDto(
                g.Key,
                g.Count(),
                g.Count(e => e.ExitedAt != null && e.Outcome == BranchOutcomes.Completed),
                g.Count(e => e.ExitedAt != null && (e.Outcome == null || e.Outcome != BranchOutcomes.Completed))));

    /// <summary>
    /// FR-349, FR-363: the variant each variants stage promoted. The promoted plain version copies its variant's agent run
    /// (the run driver's promotion), so the newest plain version of the output with a variant sibling of that agent run names it.
    /// </summary>
    private async Task<Dictionary<string, string>> ChosenVariantsAsync(PipelineRun run, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var stages = definition.Stages.Where(s => s.Variants is not null && s.Output is not null).ToList();
        if (stages.Count == 0)
        {
            return [];
        }

        var runId = run.Id;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            // One bounded query per variants stage: the newest plain version that has a variant of its agent run, and
            // that variant's label (both indexed by run and type).
            var key = stage.Output!;
            var labels = VariantRules.Labels(stage).ToList();
            var variants = db.Artifacts.AsNoTracking()
                .Where(v => v.PipelineRunId == runId && v.Type == key && v.ItemIndex == null && v.Variant != null && labels.Contains(v.Variant!));
            var label = await db.Artifacts.AsNoTracking()
                .Where(p => p.PipelineRunId == runId && p.Type == key && p.Variant == null && p.ItemIndex == null && p.AgentRunId != null
                    && variants.Any(v => v.AgentRunId == p.AgentRunId))
                .OrderByDescending(p => p.Version)
                .Select(p => variants.Where(v => v.AgentRunId == p.AgentRunId).Select(v => v.Variant).FirstOrDefault())
                .FirstOrDefaultAsync(cancellationToken);
            if (label is not null)
            {
                result[stage.Id] = label;
            }
        }

        return result;
    }

    /// <summary>
    /// FR-355, FR-363: a page of map execution <paramref name="executionId"/>'s items after index <paramref name="after"/>
    /// (index order), optionally of one <paramref name="status"/>, each with its newest agent run (of the map's stage) and
    /// output version (of the stage's output key) within the item's own execution: from its entry to its exit, and before the
    /// stage's next entry. <c>null</c> when the run has no such map execution.
    /// </summary>
    public async Task<PipelineMapItemPageDto?> MapItemsAsync(
        PipelineRun run, Guid executionId, MapItemStatus? status, int? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runId = run.Id;
        var map = await db.StageExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.PipelineRunId == runId && e.ParentExecutionId == null)
            .Select(e => new { e.Id, e.StageId, e.EnteredAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (map is null)
        {
            return null;
        }

        // Each item's agent runs and outputs are read within its own execution: from its entry to its exit, and never past
        // the stage's next entry (a route, loop or rollback re-entering the map opens new items with the same indexes).
        var mapStageId = map.StageId;
        var mapEnteredAt = map.EnteredAt;
        var mapId = map.Id;
        var nextEntry = await db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId && e.ParentExecutionId == null && e.StageId == mapStageId && e.Id != mapId
                && e.EnteredAt >= mapEnteredAt)
            .OrderBy(e => e.EnteredAt)
            .Select(e => (DateTimeOffset?)e.EnteredAt)
            .FirstOrDefaultAsync(cancellationToken);
        var outputKey = (await DefinitionAsync(run, cancellationToken))?.Definition.FindStage(mapStageId)?.Output;

        var items = db.StageExecutions.AsNoTracking().Where(e => e.ParentExecutionId == executionId);
        var progress = await ItemCounts(items).FirstOrDefaultAsync(cancellationToken) ?? new PipelineMapProgressDto(executionId, 0, 0, 0);
        var filtered = status switch
        {
            MapItemStatus.Open => items.Where(e => e.ExitedAt == null),
            MapItemStatus.Completed => items.Where(e => e.ExitedAt != null && e.Outcome == BranchOutcomes.Completed),
            MapItemStatus.Failed => items.Where(e => e.ExitedAt != null && (e.Outcome == null || e.Outcome != BranchOutcomes.Completed)),
            _ => items,
        };
        if (after is { } last)
        {
            filtered = filtered.Where(e => e.ItemIndex > last);
        }

        var page = await filtered.OrderBy(e => e.ItemIndex).Take(limit + 1).ToListAsync(cancellationToken);
        var more = page.Count > limit;
        page = [.. page.Take(limit)];
        var indexes = page.Select(e => e.ItemIndex).OfType<int>().ToList();
        var since = page.Count == 0 ? DateTimeOffset.MaxValue : page.Min(e => e.EnteredAt);
        var until = page.Any(e => e.ExitedAt is null) ? nextEntry : page.Max(e => e.ExitedAt);
        if (nextEntry is { } next0 && (until is null || until > next0))
        {
            until = next0;
        }

        var agentRunRows = db.AgentRuns.AsNoTracking()
            .Where(r => r.PipelineRunId == runId && r.Stage == mapStageId && r.ItemIndex != null && indexes.Contains(r.ItemIndex.Value) && r.CreatedAt >= since);
        var outputRows = db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == outputKey && a.Variant == null && a.ItemIndex != null && indexes.Contains(a.ItemIndex.Value)
                && a.CreatedAt >= since);
        if (until is { } bound)
        {
            agentRunRows = agentRunRows.Where(r => r.CreatedAt <= bound);
            outputRows = outputRows.Where(a => a.CreatedAt <= bound);
        }

        var agentRuns = page.Count == 0
            ? []
            : await agentRunRows.Select(r => new { r.Id, r.ItemIndex, r.Status, r.Error, r.CreatedAt }).ToListAsync(cancellationToken);
        var outputs = outputKey is null || page.Count == 0
            ? []
            : await outputRows.Select(a => new { a.Id, a.Type, a.Version, a.ItemIndex, a.CreatedAt }).ToListAsync(cancellationToken);

        // Within the item's own window: its entry to its exit (or, while open, the stage's next entry).
        bool InWindow(StageExecution e, DateTimeOffset at) =>
            at >= e.EnteredAt
            && (e.ExitedAt is not { } exited || at <= exited)
            && (nextEntry is not { } reentered || at < reentered);
        var dtos = page.Select(e =>
        {
            var agentRun = agentRuns.Where(r => r.ItemIndex == e.ItemIndex && InWindow(e, r.CreatedAt))
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefault();
            var output = outputs.Where(a => a.ItemIndex == e.ItemIndex && InWindow(e, a.CreatedAt))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).FirstOrDefault();
            return new PipelineMapItemDto(
                e.Id, e.ItemIndex ?? 0, MapRules.StatusOf(e), e.EnteredAt, e.ExitedAt,
                agentRun?.Id, agentRun?.Status, agentRun?.Error, output?.Id, output?.Type, output?.Version);
        }).ToList();
        var next = more && dtos.Count > 0 ? dtos[^1].ItemIndex.ToString(CultureInfo.InvariantCulture) : null;
        return new PipelineMapItemPageDto(progress, dtos, next);
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

        // FR-364: a variant_select gate's variants are looked up with the gated artifacts, for their keys and versions.
        var artifactIds = gates.Select(g => g.ArtifactId).OfType<Guid>()
            .Concat(gates.SelectMany(g => PipelineGatePayloads.VariantArtifactIds(g.Type, g.Payload)))
            .Distinct()
            .ToList();
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => artifactIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Type, a.Version })
            .ToDictionaryAsync(a => a.Id, cancellationToken);
        (string Key, int Version)? ArtifactOf(Guid id) => artifacts.TryGetValue(id, out var a) ? (a.Type, a.Version) : null;
        return [.. gates.Select(g =>
        {
            var runId = g.PipelineRunId ?? Guid.Empty;
            var definition = hashes.TryGetValue(runId, out var hash) ? byHash[hash] : null;
            var artifact = g.ArtifactId is { } id && artifacts.TryGetValue(id, out var a) ? a : null;
            return PipelineDtoMapper.ToGateDto(g, runId, definition, artifact?.Type, artifact?.Version) with
            {
                Payload = PipelineGatePayloads.Read(g.Type, g.Payload, ArtifactOf),
            };
        })];
    }

    /// <summary>FR-301: the run's artifacts by key (definition order, then any other key), versions newest first.</summary>
    public async Task<IReadOnlyList<PipelineArtifactDto>> ArtifactsAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var definition = (await DefinitionAsync(run, cancellationToken))?.Definition;
        var versions = await db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == run.Id)
            .Select(a => new { a.Id, a.Type, a.Version, a.Status, a.ContentHash, a.CreatedAt, a.Variant, a.ItemIndex })
            .ToListAsync(cancellationToken);
        return [.. versions
            .GroupBy(a => a.Type, StringComparer.Ordinal)
            .OrderBy(g => definition?.Artifacts.ToList().FindIndex(d => d.Key == g.Key) is { } i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PipelineArtifactDto(
                g.Key,
                TitleOf(definition, g.Key),
                [.. g.OrderByDescending(a => a.Version).Select(a => new PipelineArtifactVersionSummaryDto(a.Id, a.Version, a.Status, a.ContentHash, a.CreatedAt)
                {
                    Variant = a.Variant,
                    ItemIndex = a.ItemIndex,
                })]))];
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
