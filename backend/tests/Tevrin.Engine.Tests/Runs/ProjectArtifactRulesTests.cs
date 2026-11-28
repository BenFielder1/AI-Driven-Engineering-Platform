using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-352, §4.76: the pin rule on <c>fixture-cv</c>: the newest locked version of each project-scope artifact.</summary>
public class ProjectArtifactRulesTests
{
    private static readonly PipelineRegistry Registry = new([new FixtureCvPack()], [], Array.Empty<JobHandlerRegistration>());

    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly Guid V3 = Guid.CreateVersion7();

    private static PipelineDefinition Definition
    {
        get
        {
            Assert.True(Registry.TryGet(FixtureCvPack.PipelineId, out var loaded), string.Join("; ", Registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    private static ProjectArtifactVersion Version(Guid id, int version, ArtifactStatus status) => new("master_cv", id, version, status);

    [Fact]
    public void FR352_the_project_scoped_artifacts_of_fixture_cv_are_master_cv() =>
        Assert.Equal(["master_cv"], ProjectArtifactRules.ProjectScoped(Definition).Select(a => a.Key));

    [Fact]
    public void FR352_a_run_pins_the_newest_locked_version()
    {
        var pins = ProjectArtifactRules.Pins(Definition, [Version(V1, 1, ArtifactStatus.Superseded), Version(V2, 2, ArtifactStatus.Locked), Version(V3, 3, ArtifactStatus.Locked)]);

        Assert.True(pins.IsSuccess);
        Assert.Equal(new PinnedArtifact(V3, 3), Assert.Single(pins.Value).Value);
    }

    [Fact]
    public void FR352_a_newer_draft_or_rejected_version_is_ignored()
    {
        var pins = ProjectArtifactRules.Pins(Definition, [Version(V1, 1, ArtifactStatus.Locked), Version(V2, 2, ArtifactStatus.Locked), Version(V3, 3, ArtifactStatus.Draft)]);
        Assert.Equal(new PinnedArtifact(V2, 2), pins.Value["master_cv"]);

        var rejected = ProjectArtifactRules.Pins(Definition, [Version(V1, 1, ArtifactStatus.Locked), Version(V2, 2, ArtifactStatus.Rejected)]);
        Assert.Equal(new PinnedArtifact(V1, 1), rejected.Value["master_cv"]);
    }

    [Fact]
    public void FR352_SOURCE_ARTIFACT_MISSING_without_a_locked_version_naming_the_artifact()
    {
        var pins = ProjectArtifactRules.Pins(Definition, [Version(V1, 1, ArtifactStatus.Draft)]);

        Assert.True(pins.IsFailure);
        Assert.Equal(EngineErrorCodes.SourceArtifactMissing, pins.Error!.Code);
        Assert.Equal("Master CV", pins.Error.Reason);
        Assert.Equal(["master_cv"], pins.Error.Details);
        Assert.Equal(EngineErrorCodes.SourceArtifactMissing, ProjectArtifactRules.Pins(Definition, []).Error!.Code);
    }

    [Fact]
    public void FR352_versions_of_other_keys_are_not_pinned() =>
        Assert.True(ProjectArtifactRules.Pins(Definition, [new ProjectArtifactVersion("job", V1, 1, ArtifactStatus.Locked)]).IsFailure);

    [Fact]
    public void FR352_a_definition_without_project_scope_artifacts_pins_nothing()
    {
        var linear = new PipelineRegistry([new FixtureLinearPack()], [], Array.Empty<JobHandlerRegistration>());
        Assert.True(linear.TryGet(FixtureLinearPack.PipelineId, out var loaded));

        Assert.Empty(ProjectArtifactRules.Pins(loaded.Definition!, []).Value);
    }

    [Fact]
    public void FR352_locking_a_draft_locks_it_and_a_locked_version_is_a_no_op()
    {
        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Locked, Changed: true), ProjectArtifactRules.Lock(ArtifactStatus.Draft, 3, 2).Value);
        Assert.Equal(new ArtifactStatusChange(ArtifactStatus.Locked, Changed: true), ProjectArtifactRules.Lock(ArtifactStatus.Draft, 1, null).Value);
        Assert.False(ProjectArtifactRules.Lock(ArtifactStatus.Locked, 1, 3).Value.Changed);
    }

    [Fact]
    public void FR352_CONFLICT_locking_a_draft_older_than_the_newest_locked_version() =>
        Assert.Equal(EngineErrorCodes.Conflict, ProjectArtifactRules.Lock(ArtifactStatus.Draft, 2, 3).Error!.Code);

    [Fact]
    public void FR352_ARTIFACT_NOT_DRAFT_locking_a_superseded_version() =>
        Assert.Equal(EngineErrorCodes.ArtifactNotDraft, ProjectArtifactRules.Lock(ArtifactStatus.Superseded, 4, 3).Error!.Code);

    [Fact]
    public void FR352_a_new_version_supersedes_every_open_draft() =>
        Assert.Equal(
            [2, 4],
            ProjectArtifactRules.SupersededBy([(1, ArtifactStatus.Locked), (4, ArtifactStatus.Draft), (2, ArtifactStatus.Draft), (3, ArtifactStatus.Rejected)]));

    [Theory]
    [InlineData(new int[0], 1)]
    [InlineData(new[] { 1, 2 }, 3)]
    [InlineData(new[] { 3, 1 }, 4)]
    public void FR352_the_next_version_follows_the_newest(int[] stored, int next) =>
        Assert.Equal(next, ProjectArtifactRules.NextVersion(stored));
}
