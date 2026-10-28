using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Specs;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackResolverTests;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>FR-117, FR-124, FR-125, FR-127: the composed plan rules, warnings and protected paths.</summary>
public class CompositionPlanValidatorTests
{
    private const string Migration = "backend/src/Shop.Infrastructure/Migrations/20261028120000_AddOrders.cs";
    private const string Endpoints = "backend/src/Shop.Api/Features/Orders/OrderEndpoints.cs";
    private const string Contract = "backend/openapi/api.json";

    private static PlanStep Step(int number, string? agent, params string[] files) =>
        new(number, $"Step {number}", "Do it.", [.. files.Select(f => new DeclaredFile(f, FileAction.Create))], [], Agent: agent);

    private static StackResolution Aspnet(StackChange? change = null) => Resolve(AspnetSpa(), change);

    private static List<string> InvalidPlan(IReadOnlyList<PlanStep> steps, StackResolution resolution, bool repoEmpty = false) =>
        [.. CompositionPlanValidator.Errors(steps, resolution, repoEmpty).Where(i => i.Code == ErrorCodes.InvalidPlan).Select(i => i.Message)];

    // ── FR-124 ───────────────────────────────────────────────────────────

    [Fact]
    public void FR124_An_EF_Core_migration_in_a_backend_step_is_INVALID_PLAN_naming_the_exclusivity_rule()
    {
        var resolution = Aspnet();
        IReadOnlyList<PlanStep> steps = [Step(1, "backend-engineer", Migration)];

        var issues = CompositionPlanValidator.Errors(steps, resolution, repoEmpty: false);

        var issue = Assert.Single(issues);
        Assert.Equal(ErrorCodes.InvalidPlan, issue.Code);
        Assert.Contains("migrations belong to data-engineer", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ErrorCodes.InvalidPlan, CompositionPlanValidator.ToError(issues)?.Code);

        Assert.Empty(CompositionPlanValidator.Errors([Step(1, "data-engineer", Migration)], resolution, repoEmpty: false));
        Assert.Null(CompositionPlanValidator.ToError([]));
    }

    [Fact]
    public void FR124_Unknown_or_missing_agents_are_INVALID_PLAN_reasons()
    {
        var resolution = Aspnet();

        var reasons = CompositionPlanValidator.AgentErrors([Step(1, "db-engineer", Migration), Step(2, null, Endpoints), Step(3, "backend-engineer", Endpoints)], resolution);

        Assert.Equal(2, reasons.Count);
        Assert.Contains("unknown agent 'db-engineer'", reasons[0], StringComparison.Ordinal);
        Assert.Contains("Step 2 has no \"agent\"", reasons[1], StringComparison.Ordinal);
    }

    [Fact]
    public void FR124_Files_outside_the_agents_owned_paths_are_an_AGENT_SCOPE_warning()
    {
        var resolution = Aspnet();

        var warning = Assert.Single(CompositionPlanValidator.Warnings([Step(1, "frontend-engineer", "backend/src/Shop.Domain/Order.cs")], resolution));
        Assert.Equal(ErrorCodes.AgentScope, warning.Code);
        Assert.Contains("frontend-engineer", warning.Details[0], StringComparison.Ordinal);

        Assert.Empty(CompositionPlanValidator.Warnings([Step(1, "frontend-engineer", "frontend/src/features/orders/orders-page.tsx")], resolution));
    }

    [Fact]
    public void FR124_Hard_order_rules_are_errors_and_scaffolder_steps_are_exempt()
    {
        var resolution = Aspnet();

        var reasons = InvalidPlan([Step(1, "backend-engineer", "backend/src/Shop.Api/Program.cs"), Step(2, "data-engineer", Migration)], resolution);
        Assert.Contains(reasons, r => r.Contains("Migrations come before the endpoints", StringComparison.Ordinal));

        Assert.Empty(InvalidPlan([Step(1, "scaffolder", "backend/src/Shop.Api/Program.cs"), Step(2, "data-engineer", Migration)], resolution, repoEmpty: true));
        Assert.Empty(InvalidPlan([Step(1, "data-engineer", Migration), Step(2, "backend-engineer", "backend/src/Shop.Api/Program.cs")], resolution));
    }

    [Fact]
    public void FR124_Soft_order_rules_are_a_PLAN_ORDER_warning_not_an_error()
    {
        var resolution = Aspnet();
        IReadOnlyList<PlanStep> steps = [Step(1, "frontend-engineer", "frontend/src/api/generated/schema.ts"), Step(2, "backend-engineer", Contract)];

        Assert.Empty(InvalidPlan(steps, resolution));
        var warning = Assert.Single(CompositionPlanValidator.Warnings(steps, resolution), w => w.Code == ErrorCodes.PlanOrder);
        Assert.All(warning.Details, d => Assert.StartsWith("Step 2 declares 'backend/openapi/api.json', which must come before step 1", d, StringComparison.Ordinal));
    }

