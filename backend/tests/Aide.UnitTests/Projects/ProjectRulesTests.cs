using Aide.Domain.Common;
using Aide.Domain.Projects;

namespace Aide.UnitTests.Projects;

/// <summary>FR-5, FR-7: new-project and project-update validation.</summary>
public class ProjectRulesTests
{
    [Fact]
    public void FR5_ValidateNew_accepts_a_named_project_with_an_idea() =>
        Assert.True(ProjectRules.ValidateNew("Todo API", "Build a todo API.", 0.85m).IsSuccess);

    [Theory]
    [InlineData("", "idea", "name")]
    [InlineData("   ", "idea", "name")]
    [InlineData(null, "idea", "name")]
    [InlineData("Todo", "", "idea")]
    [InlineData("Todo", null, "idea")]
    public void FR5_ValidateNew_requires_a_name_and_an_idea(string? name, string? idea, string field)
    {
        var result = ProjectRules.ValidateNew(name, idea, 0.85m);

        AssertProblem(result, field);
    }

    [Theory]
    [InlineData(0.855)]
    [InlineData(0.901)]
    public void FR5_ValidateNew_threshold_has_at_most_two_decimals(double threshold) =>
        AssertProblem(ProjectRules.ValidateNew("Todo", "Idea", (decimal)threshold), "complianceThreshold");

    [Fact]
    public void FR5_ValidateNew_reports_every_problem()
    {
        var result = ProjectRules.ValidateNew(" ", " ", 0.123m);

        Assert.Equal(3, result.Error!.Details.Count);
        Assert.Equal(ErrorCodes.Validation, result.Error.Code);
    }

    [Fact]
    public void FR7_ValidateUpdate_omitted_fields_are_valid() =>
        Assert.True(ProjectRules.ValidateUpdate(null, null, null, clearTokenBudget: false).IsSuccess);

    [Fact]
    public void FR7_ValidateUpdate_sent_name_cannot_be_blank() =>
        AssertProblem(ProjectRules.ValidateUpdate("  ", null, null, false), "name");

    [Fact]
    public void FR7_ValidateUpdate_threshold_has_at_most_two_decimals()
    {
        Assert.True(ProjectRules.ValidateUpdate(null, 0.75m, null, false).IsSuccess);
        AssertProblem(ProjectRules.ValidateUpdate(null, 0.755m, null, false), "complianceThreshold");
    }

    [Fact]
    public void FR7_ValidateUpdate_token_budget_cannot_be_set_and_cleared()
    {
        Assert.True(ProjectRules.ValidateUpdate(null, null, 5000, false).IsSuccess);
        Assert.True(ProjectRules.ValidateUpdate(null, null, null, true).IsSuccess);
        AssertProblem(ProjectRules.ValidateUpdate(null, null, 5000, true), "tokenBudget");
    }

    internal static void AssertProblem(Result result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith(field + ":", StringComparison.Ordinal));
    }
}
