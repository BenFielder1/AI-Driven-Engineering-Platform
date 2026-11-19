using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Infrastructure.Persistence;

/// <summary>The SDLC pipeline definition a new run is pinned to: the registry's current, valid <c>sdlc</c> definition.</summary>
public static class SdlcRunDefinition
{
    /// <summary>The current SDLC definition; throws when the registry couldn't load a valid one (readiness is then
    /// unhealthy, FR-290, and no run may be written without a definition to pin it to).</summary>
    public static LoadedPipelineDefinition Current(PipelineRegistryHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.GetRegistry().TryGet(SdlcPack.PipelineId, out var loaded)
            ? loaded
            : throw new InvalidOperationException($"The '{SdlcPack.PipelineId}' pipeline definition is not registered or not valid; pipeline runs can't be written.");
    }
}

/// <summary>
/// FR-294, D-0.7.0-28: creates the SDLC pipeline runs that 0.6.1 data lacks and links existing rows to them. Idempotent and
/// safe on several replicas at once: runs are inserted with <c>ON CONFLICT DO NOTHING</c> (unique per pipeline and release
/// version, and at most one open run per project), and links only fill <c>pipeline_run_id IS NULL</c>.
/// <list type="bullet">
/// <item>The project's current release version gets a run with the project's phase, status, checkpoint, approval mode and
/// review cycle (<c>iterations.review</c>); it is <c>completed</c> when the status maps to a final generic status.</item>
/// <item>Earlier versions (not <c>imported</c> baselines, which never ran in Tevrin) get a <c>completed</c> run at
/// <c>release</c>/<c>released</c>, at the last checkpoint their transitions reached.</item>
/// <item>Every run is pinned to the current SDLC definition, stored first (FR-277).</item>
/// <item>The start-up pass also catches up after a 0.6.1 build ran against the data (a deploy rollback and redeploy): open
/// runs of versions that are no longer current are closed (<see cref="CloseSupersededAsync"/>), current runs are brought
/// back in line with their project, and every unlinked row of every version is linked.</item>
/// </list>
/// </summary>
public static class PipelineRunBackfill
{
    /// <summary>What a backfill pass did.</summary>
    public sealed record Outcome(int RunsCreated, int RunsUpdated, int RowsLinked);

    /// <summary>Stores the definition, closes superseded runs, creates the missing runs, brings current runs in line with
    /// their project and links existing transitions, gates, agent runs and artifacts (all projects, or only
    /// <paramref name="projectId"/>).</summary>
    public static async Task<Outcome> RunAsync(
        AideDbContext db, LoadedPipelineDefinition sdlc, Guid? projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sdlc);
        var closed = await CloseSupersededAsync(db, sdlc, projectId, null, now, cancellationToken);
        var runs = await CreateRunsAsync(db, sdlc, projectId, now, cancellationToken);
        var synced = await SyncCurrentAsync(db, sdlc, projectId, now, cancellationToken);

