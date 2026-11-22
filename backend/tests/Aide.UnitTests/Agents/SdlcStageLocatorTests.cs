using Aide.Api.Agents;
using Tevrin.Engine.Domain.Entities;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-310, FR-320: the agent runs <see cref="SdlcStageLocator"/> links to the project's pipeline run before the engine
/// runs: a 0.6.1 build's runs of the current version that no pipeline run owns (a rolling deploy), so a retried job resumes
/// its <c>running</c> row instead of failing the engine's ownership check.
/// </summary>
public class SdlcStageLocatorTests
{
    private static readonly Guid Project = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();

    [Fact]
    public void FR320_links_only_the_projects_unlinked_runs_of_the_current_version()
    {
        var unlinked = Run(Project, Version, null);
        var runs = new[]
        {
            unlinked,
            Run(Project, Version, Guid.CreateVersion7()), // already owned by a pipeline run
            Run(Project, Guid.CreateVersion7(), null), // an earlier version
            Run(Project, null, null), // outside any version
            Run(Guid.CreateVersion7(), Version, null), // another project
        };

        var selected = runs.Where(SdlcStageLocator.Unlinked(Project, Version).Compile()).ToList();

        Assert.Same(unlinked, Assert.Single(selected));
    }

    private static AgentRun Run(Guid projectId, Guid? versionId, Guid? pipelineRunId) => new()
    {
        ProjectId = projectId,
        ReleaseVersionId = versionId,
        PipelineRunId = pipelineRunId,
        Status = AgentRunStatus.Running,
    };
}