    [Fact]
    public void FR124_Exclusivity_keeps_CI_and_platform_files_with_their_specialists_and_exempts_the_scaffolder()
    {
        var resolution = Resolve(AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));

        Assert.Contains(InvalidPlan([Step(1, "frontend-engineer", "frontend/netlify.toml")], resolution), r => r.Contains("only agent for Netlify config", StringComparison.Ordinal));
        Assert.Contains(InvalidPlan([Step(1, "backend-engineer", ".github/workflows/ci.yml")], resolution), r => r.Contains("only agent for CI files", StringComparison.Ordinal));
        Assert.Contains(InvalidPlan([Step(1, "data-engineer", "backend/src/Shop.Api/Features/Orders/OrderDto.cs")], resolution), r => r.Contains("not endpoints or DTOs", StringComparison.Ordinal));
        Assert.Empty(InvalidPlan([Step(1, "scaffolder", ".github/workflows/ci.yml", "netlify.toml", "backend/Dockerfile")], resolution, repoEmpty: true));
        Assert.Empty(InvalidPlan([Step(1, "deploy-engineer", "netlify.toml", "backend/Dockerfile")], resolution));
    }

    [Fact]
    public void FR124_One_migration_per_step()
    {
        var resolution = Aspnet();
        var steps = new[]
        {
            Step(1, "data-engineer",
                "backend/src/Shop.Infrastructure/Migrations/20261028120000_AddOrders.cs",
                "backend/src/Shop.Infrastructure/Migrations/20261028120000_AddOrders.Designer.cs",
                "backend/src/Shop.Infrastructure/Migrations/20261028130000_AddLines.cs",
                "backend/src/Shop.Infrastructure/Migrations/20261028130000_AddLines.Designer.cs"),
        };

        Assert.Contains(InvalidPlan(steps, resolution), r => r.Contains("allows at most 1 per step", StringComparison.Ordinal));
        Assert.Empty(InvalidPlan([Step(1, "data-engineer", Migration, "backend/src/Shop.Infrastructure/Migrations/20261028120000_AddOrders.Designer.cs")], resolution));
    }

    [Fact]
    public void FR124_The_scaffolder_runs_step_1_of_an_empty_repository_or_a_stack_change_only()
    {
        var resolution = Aspnet();

        Assert.Contains(InvalidPlan([Step(1, "backend-engineer", Endpoints)], resolution, repoEmpty: true), r => r.Contains("step 1 must be assigned \"scaffolder\"", StringComparison.Ordinal));
        Assert.Empty(InvalidPlan([Step(1, "scaffolder", "README.md")], resolution, repoEmpty: true));
        Assert.Contains(InvalidPlan([Step(1, "backend-engineer", Endpoints), Step(2, "scaffolder", "README.md")], resolution), r => r.Contains("only runs step 1", StringComparison.Ordinal));
        Assert.Contains(InvalidPlan([Step(1, "scaffolder", "README.md")], resolution), r => r.Contains("already has code", StringComparison.Ordinal));

        var change = Aspnet(new StackChange(true, ["backend"]));
        Assert.Empty(InvalidPlan([Step(1, "scaffolder", "README.md"), Step(2, "backend-engineer", Endpoints, Contract)], change));
    }

    // ── FR-117 stack change ──────────────────────────────────────────────

    [Fact]
    public void FR124_A_stack_change_without_a_step_for_the_new_layers_specialist_is_STACK_CHANGE_UNPLANNED()
    {
        var resolution = Resolve(AspnetSpa(local: "none"), new StackChange(true, ["backend", "deploy"]));
        IReadOnlyList<PlanStep> steps = [Step(1, "frontend-engineer", "frontend/src/app.tsx")];

        var issues = CompositionPlanValidator.Errors(steps, resolution, repoEmpty: false);

        var issue = Assert.Single(issues);
        Assert.Equal(ErrorCodes.StackChangeUnplanned, issue.Code);
        Assert.StartsWith("The plan must include work for the backend change", issue.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.StackChangeUnplanned, CompositionPlanValidator.ToError(issues)?.Code);

        // The deploy layer has no deploy-engineer here (no host, no Compose), so only the backend needs a step.
        Assert.DoesNotContain("deploy-engineer", resolution.AgentKeys);
        Assert.Empty(CompositionPlanValidator.Errors([Step(1, "backend-engineer", Endpoints, Contract)], resolution, repoEmpty: false));
        Assert.Empty(CompositionPlanValidator.Errors(steps, Aspnet(new StackChange(false, ["backend"])), repoEmpty: false));
    }

    // ── FR-127 generated files ───────────────────────────────────────────

    [Fact]
    public void FR127_A_backend_step_adding_an_endpoint_must_declare_the_contract_at_or_after_it()
    {
        var resolution = Aspnet();

        var warning = Assert.Single(CompositionPlanValidator.Warnings([Step(1, "backend-engineer", Endpoints)], resolution));
        Assert.Equal(ErrorCodes.UndeclaredGeneratedFile, warning.Code);
        Assert.Contains(Contract, warning.Message, StringComparison.Ordinal);
        Assert.Contains("Step 1", warning.Details[0], StringComparison.Ordinal);

        Assert.Empty(CompositionPlanValidator.Warnings([Step(1, "backend-engineer", Endpoints, Contract)], resolution));
        Assert.Empty(CompositionPlanValidator.Warnings([Step(1, "backend-engineer", Endpoints), Step(2, "backend-engineer", Contract)], resolution));
        Assert.Contains(CompositionPlanValidator.Warnings([Step(1, "backend-engineer", Contract), Step(2, "backend-engineer", Endpoints)], resolution), w => w.Code == ErrorCodes.UndeclaredGeneratedFile);
        Assert.Empty(CompositionPlanValidator.Warnings([Step(1, "backend-engineer", "backend/src/Shop.Domain/Orders/OrderRules.cs")], resolution));
    }

    [Fact]
    public void FR124_AllWarnings_puts_UNCOVERED_REQUIREMENTS_first()
    {
        var resolution = Aspnet();
        SpecRequirement[] requirements = [new("FR-1", 1, "Orders", [RequirementTag.New], true, 3)];

        var warnings = CompositionPlanValidator.AllWarnings([Step(1, "backend-engineer", Endpoints)], requirements, resolution);

        Assert.Equal([ErrorCodes.UncoveredRequirements, ErrorCodes.UndeclaredGeneratedFile], warnings.Select(w => w.Code));
    }

    // ── FR-125 protected paths ───────────────────────────────────────────

    [Fact]
    public void FR125_A_Flyway_migration_on_the_base_commit_is_protected()
    {
        var resolution = Resolve(Sel("fe-angular", "be-spring-boot", "data-jpa-flyway", "vcs-github"));
        const string v2 = "backend/src/main/resources/db/migration/V2__orders.sql";

        Assert.True(resolution.IsProtected(v2, _ => true));
        Assert.False(resolution.IsProtected(v2, _ => false));
        Assert.False(resolution.IsProtected("backend/src/main/resources/db/migration/R__views.sql", _ => true));
        Assert.NotNull(StackPathRules.ImmutableRefusal(resolution, v2, existsOnBase: true));
        Assert.Null(StackPathRules.ImmutableRefusal(resolution, v2, existsOnBase: false));
        Assert.Contains("already exists on the base commit", StackPathRules.GeneratedPlanRefusal(resolution, "data-engineer", v2, existsOnBase: true), StringComparison.Ordinal);
        Assert.Null(StackPathRules.GeneratedPlanRefusal(resolution, "data-engineer", "backend/src/main/resources/db/migration/V3__lines.sql", existsOnBase: false));
        Assert.Contains("couldn't be read", StackPathRules.GeneratedPlanRefusal(resolution, "data-engineer", "backend/src/main/resources/db/migration/V3__lines.sql", existsOnBase: null), StringComparison.Ordinal);
    }

    [Fact]
    public void FR125_Every_data_pack_protects_its_migrations_on_the_base_commit()
    {
        var cases = new (StackSelection Selection, string Path)[]
        {
            (AspnetSpa(), Migration),
            (Sel("none", "be-fastapi", "data-sqlalchemy", "vcs-github"), "backend/alembic/versions/0001_init.py"),
            (Sel("fe-nextjs", "none", "data-prisma", "vcs-github"), "frontend/prisma/migrations/20261028_init/migration.sql"),
            (Sel("fe-nextjs", "none", "data-drizzle", "vcs-github"), "frontend/drizzle/0000_init.sql"),
            (Sel("fe-vue", "none", "data-supabase", "vcs-github"), "supabase/migrations/20261028_init.sql"),
        };

        Assert.All(cases, c => Assert.True(Resolve(c.Selection).IsProtected(c.Path, _ => true), c.Path));
        Assert.False(Aspnet().IsProtected("backend/src/Shop.Infrastructure/Migrations/ShopDbContextModelSnapshot.cs", _ => true));
    }

    [Fact]
    public void FR125_Generated_steps_may_declare_v1_protected_paths_their_specialist_owns_but_never_lockfiles()
    {
        var resolution = Aspnet();

        Assert.Null(StackPathRules.GeneratedPlanRefusal(resolution, "ci-engineer", ".github/workflows/ci.yml", existsOnBase: false));
        Assert.NotNull(StackPathRules.GeneratedPlanRefusal(resolution, "frontend-engineer", ".github/workflows/ci.yml", existsOnBase: false));
        Assert.NotNull(StackPathRules.GeneratedPlanRefusal(resolution, "frontend-engineer", "frontend/package-lock.json", existsOnBase: false));
        Assert.Null(StackPathRules.GeneratedPlanRefusal(resolution, "frontend-engineer", "frontend/src/app.tsx", existsOnBase: false));
    }
}