        // FR-297: executions left open by runs that ended (superseded versions, or runs a 0.6.1 build finished).
        await StageExecutionWriter.CloseEndedAsync(db, projectId, now, cancellationToken);
        var linked = await LinkAsync(db, sdlc.Definition!.Pipeline, projectId, cancellationToken);
        return new Outcome(runs, closed + synced, linked);
    }

    /// <summary>
    /// D-0.7.0-10: ends every open run of a version other than <paramref name="keepVersionId"/> (or, when null, the
    /// project's stored current version) <c>completed</c>, with the final state of a released version: stage
    /// <c>release</c>, pack status <c>released</c>, the last checkpoint its transitions reached. A version is only superseded
    /// once it was released (FR-60), so this is that version's final state whoever wrote the project meanwhile.
    /// </summary>
    public static Task<int> CloseSupersededAsync(
        AideDbContext db, LoadedPipelineDefinition sdlc, Guid? projectId, Guid? keepVersionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sdlc);
        var definition = sdlc.Definition!;
        var pipeline = definition.Pipeline;
        var all = projectId is null;
        var only = projectId ?? Guid.Empty;
        var useKeep = keepVersionId is not null;
        var keep = keepVersionId ?? Guid.Empty;
        var releaseStage = Phase.Release.ToWire();
        var releasedStatus = ProjectStatus.Released.ToWire();
        var releasedGeneric = PipelineRunRules.StoredStatus(definition, releasedStatus).ToWire();
        return db.Database.ExecuteSqlAsync(
            $"""
            UPDATE pipeline_runs r
            SET status = {releasedGeneric}, completed_at = COALESCE(r.completed_at, v.released_at, {now}),
                current_stage = {releaseStage}, pack_status = {releasedStatus},
                checkpoint = COALESCE((SELECT max(t.checkpoint) FROM state_transitions t WHERE t.release_version_id = r.subject_id), r.checkpoint)
            FROM projects p, release_versions v
            WHERE r.project_id = p.id AND v.id = r.subject_id AND r.pipeline_id = {pipeline}
              AND r.status NOT IN ('completed', 'failed', 'cancelled')
              AND r.subject_id <> (CASE WHEN {useKeep} THEN {keep} ELSE COALESCE(p.current_release_version_id, r.subject_id) END)
              AND ({all} OR p.id = {only})
            """,
            cancellationToken);
    }

    /// <summary>Brings the run of each project's current version in line with the project where they differ (rows a 0.6.1
    /// build changed). Runs aren't held across saves by the dual write, so this can't cause a false conflict.</summary>
    public static Task<int> SyncCurrentAsync(
        AideDbContext db, LoadedPipelineDefinition sdlc, Guid? projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sdlc);
        var definition = sdlc.Definition!;
        var pipeline = definition.Pipeline;
        var all = projectId is null;
        var only = projectId ?? Guid.Empty;
        var finals = FinalStatuses(definition);
        var active = GenericRunStatus.Active.ToWire();
        var reviewStage = Phase.Review.ToWire();
        return db.Database.ExecuteSqlAsync(
            $"""
            UPDATE pipeline_runs r
            SET current_stage = p.current_phase, pack_status = p.status, checkpoint = p.checkpoint, approval_mode = p.approval_mode,
                status = COALESCE(CAST({finals} AS jsonb) ->> p.status, {active}),
                completed_at = CASE WHEN CAST({finals} AS jsonb) ->> p.status IS NULL THEN NULL ELSE COALESCE(r.completed_at, {now}) END,
                iterations = r.iterations || jsonb_build_object(CAST({reviewStage} AS text), p.review_cycle)
            FROM projects p
            WHERE r.project_id = p.id AND r.pipeline_id = {pipeline} AND r.subject_id = p.current_release_version_id
              AND ({all} OR p.id = {only})
              AND (r.current_stage, r.pack_status, r.checkpoint, r.approval_mode, r.iterations -> CAST({reviewStage} AS text))
                  IS DISTINCT FROM (p.current_phase, p.status, p.checkpoint, p.approval_mode, to_jsonb(p.review_cycle))
            """,
            cancellationToken);
    }

    /// <summary>Stores the definition and creates the missing runs, without linking rows (the lazy path, which must not
    /// touch rows its unit of work may be tracking with an <c>xmin</c> token).</summary>
    public static async Task<int> CreateRunsAsync(
        AideDbContext db, LoadedPipelineDefinition sdlc, Guid? projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sdlc);
        await StoredDefinitions.StoreAsync(db, sdlc, now, cancellationToken);

        var definition = sdlc.Definition!;
        var pipeline = definition.Pipeline;
        var hash = sdlc.Hash!;
        var all = projectId is null;
        var only = projectId ?? Guid.Empty;
        var releaseStage = Phase.Release.ToWire();
        var releasedStatus = ProjectStatus.Released.ToWire();
        var releasedGeneric = PipelineRunRules.StoredStatus(definition, releasedStatus).ToWire();
        var reviewStage = Phase.Review.ToWire();
        var finals = FinalStatuses(definition);
        var active = GenericRunStatus.Active.ToWire();

        return await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO pipeline_runs (id, project_id, pipeline_id, definition_hash, subject_id, current_stage, pack_status, status,
                                       checkpoint, approval_mode, iterations, started_at, completed_at)
            SELECT uuidv7(), s.project_id, {pipeline}, {hash}, s.version_id,
                   CASE WHEN s.is_current THEN s.current_phase ELSE {releaseStage} END,
                   CASE WHEN s.is_current THEN s.status ELSE {releasedStatus} END,
                   CASE WHEN s.is_current THEN COALESCE(CAST({finals} AS jsonb) ->> s.status, {active}) ELSE {releasedGeneric} END,
                   CASE WHEN s.is_current THEN s.checkpoint ELSE s.last_checkpoint END,
                   s.approval_mode,
                   CASE WHEN s.is_current THEN jsonb_build_object(CAST({reviewStage} AS text), s.review_cycle) ELSE jsonb_build_object() END,
                   s.version_created_at,
                   CASE WHEN s.is_current AND CAST({finals} AS jsonb) ->> s.status IS NULL THEN NULL ELSE COALESCE(s.released_at, {now}) END
            FROM (
                SELECT p.id AS project_id, p.current_phase, p.status, p.checkpoint, p.approval_mode, p.review_cycle,
                       v.id AS version_id, v.status AS version_status, v.created_at AS version_created_at, v.released_at,
                       v.id = COALESCE(p.current_release_version_id, latest.id) AS is_current,
                       COALESCE((SELECT max(t.checkpoint) FROM state_transitions t WHERE t.release_version_id = v.id), 1) AS last_checkpoint
                FROM projects p
                JOIN release_versions v ON v.project_id = p.id
                CROSS JOIN LATERAL (
                    SELECT lv.id FROM release_versions lv WHERE lv.project_id = p.id ORDER BY lv.created_at DESC, lv.id DESC LIMIT 1
                ) latest
                WHERE ({all} OR p.id = {only})
                  AND NOT EXISTS (SELECT 1 FROM pipeline_runs r WHERE r.pipeline_id = {pipeline} AND r.subject_id = v.id)
            ) s
            WHERE s.is_current OR s.version_status <> 'imported'
            ORDER BY s.project_id, s.version_created_at, s.version_id
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);
    }

    /// <summary>Links rows of each release version to that version's run where they have none yet.</summary>
    public static async Task<int> LinkAsync(AideDbContext db, string pipeline, Guid? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var all = projectId is null;
        var only = projectId ?? Guid.Empty;
        var linked = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE state_transitions x SET pipeline_run_id = r.id FROM pipeline_runs r
            WHERE x.pipeline_run_id IS NULL AND r.pipeline_id = {pipeline} AND r.subject_id = x.release_version_id
              AND r.project_id = x.project_id AND ({all} OR x.project_id = {only})
            """,
            cancellationToken);
        linked += await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE approval_gates x SET pipeline_run_id = r.id FROM pipeline_runs r
            WHERE x.pipeline_run_id IS NULL AND r.pipeline_id = {pipeline} AND r.subject_id = x.release_version_id
              AND r.project_id = x.project_id AND ({all} OR x.project_id = {only})
            """,
            cancellationToken);
        linked += await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE agent_runs x SET pipeline_run_id = r.id FROM pipeline_runs r
            WHERE x.pipeline_run_id IS NULL AND r.pipeline_id = {pipeline} AND r.subject_id = x.release_version_id
              AND r.project_id = x.project_id AND ({all} OR x.project_id = {only})
            """,
            cancellationToken);
        linked += await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE artifacts x SET pipeline_run_id = r.id FROM pipeline_runs r
            WHERE x.pipeline_run_id IS NULL AND r.pipeline_id = {pipeline} AND r.subject_id = x.release_version_id
              AND r.project_id = x.project_id AND ({all} OR x.project_id = {only})
            """,
            cancellationToken);
        return linked;
    }

    /// <summary>Pack statuses (and generic names) that map to a final generic status, as a JSON object
    /// <c>{ status: generic }</c> for the SQL above.</summary>
    private static string FinalStatuses(PipelineDefinition definition)
    {
        var finals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in definition.Statuses.Select(s => s.Name).Concat(EnumStrings.Values<GenericRunStatus>()))
        {
            if (definition.GenericStatusOf(name) is { } generic && PipelineRunRules.IsFinal(generic))
            {
                finals[name] = generic.ToWire();
            }
        }

        return JsonSerializer.Serialize(finals);
    }
}

