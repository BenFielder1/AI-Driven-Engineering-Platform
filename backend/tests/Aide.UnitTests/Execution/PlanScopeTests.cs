using System.Text.Json;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Specs;

namespace Aide.UnitTests.Execution;

/// <summary>§4.13 plan scope and FR-76 (no preset): <c>covers</c>, <c>UNCOVERED_REQUIREMENTS</c>, the <c>generalist</c> agent.</summary>
public class PlanScopeTests
{
    private static readonly string[] CoversFr2 = ["FR-2"];
    private static readonly int[] DependsOnStep1 = [1];

    private static PlanStep Step(int number, string path, FileAction action = FileAction.Create, string? agent = null, string[]? covers = null) =>
        new(number, $"Step {number}", "Do it.", [new DeclaredFile(path, action)], number == 1 ? [] : [number - 1], null, agent, covers ?? []);

    private static SpecRequirement Requirement(string id, RequirementTag tag) =>
        new(id, int.Parse(id[3..], System.Globalization.CultureInfo.InvariantCulture), id, [tag], true, 1);

    [Fact]
    public void S4_13_New_and_changed_requirements_no_step_covers_are_an_UNCOVERED_REQUIREMENTS_warning()
    {
        IReadOnlyList<SpecRequirement> requirements =
        [
            Requirement("FR-1", RequirementTag.Unchanged),
            Requirement("FR-2", RequirementTag.Changed),
            Requirement("FR-3", RequirementTag.New),
            Requirement("FR-4", RequirementTag.New),
            Requirement("FR-5", RequirementTag.Removed),
        ];
        IReadOnlyList<PlanStep> steps = [Step(1, "src/export.ts", covers: ["fr-2"]), Step(2, "src/import.ts", covers: ["FR-1", "FR-9"])];

        var warning = PlanScope.Check(steps, requirements);

        Assert.Equal(ErrorCodes.UncoveredRequirements, warning!.Code);
        Assert.Equal(["FR-3", "FR-4"], warning.Details);
        Assert.Contains("FR-3, FR-4", warning.Message, StringComparison.Ordinal);
        Assert.Null(PlanScope.Check([Step(1, "a.ts", covers: ["FR-2", "FR-3", "FR-4"])], requirements));
    }

    [Fact]
    public void S4_13_A_first_version_spec_without_tags_has_no_uncovered_requirements()
    {
        IReadOnlyList<SpecRequirement> untagged = [new("FR-1", 1, "Sign in", [], true, 1)];

        Assert.Empty(PlanScope.Uncovered([Step(1, "a.ts")], untagged));
    }

    [Fact]
    public void S4_13_Iteration_steps_may_modify_existing_files_and_one_creator_per_file_applies_within_the_plan()
    {
        IReadOnlyList<PlanStep> plan = [Step(1, "src/existing.ts", FileAction.Modify), Step(2, "src/existing.ts", FileAction.Modify), Step(3, "src/new.ts")];

        Assert.True(PlanValidator.Validate(plan).IsSuccess);
        var twice = PlanValidator.Validate([Step(1, "src/new.ts"), Step(2, "src/new.ts")]);
        Assert.Contains(twice.Error!.Details, d => d.Contains("created by step 1 and again by step 2", StringComparison.Ordinal));
    }

    [Fact]
    public void FR76_Without_a_preset_a_missing_agent_becomes_generalist_and_covers_are_normalised()
    {
        var result = PlanValidator.Validate([Step(1, "a.ts", covers: [" fr-2", "FR-2", "FR-10"])]);

        var step = Assert.Single(result.Value);
        Assert.Equal(PlanAgents.Generalist, step.Agent);
        Assert.Equal(["FR-2", "FR-10"], step.Covers);
    }

    [Fact]
    public void FR76_Without_a_preset_any_other_agent_or_a_bad_requirement_id_is_INVALID_PLAN()
    {
        var result = PlanValidator.Validate([Step(1, "a.ts", agent: "ml-engineer", covers: ["login"])]);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Equal(
            [
                "Step 1 names the agent 'ml-engineer'; without a preset every step is \"generalist\".",
                "Step 1 covers 'login', which is not a requirement ID (FR-n).",
            ],
            result.Error.Details);
        Assert.True(PlanValidator.Validate([Step(1, "a.ts", agent: "generalist")]).IsSuccess);
    }

    [Fact]
    public void FR76_A_preset_catalogue_is_the_extension_point_for_agent_keys()
    {
        var catalogue = new PlanAgents(["db-engineer", "ui-engineer"], "nextjs-supabase");

        Assert.True(PlanValidator.Validate([Step(1, "a.sql", agent: "db-engineer")], agents: catalogue).IsSuccess);
        Assert.Equal(
            ["Step 1 has no \"agent\"; use one of db-engineer, ui-engineer."],
            PlanValidator.Validate([Step(1, "a.sql")], agents: catalogue).Error!.Details);
        Assert.Contains(
            "unknown agent 'ml-engineer'; the nextjs-supabase preset has db-engineer, ui-engineer",
            PlanValidator.Validate([Step(1, "a.sql", agent: "ml-engineer")], agents: catalogue).Error!.Details[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public void FR76_Agent_and_covers_are_read_from_the_plan_json_and_round_trip_through_the_document()
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new { number = 1, title = "Export", task = "Add CSV export.", files = new[] { new { path = "src/export.ts", action = "create" } }, covers = CoversFr2 },
                new { number = 2, title = "Docs", task = "Document it.", files = new[] { new { path = "README.md", action = "modify" } }, dependencies = DependsOnStep1, agent = "generalist" },
            },
        });

        var steps = PlanDocument.ParseAgentOutput(reply, 20).Value;
        Assert.Equal(["FR-2"], steps[0].Covers);
        Assert.Equal(["generalist", "generalist"], steps.Select(s => s.Agent));

        var markdown = PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, steps, 20).Value;
        Assert.Contains("**Covers:** FR-2", markdown, StringComparison.Ordinal);
        Assert.Equal(PlanDocument.ToJson(steps), PlanDocument.ToJson(PlanDocument.ParseMarkdown(markdown, 20).Value));
    }

    [Fact]
    public void FR76_Covers_and_agent_of_the_wrong_json_type_are_INVALID_PLAN()
    {
        var reply = JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new { number = 1, title = "A", task = "B", files = new[] { new { path = "a.ts", action = "create" } }, covers = "FR-1", agent = 3 },
            },
        });

        var result = PlanDocument.ParseAgentOutput(reply, 20);

        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        Assert.Equal(2, result.Error.Details.Count);
    }
}
