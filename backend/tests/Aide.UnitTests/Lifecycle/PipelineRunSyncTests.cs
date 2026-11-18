using Aide.Api.Features.Lifecycle;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>FR-292, FR-294, D-0.7.0-28: the run follows the project, and transitions store their generic kind.</summary>
public class PipelineRunSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<TransitionKind, string> Kinds => new()
    {
        { TransitionKind.Advance, "advance" },
        { TransitionKind.ReviewCycle, "loop" },
        { TransitionKind.Rollback, "rollback" },
        { TransitionKind.InvalidationFallback, "fallback" },
        { TransitionKind.ReleaseNoop, "noop" },
        { TransitionKind.ReleaseCompleted, "status_change" },
        { TransitionKind.ReleaseAwaitingMerge, "status_change" },
        { TransitionKind.ReleaseMergeAbandoned, "status_change" },
        { TransitionKind.NewVersion, "start" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FR294_each_transition_kind_is_stored_as_its_generic_kind(TransitionKind kind, string expected) =>
        Assert.Equal(expected, TransitionKinds.Of(kind));

    [Fact]
    public void FR294_every_transition_kind_has_a_generic_kind() =>
        Assert.Equal(Enum.GetValues<TransitionKind>().Length, Kinds.Count);

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
        var options = new DbContextOptionsBuilder<AideDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new PipelineRunSyncInterceptor(Services.GetRequiredService<PipelineRegistryHost>(), TimeProvider.System))
            .Options;
        using var db = new AideDbContext(options);
        db.Projects.Add(new Project { Name = "Sync save" });

        Assert.Throws<NotSupportedException>(() => db.SaveChanges());
    }
}
