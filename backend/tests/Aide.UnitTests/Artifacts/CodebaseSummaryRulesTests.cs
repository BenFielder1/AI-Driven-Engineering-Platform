using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.UnitTests.Artifacts;

/// <summary>FR-70, §4.12: the auto-locked <c>codebase_summary</c> artifact.</summary>
public class CodebaseSummaryRulesTests
{
    private static string Summary(params string[] skip) =>
        string.Join("\n\n", CodebaseSummaryRules.RequiredSections.Where(s => !skip.Contains(s)).Select(s => $"## {s}\nText."));

    [Fact]
    public void FR70_A_summary_needs_its_eight_sections()
    {
        Assert.Equal(
            ["Overview", "Stack & Dependencies", "Structure", "Key Modules", "Data & APIs", "Conventions", "Tests & CI", "Notable Risks"],
            CodebaseSummaryRules.RequiredSections);
        Assert.True(CodebaseSummaryRules.Check(Summary()).IsSuccess);

        var result = CodebaseSummaryRules.Check(Summary("Tests & CI", "Notable Risks"));

        Assert.Equal(ErrorCodes.MissingSections, result.Error!.Code);
        Assert.Equal(["Tests & CI", "Notable Risks"], result.Error.Details);
        Assert.Contains("\"Tests & CI\"", CodebaseSummaryRules.CorrectiveFeedback(result.Error.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void FR70_The_summary_is_auto_locked_produced_in_refinement_and_never_drafted()
    {
        Assert.True(CodebaseSummaryRules.IsAutoLocked(ArtifactType.CodebaseSummary));
        Assert.All(ArtifactRules.All, t => Assert.False(CodebaseSummaryRules.IsAutoLocked(t)));
        Assert.DoesNotContain(ArtifactType.CodebaseSummary, ArtifactRules.All);

        Assert.Equal(ArtifactStatus.Locked, ArtifactRules.InitialStatus(ArtifactType.CodebaseSummary));
        Assert.Equal(ArtifactStatus.Draft, ArtifactRules.InitialStatus(ArtifactType.TechnicalSpecification));
        Assert.Equal(Phase.Refinement, ArtifactRules.ProducingPhase(ArtifactType.CodebaseSummary));
        Assert.Null(RestoreRules.RestorablePhase(ArtifactType.CodebaseSummary));

        foreach (var phase in Enum.GetValues<Phase>())
        {
            Assert.Equal(ErrorCodes.ArtifactLocked, ArtifactRules.CanCreateDraft(ArtifactType.CodebaseSummary, phase, ProjectStatus.Active).Error!.Code);
        }
    }

    [Fact]
    public void FR70_A_new_summary_invalidates_nothing_and_a_corrupted_one_reruns_no_agent_kind()
    {
        var effect = InvalidationCascade.Effect(ArtifactType.CodebaseSummary, Phase.Planning);
        Assert.Equal((0, StepReset.None, (Phase?)null), (effect.InvalidatedTypes.Count, effect.StepReset, effect.FallbackPhase));

        var change = ArtifactRules.OnIntegrityCheck(ArtifactType.CodebaseSummary, ArtifactStatus.Locked, "changed", ContentHash.Compute("original"));
        Assert.Equal((ArtifactStatus.Corrupted, true, (AgentKind?)null), (change.NewStatus, change.Changed, change.RerunAgent));
        Assert.Null(ArtifactRules.RerunAgent(ArtifactType.CodebaseSummary));
        Assert.Equal(AgentKind.SpecGenerator, ArtifactRules.RerunAgent(ArtifactType.TechnicalSpecification));
    }
}
