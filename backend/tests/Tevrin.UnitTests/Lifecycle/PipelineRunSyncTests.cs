using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Lifecycle;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Infrastructure.Persistence;
using static Tevrin.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Tevrin.UnitTests.Lifecycle;

/// <summary>FR-292, FR-294, D-0.7.0-28: the run follows the project, and transitions store their generic kind.</summary>
public class PipelineRunSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Each SDLC move, applied on the engine: its stored generic kind and 0.6.1's <c>PHASE_CHANGED</c> kind.</summary>
    private static readonly (string Move, string Stored, string Event)[] Moves =
    [
        ("advance", "advance", "advance"),
        ("review cycle", "loop", "review_cycle"),
        ("rollback", "rollback", "rollback"),
        ("fallback", "fallback", "invalidation_fallback"),
        ("release no-op", "noop", "release_noop"),
        ("release completed", "status_change", "release_completed"),
        ("release awaiting merge", "status_change", "release_awaiting_merge"),
        ("release merge abandoned", "status_change", "release_merge_abandoned"),
        ("new version", "start", "new_version"),
    ];

    public static TheoryData<string, string, string> Kinds => [.. Moves];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FR294_each_transition_kind_is_stored_as_its_generic_kind(string move, string expected, string eventKind)
    {
        var all = new LifecycleSnapshot
        {
            Mode = ApprovalMode.Autonomous,
            TechnicalSpecificationLocked = true,
            ImplementationPlanLocked = true,
            CurrentPlanStepCount = 1,
            WorkingCopyPrepared = true,
            LatestReviewDecision = Domain.Review.ReviewDecision.ReviewRequired,
            LatestReviewProjectVersion = 4,
            OptimizationPlanLocked = true,
            OptimizationPlanStepCount = 1,
        };
        var review = new LifecycleState(Phase.Review, ProjectStatus.Reviewing, 4);
        var releasing = new LifecycleState(Phase.Release, ProjectStatus.Releasing, 4);
        var awaiting = releasing with { Status = ProjectStatus.AwaitingMerge };
        var applied = move switch
        {
            "advance" => Sdlc.Apply(review with { Phase = Phase.Planning, Status = ProjectStatus.Active }, SdlcTransitions.Advance(), all),
            "review cycle" => Sdlc.Apply(review, SdlcTransitions.ReviewCycle(), all),
            "rollback" => Sdlc.Apply(review, SdlcTransitions.Rollback(Phase.Planning, 2), all),
            "fallback" => Sdlc.Apply(review, SdlcTransitions.Fallback(Phase.Planning), all),
            "release no-op" => Sdlc.Apply(releasing, SdlcTransitions.ReleaseNoop(), all),
            "release completed" => Sdlc.Apply(releasing, SdlcTransitions.ReleaseCompleted(), all),
            "release awaiting merge" => Sdlc.Apply(releasing, SdlcTransitions.ReleaseAwaitingMerge(), all),
            "release merge abandoned" => Sdlc.Apply(awaiting, SdlcTransitions.ReleaseMergeAbandoned(), all),
            _ => Sdlc.NewVersion(releasing with { Status = ProjectStatus.Released }, LifecycleSnapshot.Empty),
        };

        Assert.True(applied.IsSuccess, applied.Error?.Message);
        Assert.Equal((expected, eventKind), (applied.Value.Kind, applied.Value.EventKind));
    }

    [Fact]
    public void FR294_every_engine_transition_kind_and_a_new_version_have_a_generic_kind() =>
        Assert.Equal(Enum.GetValues<RunTransitionKind>().Length + 1, Moves.Select(m => m.Stored).Distinct().Count());

    [Fact]
    public void FR292_the_run_copies_the_projects_stage_status_checkpoint_mode_and_review_cycle()
    {
        var project = new Project
        {
            CurrentPhase = Phase.Review,
            Status = ProjectStatus.Reviewing,
            Checkpoint = 4,
            ApprovalMode = ApprovalMode.Strict,
            ReviewCycle = 2,
        };
        var run = new PipelineRun { StartedAt = Now };

        PipelineRunSyncInterceptor.Copy(project, run, Definition, Now);

        Assert.Equal(("review", "reviewing", GenericRunStatus.Active, 4, ApprovalMode.Strict, (DateTimeOffset?)null),
            (run.CurrentStage, run.PackStatus, run.Status, run.Checkpoint, run.ApprovalMode, run.CompletedAt));
        Assert.Equal(new Dictionary<string, int> { ["review"] = 2 }, run.Iterations);
    }

    [Fact]
    public void FR292_a_released_project_completes_its_run_once()
    {
        var project = new Project { CurrentPhase = Phase.Release, Status = ProjectStatus.Released, Checkpoint = 6 };
        var run = new PipelineRun { StartedAt = Now };

        PipelineRunSyncInterceptor.Copy(project, run, Definition, Now);
        PipelineRunSyncInterceptor.Copy(project, run, Definition, Now.AddHours(1));

        Assert.Equal(("release", "released", GenericRunStatus.Completed, (DateTimeOffset?)Now), (run.CurrentStage, run.PackStatus, run.Status, run.CompletedAt));
    }

    [Theory]
    [InlineData(ProjectStatus.Active)]
    [InlineData(ProjectStatus.Building)]
    [InlineData(ProjectStatus.Reviewing)]
    [InlineData(ProjectStatus.Releasing)]
    [InlineData(ProjectStatus.RolledBack)]
    [InlineData(ProjectStatus.AwaitingMerge)]
    public void D_0_7_0_28_every_other_SDLC_status_is_stored_active(ProjectStatus status)
    {
        var run = new PipelineRun();

        PipelineRunSyncInterceptor.Copy(new Project { Status = status }, run, Definition, Now);

        Assert.Equal((GenericRunStatus.Active, (DateTimeOffset?)null), (run.Status, run.CompletedAt));
    }

    [Fact]
    public void D_0_7_0_28_a_synchronous_save_that_touches_a_project_is_not_supported()
    {
        var options = new DbContextOptionsBuilder<TevrinDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new PipelineRunSyncInterceptor(Services.GetRequiredService<PipelineRegistryHost>(), TimeProvider.System))
            .Options;
        using var db = new TevrinDbContext(options);
        db.Projects.Add(new Project { Name = "Sync save" });

        Assert.Throws<NotSupportedException>(() => db.SaveChanges());
    }
}
