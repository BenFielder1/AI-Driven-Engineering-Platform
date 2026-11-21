using Aide.Api.Agents;
using Aide.Domain.Entities;
using Aide.Domain.Specs;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-317: the SDLC context values are the ones 0.6.1's job handlers put into the prompt (<see cref="SdlcContextValues"/>),
/// and each agent gets only its own (<see cref="SdlcAgentContext"/>).
/// </summary>
public class SdlcContextValuesTests
{
    private static Artifact Doc(string type, string content) => new() { Type = type, Content = content };

    [Fact]
    public void FR317_project_values_are_the_name_the_idea_and_the_invariant_file_limit()
    {
        var values = SdlcContextValues.FromProject(new Project { Name = "Demo", Idea = "A tiny shop", MaxFilesPerStep = 12 });

        Assert.Equal("Demo", values[SdlcContextNames.ProjectName]);
        Assert.Equal("A tiny shop", values[SdlcContextNames.Idea]);
        Assert.Equal("12", values[SdlcContextNames.MaxFilesPerStep]);
    }

    [Fact]
    public void FR317_iteration_values_are_the_summary_baselines_spec_mode_request_and_base()
    {
        var version = new ReleaseVersion { Request = "Add search", BaseTag = "v1.2.0", BaseCommitSha = "abc123" };
        var iteration = new IterationContext(
            version,
            HasExistingCode: true,
            IsIteration: true,
            Doc(SdlcArtifacts.CodebaseSummary, "summary"),
            Doc(SdlcArtifacts.TechnicalSpecification, "baseline spec"),
            Doc(SdlcArtifacts.ProjectRequest, "baseline request"));

        var values = SdlcContextValues.FromIteration(iteration);

        Assert.Equal("summary", values[SdlcContextNames.CodebaseSummary]);
        Assert.Equal(iteration.SpecContext.Mode, values[SdlcContextNames.SpecMode]);
        Assert.Equal("baseline spec", values[SdlcContextNames.BaselineSpecification]);
        Assert.Equal("baseline spec", values[SdlcContextNames.PreviousSpecification]);
        Assert.Equal("baseline request", values[SdlcContextNames.PreviousProjectRequest]);
        Assert.Equal("Add search", values[SdlcContextNames.VersionRequest]);
        Assert.Equal("v1.2.0 (abc123)", values[SdlcContextNames.Base]);
    }

    [Fact]
    public void FR317_base_is_the_commit_without_a_base_tag_and_absent_without_a_base_commit()
    {
        Assert.Equal("commit abc123", SdlcContextValues.BaseText(new ReleaseVersion { BaseCommitSha = "abc123" }));
        Assert.Null(SdlcContextValues.BaseText(new ReleaseVersion { BaseTag = "v1.0.0" }));
    }

    [Fact]
    public void FR317_no_version_gives_the_first_version_spec_mode_and_nothing_else()
    {
        var values = SdlcContextValues.FromIteration(IterationContext.None);

        Assert.Equal(SpecValidationContext.FirstVersionMode, values[SdlcContextNames.SpecMode]);
        Assert.All(values.Where(v => v.Key != SdlcContextNames.SpecMode), v => Assert.Null(v.Value));
    }

    [Fact]
    public void FR317_each_agent_gets_only_the_context_its_handler_gave_it()
    {
        var all = SdlcContextNames.All.ToDictionary(n => n, n => (string?)$"value of {n}", StringComparer.Ordinal);

        Assert.Equal(
            [SdlcContextNames.ProjectName, SdlcContextNames.Idea, SdlcContextNames.CodebaseSummary, SdlcContextNames.PreviousProjectRequest, SdlcContextNames.PreviousSpecification],
            SdlcAgentContext.Sections(SdlcAgents.IdeaRefinement, all).Select(s => s.Name));
        Assert.Equal(
            [SdlcContextNames.SpecMode, SdlcContextNames.BaselineSpecification, SdlcContextNames.CodebaseSummary],
            SdlcAgentContext.Sections(SdlcAgents.SpecGenerator, all).Select(s => s.Name));
        Assert.Equal([SdlcContextNames.MaxFilesPerStep], SdlcAgentContext.Sections(SdlcAgents.BuildFix, all).Select(s => s.Name));
        Assert.Equal(
            [SdlcContextNames.ProjectName, SdlcContextNames.VersionRequest, SdlcContextNames.Base],
            SdlcAgentContext.Sections(SdlcAgents.RepositoryAnalyst, all).Select(s => s.Name));
        Assert.Empty(SdlcAgentContext.Sections("unknown_agent", all));
        Assert.Equal("value of base", SdlcAgentContext.Sections(SdlcAgents.StackAnalyst, all).Single(s => s.Name == SdlcContextNames.Base).Content);
    }

    [Fact]
    public void FR317_an_absent_or_null_value_gives_no_section_except_an_overriding_input()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SdlcContextNames.ProjectName] = "Demo",
            [SdlcContextNames.Base] = null,
            [SdlcContextNames.CodebaseSummary] = null,
        };

        // version_request is absent, base is null: no sections. A known null summary is an empty section, so the
        // stage's codebase_summary input artifact can't fill {{CODEBASE_SUMMARY?}} (0.6.1 rendered it empty).
        Assert.Equal(
            [(SdlcContextNames.ProjectName, "Demo"), (SdlcContextNames.CodebaseSummary, string.Empty)],
            SdlcAgentContext.Sections(SdlcAgents.StackAnalyst, values).Select(s => (s.Name, s.Content)));
        Assert.Empty(SdlcAgentContext.Sections(SdlcAgents.PlanGenerator, new Dictionary<string, string?>(StringComparer.Ordinal)));
    }
}
