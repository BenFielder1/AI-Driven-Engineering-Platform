using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Presets;
using Aide.Domain.Specs;

namespace Aide.UnitTests.Presets;

/// <summary>FR-76, §4.11, §4.12: plan validation with a preset (after the v1 rules), on the test fixture preset.</summary>
public class PresetPlanValidatorTests
{
    private static readonly Preset Fixture = TestPresets.Fixture;

    private static PlanStep Step(int number, string agent, params string[] paths) =>
        new(number, $"Step {number}", "Do it.", [.. paths.Select(p => new DeclaredFile(p, FileAction.Create))], number == 1 ? [] : [number - 1], null, agent);

    /// <summary>A plan that follows every rule of the fixture: scaffold, migration, domain, API, UI.</summary>
    private static List<PlanStep> GoodPlan() =>
    [
        Step(1, "project-scaffolder", "package.json", "app/layout.tsx"),
        Step(2, "db-engineer", "supabase/migrations/0001_tasks.sql"),
        Step(3, "domain-logic-engineer", "lib/tasks/rules.ts"),
        Step(4, "api-engineer", "app/api/tasks/route.ts"),
        Step(5, "ui-engineer", "app/tasks/page.tsx", "components/TaskList.tsx"),
    ];

    private static Result<IReadOnlyList<PlanStep>> Validate(IReadOnlyList<PlanStep> steps, bool repoEmpty = true) =>
        PlanValidator.Validate(steps, agents: PlanAgents.For(Fixture, repoEmpty));

