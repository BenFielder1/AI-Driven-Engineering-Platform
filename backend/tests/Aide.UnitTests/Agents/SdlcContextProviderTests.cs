using Aide.Api.Agents;
using Aide.Domain.Entities;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Agents;

/// <summary>FR-317: the SDLC context providers read only what their agent needs, from the run's version.</summary>
public class SdlcContextProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentContextRequest Request(string agent, Guid? runId = null, Guid? projectId = null) =>
        new(runId ?? Guid.NewGuid(), projectId ?? Guid.NewGuid(), SdlcPack.PipelineId, "stage", agent);

    [Fact]
    public async Task FR317_a_missing_project_gives_no_sections()
    {
        var source = new FakeSource(project: null);

        var sections = await new SdlcProjectContextProvider(source).GetContextAsync(Request(SdlcAgents.IdeaRefinement), Ct);

        Assert.Empty(sections);
        Assert.Equal(1, source.ProjectReads);
    }

    [Fact]
    public async Task FR317_project_context_is_the_agents_own()
    {
        var source = new FakeSource(new Project { Name = "Demo", Idea = "Idea", MaxFilesPerStep = 9 });

        var sections = await new SdlcProjectContextProvider(source).GetContextAsync(Request(SdlcAgents.BuildFix), Ct);

        var section = Assert.Single(sections);
        Assert.Equal((SdlcContextNames.MaxFilesPerStep, "9"), (section.Name, section.Content));
    }

    [Theory]
    [InlineData(SdlcAgents.BuildFix)]
    [InlineData("unknown_agent")]
    public async Task FR317_an_agent_without_iteration_context_skips_the_read(string agent)
    {
        var source = new FakeSource(new Project());

        var sections = await new SdlcIterationContextProvider(source).GetContextAsync(Request(agent), Ct);

        Assert.Empty(sections);
        Assert.Equal(0, source.IterationReads);
    }

    [Fact]
    public async Task FR317_an_agent_without_project_context_skips_the_read()
    {
        var source = new FakeSource(new Project());

        Assert.Empty(await new SdlcProjectContextProvider(source).GetContextAsync(Request(SdlcAgents.SpecGenerator), Ct));
        Assert.Equal(0, source.ProjectReads);
    }

    [Fact]
    public async Task FR317_iteration_context_is_read_for_the_run_and_project()
    {
        var (runId, projectId) = (Guid.NewGuid(), Guid.NewGuid());
        var version = new ReleaseVersion { Request = "Add search", BaseCommitSha = "abc" };
        var summary = new Artifact { Type = SdlcArtifacts.CodebaseSummary, Content = "summary" };
        var source = new FakeSource(new Project()) { Iteration = new IterationContext(version, true, true, summary, null, null) };

        var sections = await new SdlcIterationContextProvider(source).GetContextAsync(Request(SdlcAgents.StackAnalyst, runId, projectId), Ct);

        Assert.Equal((runId, projectId), source.LastIteration);
        Assert.Equal(
            [(SdlcContextNames.VersionRequest, "Add search"), (SdlcContextNames.Base, "commit abc"), (SdlcContextNames.CodebaseSummary, "summary")],
            sections.Select(s => (s.Name, s.Content)));
    }

    [Fact]
    public void FR317_the_pack_registers_its_tools_and_context_names_and_the_definition_is_unchanged()
    {
        var pack = Assert.Single(Registry.Packs);

        Assert.Equal(SdlcToolNames.All.Order(StringComparer.Ordinal), pack.Tools.Order(StringComparer.Ordinal));
        Assert.Equal(SdlcContextNames.All.Order(StringComparer.Ordinal), pack.ContextNames.Order(StringComparer.Ordinal));
        Assert.Empty(Registry.Errors);
        Assert.True(Registry.TryGet(SdlcPack.PipelineId, out var loaded));
        Assert.Equal(SdlcDefinitionHash, loaded.Hash);
    }

    /// <summary>
    /// The hash of the 0.7.0 <c>sdlc.pipeline.yaml</c> (version 1, unchanged by FR-317: the hash covers the definition only,
    /// not the pack's tool and context names). FR-321 changes it.
    /// </summary>
    private const string SdlcDefinitionHash = "90f1f065397fe245955c62ef5057122a6fca2ae373941a90dc37b431ecf0c9e7";

    private sealed class FakeSource(Project? project) : ISdlcContextSource
    {
        public IterationContext Iteration { get; init; } = IterationContext.None;

        public int ProjectReads { get; private set; }

        public int IterationReads { get; private set; }

        public (Guid RunId, Guid ProjectId)? LastIteration { get; private set; }

        public Task<Project?> ProjectAsync(Guid projectId, CancellationToken cancellationToken)
        {
            ProjectReads++;
            return Task.FromResult(project);
        }

        public Task<IterationContext> IterationAsync(Guid runId, Guid projectId, CancellationToken cancellationToken)
        {
            IterationReads++;
            LastIteration = (runId, projectId);
            return Task.FromResult(Iteration);
        }
    }
}
