using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-295, §4.10: an agent stage reads and changes only its run's subject (the SDLC's release version).</summary>
public class AgentStageSubjectTests
{
    private static Artifact Add(AgentStageHarness harness, string type, int version, ArtifactStatus status, Guid subject, string content, string? hash = null)
    {
        var artifact = new Artifact
        {
            ProjectId = harness.Run.ProjectId,
            Type = type,
            Version = version,
            Content = content,
            ContentHash = hash ?? ContentHash.Compute(content),
            Status = status,
            ReleaseVersionId = subject,
        };
        harness.Store.Artifacts.Add(artifact);
        return artifact;
    }

    [Fact]
    public async Task FR295_inputs_come_only_from_the_runs_subject()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        Add(harness, "draft", 1, ArtifactStatus.Locked, harness.Run.SubjectId!.Value, """{"title":"mine","items":[]}""");
        Add(harness, "draft", 2, ArtifactStatus.Locked, Guid.CreateVersion7(), """{"title":"other version","items":[]}""");

        await harness.RunStageAsync("check");

        Assert.Contains("mine", checker.Prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("other version", checker.Prompts[0], StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(harness.Store.AgentRuns[0].InputArtifacts).Version);
    }

    [Fact]
    public async Task FR295_FR20_a_corrupted_locked_input_is_absent()
    {
        var checker = new ScriptedResponder("checker", AgentStageHarness.ValidReport);
        using var harness = new AgentStageHarness([checker]);
        Add(harness, "draft", 1, ArtifactStatus.Locked, harness.Run.SubjectId!.Value, """{"title":"tampered","items":[]}""", new string('0', 64));

        await harness.RunStageAsync("check");

        Assert.Contains("<draft></draft>", checker.Prompts[0], StringComparison.Ordinal);
        Assert.Empty(harness.Store.AgentRuns[0].InputArtifacts);
    }

    [Fact]
    public async Task FR295_FR58_versions_count_project_wide_but_only_the_subjects_drafts_are_superseded()
    {
        using var harness = new AgentStageHarness([new ScriptedResponder("checker", AgentStageHarness.ValidReport)]);
        var other = Add(harness, "report", 1, ArtifactStatus.Draft, Guid.CreateVersion7(), AgentStageHarness.ValidReport);
        var mine = Add(harness, "report", 2, ArtifactStatus.Draft, harness.Run.SubjectId!.Value, AgentStageHarness.ValidReport);

        await harness.RunStageAsync("check");

        var created = harness.Store.Artifacts[^1];
        Assert.Equal((3, (int?)2, ArtifactStatus.Draft), (created.Version, created.ParentVersion, created.Status));
        Assert.Equal(ArtifactStatus.Draft, other.Status);
        Assert.Equal(ArtifactStatus.Superseded, mine.Status);
        Assert.Equal(harness.Run.SubjectId, created.ReleaseVersionId);
    }

    [Fact]
    public void FR298_D_0_7_0_33_validation_rejected_runs_count_as_failures_in_the_prompt_stats()
    {
        using var db = new OfflineEngineDbContext();
        var queries = new RunLogQueries(db);

        var sql = queries.GroupsQuery(db.PipelineRuns.Where(p => p.PipelineId == "fixture-review")).ToQueryString();

        Assert.Contains("rejected: ", sql, StringComparison.Ordinal);
    }
}