    private static IReadOnlyList<string> Reasons(Result<IReadOnlyList<PlanStep>> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InvalidPlan, result.Error!.Code);
        return result.Error.Details;
    }

    [Fact]
    public void FR76_A_plan_following_the_preset_rules_is_valid_with_no_warnings()
    {
        var result = Validate(GoodPlan());

        Assert.True(result.IsSuccess, result.IsFailure ? string.Join(" | ", result.Error!.Details) : null);
        Assert.Empty(PresetPlanValidator.Warnings(result.Value, Fixture));
    }

    [Fact]
    public void FR76_An_unknown_agent_ml_engineer_is_INVALID_PLAN_naming_the_agent()
    {
        var plan = GoodPlan();
        plan[2] = Step(3, "ml-engineer", "lib/tasks/rules.ts");

        var reason = Assert.Single(Reasons(Validate(plan)));

        Assert.Contains("'ml-engineer'", reason, StringComparison.Ordinal);
        Assert.Contains("test-stack", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FR76_A_missing_agent_is_INVALID_PLAN_with_a_preset()
    {
        var plan = GoodPlan();
        plan[2] = plan[2] with { Agent = null };

        Assert.StartsWith("Step 3 has no \"agent\"", Assert.Single(Reasons(Validate(plan))), StringComparison.Ordinal);
    }

    [Fact]
    public void FR76_A_db_engineer_step_declaring_app_files_is_an_error()
    {
        var plan = GoodPlan();
        plan[1] = Step(2, "db-engineer", "supabase/migrations/0001_tasks.sql", "app/tasks/edit/page.tsx");

        var reason = Assert.Single(Reasons(Validate(plan)));

        Assert.Equal("Step 2 is assigned \"db-engineer\", which must not declare 'app/tasks/edit/page.tsx' (a db-engineer step never shares a step with UI files).", reason);
    }

    [Fact]
    public void FR76_A_migration_after_the_api_step_that_uses_it_is_an_error()
    {
        List<PlanStep> plan =
        [
            Step(1, "project-scaffolder", "package.json"),
            Step(2, "api-engineer", "app/api/tasks/route.ts"),
            Step(3, "db-engineer", "supabase/migrations/0001_tasks.sql"),
        ];

        var reason = Assert.Single(Reasons(Validate(plan)));

        Assert.Equal("Step 3 declares 'supabase/migrations/0001_tasks.sql', which must come before step 2 ('app/api/tasks/route.ts'): migrations before the API that uses them.", reason);
    }

    [Fact]
    public void FR76_On_an_empty_repository_step_1_must_be_the_scaffolder()
    {
        var plan = GoodPlan().Skip(1).Select((s, i) => s with { Number = i + 1, Dependencies = i == 0 ? [] : [i] }).ToList();

        var reason = Assert.Single(Reasons(Validate(plan, repoEmpty: true)));
        Assert.Equal("The repository is empty, so step 1 must be assigned \"project-scaffolder\" (the test-stack preset scaffolds first); it is assigned \"db-engineer\".", reason);

        // A repository with code (an import, or a later version) needs no scaffold step.
        Assert.True(Validate(plan, repoEmpty: false).IsSuccess);
    }

    [Fact]
    public void FR76_A_step_with_two_migrations_breaks_the_per_step_file_limit()
    {
        var plan = GoodPlan();
        plan[1] = Step(2, "db-engineer", "supabase/migrations/0001_tasks.sql", "supabase/migrations/0002_tags.sql");

        Assert.Equal(
            "Step 2 declares 2 files matching supabase/migrations/** ('supabase/migrations/0001_tasks.sql', 'supabase/migrations/0002_tags.sql'); the test-stack preset allows at most 1 per step.",
            Assert.Single(Reasons(Validate(plan))));
    }

    [Fact]
    public void FR76_Files_outside_the_agents_owned_paths_are_an_AGENT_SCOPE_warning_not_an_error()
    {
        var plan = GoodPlan();
        plan[3] = Step(4, "api-engineer", "app/api/tasks/route.ts", "lib/tasks/validation.ts");

        var result = Validate(plan);

        Assert.True(result.IsSuccess);
        var warning = Assert.Single(PresetPlanValidator.Warnings(result.Value, Fixture));
        Assert.Equal(ErrorCodes.AgentScope, warning.Code);
        Assert.Equal("Step 4 (api-engineer) declares 'lib/tasks/validation.ts' outside its owned paths (app/api/**, lib/api/**).", Assert.Single(warning.Details));
    }

    [Fact]
    public void S4_12_A_lib_domain_step_after_an_app_step_is_a_soft_PLAN_ORDER_warning()
    {
        List<PlanStep> plan =
        [
            Step(1, "project-scaffolder", "package.json"),
            Step(2, "ui-engineer", "app/tasks/page.tsx"),
            Step(3, "domain-logic-engineer", "lib/tasks/rules.ts"),
        ];

        var result = Validate(plan);

        Assert.True(result.IsSuccess);
        var warning = Assert.Single(PresetPlanValidator.Warnings(result.Value, Fixture));
        Assert.Equal(ErrorCodes.PlanOrder, warning.Code);
        Assert.StartsWith("Step 3 declares 'lib/tasks/rules.ts', which must come before step 2 ('app/tasks/page.tsx')", Assert.Single(warning.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void FR76_All_warnings_lists_UNCOVERED_REQUIREMENTS_then_the_preset_warnings()
    {
        var plan = GoodPlan();
        plan[3] = Step(4, "api-engineer", "app/api/tasks/route.ts", "lib/tasks/validation.ts");
        var steps = Validate(plan).Value;
        SpecRequirement[] requirements = [new("FR-1", 1, "FR-1", [RequirementTag.New], true, 1)];

        Assert.Equal([ErrorCodes.UncoveredRequirements, ErrorCodes.AgentScope], PresetPlanValidator.AllWarnings(steps, requirements, Fixture).Select(w => w.Code));
        Assert.Equal([ErrorCodes.UncoveredRequirements], PresetPlanValidator.AllWarnings(steps, requirements, preset: null).Select(w => w.Code));
        Assert.Empty(PresetPlanValidator.AllWarnings(steps, requirements: null, preset: null));
    }

    [Fact]
    public void FR76_Without_a_preset_the_generalist_rule_is_unchanged()
    {
        Assert.Same(PlanAgents.None, PlanAgents.For(null));
        var plan = GoodPlan().Select(s => s with { Agent = null }).ToList();

        var result = PlanValidator.Validate(plan, agents: PlanAgents.For(null));

        Assert.True(result.IsSuccess);
        Assert.All(result.Value, s => Assert.Equal(PlanAgents.Generalist, s.Agent));
        Assert.Contains("without a preset every step is \"generalist\"", Assert.Single(Reasons(PlanValidator.Validate(GoodPlan().Take(1).ToList()))), StringComparison.Ordinal);
    }
}