/// <summary>
/// FR-292, FR-294 (D-0.7.0-8, D-0.7.0-28), as part of the <c>SaveChanges</c> of the change it follows:
/// <list type="bullet">
/// <item>Every new or changed project copies its phase, status, checkpoint, approval mode and review cycle onto the run of its
/// current release version; that run update is in the same transaction as the project's. A version without a run (a new
/// project, a new version) starts one, pinned to the current SDLC definition; any other open run of the project ends
/// <c>completed</c>. A project that predates the runs gets its missing runs from the backfill first.</item>
/// <item>New transitions, gates, agent runs and artifacts get the run of their release version.</item>
/// </list>
/// Not all of it is in that transaction: storing the definition record, closing superseded runs and the lazy backfill run as
/// their own statements before EF opens its transaction (inside it only when the caller holds one). They are idempotent and
/// describe facts that hold whether or not the save then succeeds (a content-addressed definition, a released version).
/// <para>
/// Runs are never reused from an earlier save in the same scope: the run to update is loaded fresh (a stale tracked copy is
/// detached first) and links read run ids without tracking, so the dual write never adds a concurrency failure of its own.
/// </para>
/// Since FR-292 the engine decides every lifecycle move from the run (<c>SdlcLifecycle</c> in the API); the writers record
/// the result on the project's columns (the 0.6.1 copy), and this copies them onto the run in the same transaction, so the
/// run follows them wherever they change; nothing here decides anything. Runs after <see cref="ReleaseVersionStampingInterceptor"/>,
/// which fills the release versions this reads. Disabled when the host has no pipeline registry (design-time tools).
/// Synchronous <c>SaveChanges</c> isn't supported for saves that touch projects or linked rows (no caller uses one).
/// </summary>
public sealed class PipelineRunSyncInterceptor(PipelineRegistryHost? registry, TimeProvider time) : SaveChangesInterceptor
{
    private const string RunIdProperty = "PipelineRunId";

    private static readonly Type[] Linked = [typeof(StateTransition), typeof(ApprovalGate), typeof(AgentRun), typeof(Artifact)];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (registry is not null && eventData.Context is AideDbContext db && HasWork(Work(db)))
        {
            throw new NotSupportedException(
                "Saves that change projects, transitions, gates, agent runs or artifacts must use SaveChangesAsync (pipeline runs, FR-292).");
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (registry is not null && eventData.Context is AideDbContext db)
        {
            await SyncAsync(db, registry, cancellationToken);
        }

        return result;
    }

    private static bool HasWork((List<EntityEntry<Project>> Projects, List<EntityEntry> Unlinked) work) =>
        work.Projects.Count > 0 || work.Unlinked.Count > 0;

    private static (List<EntityEntry<Project>> Projects, List<EntityEntry> Unlinked) Work(AideDbContext db) =>
        ([.. db.ChangeTracker.Entries<Project>().Where(e => e.State is EntityState.Added or EntityState.Modified)],
         [.. db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && Linked.Contains(e.Metadata.ClrType) && e.Property(RunIdProperty).CurrentValue is null)]);

    private async Task SyncAsync(AideDbContext db, PipelineRegistryHost host, CancellationToken cancellationToken)
    {
        var work = Work(db);
        if (!HasWork(work))
        {
            return;
        }

        var session = new Session(db, host, SdlcRunDefinition.Current(host), time.GetUtcNow(), cancellationToken);
        var addedProjects = work.Projects.Where(e => e.State == EntityState.Added).Select(e => e.Entity.Id).ToHashSet();
        foreach (var entry in work.Projects)
        {
            await session.SyncAsync(entry);
        }

        foreach (var entry in work.Unlinked)
        {
            var projectId = (Guid)entry.Property("ProjectId").CurrentValue!;
            if (entry.Property("ReleaseVersionId").CurrentValue is Guid versionId && versionId != Guid.Empty
                && await session.RunIdAsync(projectId, versionId, addedProjects.Contains(projectId)) is { } runId)
            {
                entry.Property(RunIdProperty).CurrentValue = runId;
            }
        }

        await session.RecordStageExecutionsAsync(
            [.. db.ChangeTracker.Entries<StateTransition>().Where(e => e.State == EntityState.Added).Select(e => e.Entity)]);
    }

    /// <summary>One <c>SaveChanges</c>: the definition, the clock, the runs loaded or started in it (by release version), run
    /// ids read for links, and which projects were already backfilled.</summary>
    private sealed class Session(
        AideDbContext db, PipelineRegistryHost registry, LoadedPipelineDefinition sdlc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Only runs added in this save; runs tracked by earlier saves in the scope may be stale.
        private readonly Dictionary<Guid, PipelineRun> _runs = db.ChangeTracker.Entries<PipelineRun>()
            .Where(e => e.State == EntityState.Added && e.Entity.PipelineId == sdlc.Definition!.Pipeline && e.Entity.SubjectId is not null)
            .Select(e => e.Entity)
            .ToDictionary(r => r.SubjectId!.Value);

        private readonly Dictionary<Guid, Guid> _runIds = [];
        private readonly HashSet<Guid> _backfilled = [];
        private bool _definitionStored;

        private string Pipeline => sdlc.Definition!.Pipeline;

        /// <summary>
        /// FR-297, D-0.7.0-32: stage executions in this save, after the links are set. Each new transition that enters a stage
        /// (<see cref="StageExecutionRules.IsEntry"/>) closes its run's open execution with the transition's kind and opens
        /// one for the stage it enters, at the transition's checkpoint and time (in the order the transitions were added). A
        /// run that ended in this save closes its open execution with its final status.
        /// </summary>
        public async Task RecordStageExecutionsAsync(IReadOnlyList<StateTransition> transitions)
        {
            foreach (var transition in transitions.Where(t => t.PipelineRunId is not null).OrderBy(t => t.CreatedAt))
            {
                var from = transition.FromPhase?.ToWire();
                var to = transition.ToPhase.ToWire();
                if (StageExecutionRules.IsEntry(from, to, transition.Kind))
                {
                    await StageExecutionWriter.EnterAsync(
                        db,
                        transition.PipelineRunId!.Value,
                        to,
                        transition.Checkpoint,
                        StageExecutionRules.LeavingOutcome(from, transition.Kind),
                        transition.CreatedAt,
                        cancellationToken);
                }
            }

            foreach (var run in _runs.Values.Where(r => PipelineRunRules.IsFinal(r.Status)).ToList())
            {
                var entry = db.Entry(run);
                if (entry.State == EntityState.Added || entry.Property(r => r.Status).IsModified)
                {
                    await StageExecutionWriter.EndAsync(db, run.Id, run.Status, run.CompletedAt ?? now, cancellationToken);
                }
            }
        }

        public async Task SyncAsync(EntityEntry<Project> entry)
        {
            var project = entry.Entity;
            var added = entry.State == EntityState.Added;
            var versionId = project.CurrentReleaseVersionId
                ?? (added ? null : await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken));
            if (versionId is not { } subject)
            {
                return;
            }

            var run = await TrackedRunAsync(project.Id, subject, added) ?? await StartAsync(project.Id, subject, added);
            var definition = registry.GetRegistry().GetByHash(run.DefinitionHash)?.Definition ?? sdlc.Definition!;
            Copy(project, run, definition, now);
        }

        /// <summary>The run id of <paramref name="versionId"/> for a link, read without tracking.</summary>
        public async Task<Guid?> RunIdAsync(Guid projectId, Guid versionId, bool projectAdded)
        {
            if (_runs.TryGetValue(versionId, out var known))
            {
                return known.Id;
            }

            if (_runIds.TryGetValue(versionId, out var id))
            {
                return id;
            }

            if (projectAdded)
            {
                return null;
            }

            var found = await RunIdQueryAsync(versionId);
            if (found is null && await BackfillAsync(projectId))
            {
                found = await RunIdQueryAsync(versionId);
            }

            if (found is { } runId)
            {
                _runIds[versionId] = runId;
            }

            return found;
        }

        /// <summary>The run of <paramref name="versionId"/>, loaded fresh and tracked for this save (a stale tracked copy is
        /// detached first), or created by the backfill for a project that predates runs. A new project's versions have no
        /// stored run.</summary>
        private async Task<PipelineRun?> TrackedRunAsync(Guid projectId, Guid versionId, bool projectAdded)
        {
            if (_runs.TryGetValue(versionId, out var known))
            {
                return known;
            }

            if (projectAdded)
            {
                return null;
            }

            var run = await LoadFreshAsync(versionId);
            if (run is null && await BackfillAsync(projectId))
            {
                run = await LoadFreshAsync(versionId);
            }

            if (run is not null)
            {
                _runs[versionId] = run;
            }

            return run;
        }

        /// <summary>The lazy backfill, once per project and save: closes superseded open runs and creates missing ones. It
        /// never links rows: a raw update would change the <c>xmin</c> of gates and artifacts this unit of work may hold.</summary>
        private async Task<bool> BackfillAsync(Guid projectId)
        {
            if (!_backfilled.Add(projectId))
            {
                return false;
            }

            await StoreDefinitionAsync();
            DetachTracked(r => r.ProjectId == projectId);
            await PipelineRunBackfill.CloseSupersededAsync(db, sdlc, projectId, null, now, cancellationToken);
            await StageExecutionWriter.CloseEndedAsync(db, projectId, now, cancellationToken);
            await PipelineRunBackfill.CreateRunsAsync(db, sdlc, projectId, now, cancellationToken);
            return true;
        }

        private async Task<PipelineRun?> LoadFreshAsync(Guid versionId)
        {
            DetachTracked(r => r.SubjectId == versionId);
            var pipeline = Pipeline;
            return await db.PipelineRuns.FirstOrDefaultAsync(r => r.PipelineId == pipeline && r.SubjectId == versionId, cancellationToken);
        }

        private Task<Guid?> RunIdQueryAsync(Guid versionId)
        {
            var pipeline = Pipeline;
            return db.PipelineRuns.AsNoTracking()
                .Where(r => r.PipelineId == pipeline && r.SubjectId == versionId)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <summary>Detaches tracked runs of this pipeline that match <paramref name="match"/>, except those added or loaded in
        /// this save (they are current).</summary>
        private void DetachTracked(Func<PipelineRun, bool> match)
        {
            var current = _runs.Values.ToHashSet();
            foreach (var stale in db.ChangeTracker.Entries<PipelineRun>()
                         .Where(e => e.State != EntityState.Added && e.Entity.PipelineId == Pipeline && !current.Contains(e.Entity) && match(e.Entity))
                         .ToList())
            {
                stale.State = EntityState.Detached;
            }
        }

        private async Task StoreDefinitionAsync()
        {
            if (!_definitionStored)
            {
                await StoredDefinitions.StoreAsync(db, sdlc, now, cancellationToken);
                _definitionStored = true;
            }
        }

        /// <summary>D-0.7.0-10: a new run for a release version (a new project or a new version). The project's other open
        /// runs first end <c>completed</c> with their version's final state (<see cref="PipelineRunBackfill.CloseSupersededAsync"/>,
        /// its own statement, so the partial unique index never sees two open runs).</summary>
        private async Task<PipelineRun> StartAsync(Guid projectId, Guid versionId, bool projectAdded)
        {
            await StoreDefinitionAsync();
            if (!projectAdded)
            {
                DetachTracked(r => r.ProjectId == projectId && r.SubjectId != versionId);
                await PipelineRunBackfill.CloseSupersededAsync(db, sdlc, projectId, versionId, now, cancellationToken);
                await StageExecutionWriter.CloseEndedAsync(db, projectId, now, cancellationToken);
            }

            var run = new PipelineRun
            {
                ProjectId = projectId,
                PipelineId = Pipeline,
                DefinitionHash = sdlc.Hash!,
                SubjectId = versionId,
                StartedAt = now,
            };
            db.PipelineRuns.Add(run);
            _runs[versionId] = run;
            return run;
        }
    }

    /// <summary>The project's lifecycle state onto its run (only changed values are written).</summary>
    public static void Copy(Project project, PipelineRun run, PipelineDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(run);
        run.CurrentStage = project.CurrentPhase.ToWire();
        run.PackStatus = project.Status.ToWire();
        run.Status = PipelineRunRules.StoredStatus(definition, run.PackStatus);
        run.CompletedAt = PipelineRunRules.IsFinal(run.Status) ? run.CompletedAt ?? now : null;
        run.Checkpoint = project.Checkpoint;
        run.ApprovalMode = project.ApprovalMode;

        // D-0.7.0-27: the review loop's counter is the project's review cycle (rollback restores it, a new version resets it).
        var review = Phase.Review.ToWire();
        if (!run.Iterations.TryGetValue(review, out var cycle) || cycle != project.ReviewCycle)
        {
            run.Iterations = new Dictionary<string, int>(run.Iterations, StringComparer.Ordinal) { [review] = project.ReviewCycle };
        }
    }
}

/// <summary>
/// FR-294, D-0.7.0-28: the backfill at start-up, before the job worker starts (hosted services start in order, and this one
/// finishes in <see cref="StartAsync"/>). Skipped while migrations are pending (the migration job hasn't run, or a test host
/// before <c>MigrateAsync</c>); the lifecycle's lazy path creates any run still missing. Failures are logged and never stop
/// the API.
/// </summary>
public sealed partial class PipelineRunBackfillService(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<PipelineRunBackfillService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<PipelineRegistryHost>() is not { Registry: not null } host)
            {
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                return;
            }

            var outcome = await PipelineRunBackfill.RunAsync(db, SdlcRunDefinition.Current(host), null, time.GetUtcNow(), cancellationToken);
            if (outcome.RunsCreated > 0 || outcome.RowsLinked > 0)
            {
                LogBackfilled(logger, outcome.RunsCreated, outcome.RowsLinked);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run backfill: {Runs} run(s) created, {Rows} row(s) linked")]
    private static partial void LogBackfilled(ILogger logger, int runs, int rows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pipeline run backfill failed; it runs again on the next start")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
