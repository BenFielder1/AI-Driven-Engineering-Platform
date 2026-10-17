using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Presets;
using Aide.Domain.Review;
using Aide.Domain.Specs;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Presets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aide.UnitTests.Presets;

/// <summary>
/// FR-74–FR-77, §4.11, §4.12: the preset wiring rules on the shipped presets — prompt guidance, the protected-path
/// ownership exception, the Guardian's preset protected paths, the deterministic preset review rules, the checklist
/// mapping, the specialist prompt and the Fake provider's scripted preset outputs.
/// </summary>
public sealed class PresetWiringTests
{
    private static readonly PresetCatalog Shipped = new([EmbeddedPresetSource.Shipped], NullLogger<PresetCatalog>.Instance);

    private static readonly string[] SpecialistMarkers =
        ["You are the Code Generator agent", "## Stack conventions", "## Your role: Database engineer (db-engineer)", "## Skill: supabase-migration"];

    private static Preset Supabase => Shipped.Find("nextjs-supabase")!;

    private static Preset Frontend => Shipped.Find("nextjs-frontend")!;

    private static PlanStep Step(int number, string agent, params (string Path, FileAction Action)[] files) =>
        new(number, $"Step {number}", "Do it.", [.. files.Select(f => new DeclaredFile(f.Path, f.Action))], number == 1 ? [] : [number - 1], Agent: agent);

    private static Result<IReadOnlyList<PlanStep>> Generated(PlanAgents agents, params PlanStep[] steps) =>
        PlanValidator.Validate(steps, maxFilesPerStep: 20, allowProtectedPaths: false, agents);

    // ---------- §4.11 protected-path ownership exception (Decision 2026-10-17) ----------

    [Fact]
    public void S4_11_A_generated_preset_plan_may_declare_a_CI_workflow_and_env_example_when_the_agent_owns_them()
    {
        var plan = Generated(
            PlanAgents.For(Frontend, repoEmpty: false),
            Step(1, "devops-engineer", (".github/workflows/ci.yml", FileAction.Create), (".env.example", FileAction.Create)));

        Assert.True(plan.IsSuccess, plan.Error?.Message);
    }

    [Fact]
    public void S4_11_A_step_whose_agent_does_not_own_a_CI_workflow_may_not_declare_it()
    {
        var plan = Generated(PlanAgents.For(Frontend), Step(1, "ui-engineer", (".github/workflows/ci.yml", FileAction.Create)));

        Assert.Equal(ErrorCodes.InvalidPlan, plan.Error?.Code);
        Assert.Contains(plan.Error!.Details, d => d.Contains("protected path '.github/workflows/ci.yml'", StringComparison.Ordinal) && d.Contains("devops-engineer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".gitlab-ci.yml")]
    public void S4_11_Lockfiles_env_files_and_other_protected_paths_are_never_declarable_even_by_an_owner(string path)
    {
        var plan = Generated(PlanAgents.For(Frontend), Step(1, "devops-engineer", (path, FileAction.Create)));

        Assert.Equal(ErrorCodes.InvalidPlan, plan.Error?.Code);
        Assert.Contains(plan.Error!.Details, d => d.Contains($"protected path '{path}'", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_11_Without_a_preset_a_generated_plan_still_may_not_declare_a_CI_workflow()
    {
        var plan = Generated(PlanAgents.None, Step(1, "generalist", (".github/workflows/ci.yml", FileAction.Create)));

        Assert.Equal(
            "Step 1 declares the protected path '.github/workflows/ci.yml'; a generated plan may not create or modify protected paths (.git/, .aide/, CI workflows, lockfiles, .env files).",
            Assert.Single(plan.Error!.Details));
    }

    [Fact]
    public void S4_12_A_migration_that_exists_on_the_base_commit_is_immutable_and_a_new_one_is_fine()
    {
        var baseFiles = new HashSet<string>(StringComparer.Ordinal) { "supabase/migrations/0001_init.sql" };
        var agents = PlanAgents.For(Supabase, repoEmpty: false, baseFiles);

        var edited = Generated(agents, Step(1, "db-engineer", ("supabase/migrations/0001_init.sql", FileAction.Modify)));
        var added = Generated(agents, Step(1, "db-engineer", ("supabase/migrations/0002_tasks.sql", FileAction.Create)));

        Assert.Contains(edited.Error!.Details, d => d.Contains("already exists on the base commit", StringComparison.Ordinal));
        Assert.True(added.IsSuccess, added.Error?.Message);
    }

    [Fact]
    public void S4_12_Without_a_base_listing_a_modified_migration_counts_as_existing()
    {
        var plan = Generated(PlanAgents.For(Supabase), Step(1, "db-engineer", ("supabase/migrations/0001_init.sql", FileAction.Modify)));

        Assert.Equal(ErrorCodes.InvalidPlan, plan.Error?.Code);
    }

    [Fact]
    public void S4_12_With_the_base_unknown_a_generated_plan_may_not_create_any_migration()
    {
        var plan = Generated(PlanAgents.For(Supabase), Step(1, "db-engineer", ("supabase/migrations/0002_tasks.sql", FileAction.Create)));

        Assert.Contains(plan.Error!.Details, d => d.Contains("couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_12_A_create_of_a_migration_that_exists_on_base_is_refused_whatever_the_case()
    {
        var baseFiles = new HashSet<string>(StringComparer.Ordinal) { "supabase/migrations/0001_init.sql" };

        var plan = Generated(PlanAgents.For(Supabase, false, baseFiles), Step(1, "db-engineer", ("Supabase/migrations/0001_init.sql", FileAction.Create)));

        Assert.Contains(plan.Error!.Details, d => d.Contains("already exists on the base commit", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_12_An_owner_edited_plan_may_not_declare_a_migration_that_exists_on_base_but_may_add_one()
    {
        var agents = PlanAgents.For(Supabase, false, new HashSet<string>(StringComparer.Ordinal) { "supabase/migrations/0001_init.sql" });

        var edited = PlanValidator.Validate([Step(1, "db-engineer", ("supabase/migrations/0001_init.sql", FileAction.Modify))], 20, allowProtectedPaths: true, agents);
        var added = PlanValidator.Validate([Step(1, "db-engineer", ("supabase/migrations/0002_new.sql", FileAction.Create))], 20, allowProtectedPaths: true, agents);

        Assert.Contains(edited.Error!.Details, d => d.Contains("already exists on the base commit", StringComparison.Ordinal));
        Assert.True(added.IsSuccess, added.Error?.Message);
    }

    [Fact]
    public void S4_11_Preset_protected_paths_match_ignoring_case()
    {
        Assert.True(Supabase.IsProtected("LIB/database.types.ts", _ => false));
        Assert.True(Supabase.IsProtected("Supabase/migrations/0001_init.sql", _ => true));
        var plan = Generated(PlanAgents.For(Supabase), Step(1, "api-engineer", ("LIB/database.types.ts", FileAction.Create)));
        Assert.Contains(plan.Error!.Details, d => d.Contains("protected path 'LIB/database.types.ts'", StringComparison.Ordinal));
        Assert.Single(PresetReviewChecks.RlsOnEveryTable([new SourceFile("Supabase/migrations/0001_init.sql", "create table tasks (id int);")]));
    }

    [Fact]
    public void S4_11_The_Guardian_rejects_a_declared_migration_that_exists_on_the_base_commit()
    {
        var result = ExecutionGuardian.Validate(new GuardianInput(
            1, 1, [new StepState(1, StepStatus.InProgress, [], ["supabase/migrations/0001_init.sql"])], [new ProducedFile("supabase/migrations/0001_init.sql", "select 1;")], 20, Supabase, p => p == "supabase/migrations/0001_init.sql"));

        Assert.Contains(result.Errors, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Message.Contains("immutable", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_12_The_db_engineer_may_declare_the_generated_types_file_it_owns_and_others_may_not()
    {
        var owner = Generated(PlanAgents.For(Supabase), Step(1, "db-engineer", ("lib/database.types.ts", FileAction.Create)));
        var other = Generated(PlanAgents.For(Supabase), Step(1, "api-engineer", ("lib/database.types.ts", FileAction.Create)));

        Assert.True(owner.IsSuccess, owner.Error?.Message);
        Assert.Contains(other.Error!.Details, d => d.Contains("protected path 'lib/database.types.ts'", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_11_An_owner_edited_plan_may_declare_preset_protected_paths()
    {
        var plan = PlanValidator.Validate(
            [Step(1, "ui-engineer", (".github/workflows/ci.yml", FileAction.Create))], 20, allowProtectedPaths: true, PlanAgents.For(Frontend));

        Assert.True(plan.IsSuccess, plan.Error?.Message);
    }

    [Fact]
    public void FR76_Optimisation_plans_allow_generalist_steps_and_skip_the_ordering_rules()
    {
        var plan = PlanValidator.Validate(
            [
                new PlanStep(1, "Fix", "Fix it.", [new DeclaredFile("docs/review/cycle-1-step-1.md", FileAction.Create)], []),
                Step(2, "db-engineer", ("supabase/migrations/0003_fix.sql", FileAction.Create)),
            ],
            20,
            allowProtectedPaths: false,
            PlanAgents.ForOptimization(Supabase, new HashSet<string>(StringComparer.Ordinal)));

        Assert.True(plan.IsSuccess, plan.Error?.Message);
        Assert.Equal(PlanAgents.Generalist, plan.Value[0].Agent);
    }

    [Fact]
    public void FR76_A_plan_step_naming_ml_engineer_under_nextjs_frontend_is_INVALID_PLAN_naming_the_agent()
    {
        var plan = Generated(PlanAgents.For(Frontend), Step(1, "ml-engineer", ("lib/model/train.ts", FileAction.Create)));

        Assert.Equal(ErrorCodes.InvalidPlan, plan.Error?.Code);
        Assert.Contains(plan.Error!.Details, d => d.Contains("'ml-engineer'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "README.md", "LICENSE", ".gitignore" }, true)]
    [InlineData(new[] { "README.md", "package.json" }, false)]
    [InlineData(new[] { "docs/README.md" }, false)]
    public void S4_11_A_repository_with_only_starter_files_counts_as_empty(string[] paths, bool empty) =>
        Assert.Equal(empty, PresetPathRules.IsStarterOnly(paths));

    // ---------- §4.11 Guardian ----------

    private static GuardianResult Guard(Preset preset, string declared, string produced, Func<string, bool>? existsOnBase = null) =>
        ExecutionGuardian.Validate(new GuardianInput(
            1,
            1,
            [new StepState(1, StepStatus.InProgress, [], [declared])],
            [new ProducedFile(declared, "x"), new ProducedFile(produced, "y")],
            20,
            preset,
            existsOnBase));

    [Fact]
    public void S4_11_The_Guardian_rejects_an_undeclared_preset_protected_path()
    {
        var result = Guard(Supabase, "supabase/migrations/0002_tasks.sql", "lib/database.types.ts");

        Assert.Contains(result.Errors, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Path == "lib/database.types.ts");
    }

    [Fact]
    public void S4_11_The_Guardian_protects_existing_migrations_only_when_they_exist_on_the_base_commit()
    {
        var existing = Guard(Supabase, "supabase/migrations/0002_tasks.sql", "supabase/migrations/0001_init.sql", p => p == "supabase/migrations/0001_init.sql");
        var fresh = Guard(Supabase, "supabase/migrations/0002_tasks.sql", "supabase/migrations/0003_other.sql", p => p == "supabase/migrations/0001_init.sql");

        Assert.Contains(existing.Errors, v => v.Code == ErrorCodes.Guardian.ProtectedPath);
        Assert.DoesNotContain(fresh.Errors, v => v.Code == ErrorCodes.Guardian.ProtectedPath);
    }

    [Fact]
    public void S4_11_A_declared_preset_protected_path_is_accepted()
    {
        var result = ExecutionGuardian.Validate(new GuardianInput(
            1, 1, [new StepState(1, StepStatus.InProgress, [], ["lib/database.types.ts"])], [new ProducedFile("lib/database.types.ts", "export type Database = {};")], 20, Supabase));

        Assert.True(result.Accepted);
    }

    // ---------- FR-77 deterministic preset rules ----------

    [Fact]
    public void FR77_A_migration_creating_tasks_without_RLS_is_a_critical_security_finding_costing_0_20()
    {
        var files = new[] { new SourceFile("supabase/migrations/0002_tasks.sql", "create table public.tasks (id uuid primary key);") };

        var finding = Assert.Single(PresetReviewChecks.Run(Supabase, files));
        var scored = ComplianceScoring.Score(Enum.GetValues<ReviewCategory>().ToDictionary(c => c, _ => 1m), [finding]);

        Assert.Equal((ReviewCategory.Security, Severity.Critical, "supabase/migrations/0002_tasks.sql"), (finding.Category, finding.Severity, finding.Path));
        Assert.Contains("'tasks'", finding.Message, StringComparison.Ordinal);
        Assert.Equal(0.80m, scored.CategoryScores[ReviewCategory.Security]);
    }

    [Theory]
    [InlineData("create table tasks (id int);\nalter table public.tasks enable row level security;")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"tasks\" (id int);\nALTER TABLE tasks ENABLE ROW LEVEL SECURITY;")]
    [InlineData("-- create table forgotten (id int);\ncreate temporary table scratch (id int);")]
    public void FR77_Tables_with_RLS_enabled_commented_and_temporary_tables_pass(string sql) =>
        Assert.Empty(PresetReviewChecks.RlsOnEveryTable([new SourceFile("supabase/migrations/0001_init.sql", sql)]));

    [Fact]
    public void FR77_RLS_enabled_in_a_later_migration_counts_and_sql_outside_migrations_is_ignored()
    {
        var files = new[]
        {
            new SourceFile("supabase/migrations/0001_tasks.sql", "create table tasks (id int);"),
            new SourceFile("supabase/migrations/0002_rls.sql", "alter table tasks enable row level security;"),
            new SourceFile("docs/schema.sql", "create table notes (id int);"),
        };

        Assert.Empty(PresetReviewChecks.RlsOnEveryTable(files));
    }

    [Fact]
    public void FR77_The_service_role_key_or_a_server_env_name_in_a_use_client_file_is_critical()
    {
        var files = new[]
        {
            new SourceFile("components/Admin.tsx", "// admin\n'use client';\nconst k = process.env.SUPABASE_SERVICE_ROLE_KEY;\nconst u = process.env.NEXT_PUBLIC_SUPABASE_URL;\nconst s = process.env['STRIPE_SECRET'];\nconst m = process.env.NODE_ENV;"),
            new SourceFile("app/api/route.ts", "const k = process.env.SUPABASE_SERVICE_ROLE_KEY;"),
        };

        var findings = PresetReviewChecks.Run(Frontend, files);

        Assert.Equal(["SUPABASE_SERVICE_ROLE_KEY", "STRIPE_SECRET"], findings.Select(f => f.Message.Contains(PresetReviewChecks.ServiceRoleKey, StringComparison.Ordinal) ? PresetReviewChecks.ServiceRoleKey : "STRIPE_SECRET"));
        Assert.All(findings, f => Assert.Equal((ReviewCategory.Security, Severity.Critical, "components/Admin.tsx"), (f.Category, f.Severity, f.Path)));
    }

    [Theory]
    [InlineData("\"use client\"\nexport {}", true)]
    [InlineData("/* header */\n  'use client'", true)]
    [InlineData("import x from 'y';\n'use client'", false)]
    public void FR77_A_client_file_starts_with_the_use_client_directive(string content, bool client) =>
        Assert.Equal(client, PresetReviewChecks.IsClientFile(content));

    [Fact]
    public void FR77_The_frontend_preset_has_no_RLS_rule_and_no_preset_runs_nothing()
    {
        var files = new[] { new SourceFile("supabase/migrations/0002_tasks.sql", "create table tasks (id int);") };

        Assert.Empty(PresetReviewChecks.Run(Frontend, files));
        Assert.Empty(PresetReviewChecks.Run(null, files));
    }

    [Fact]
    public void FR77_Checklist_violations_take_the_item_category_and_severity_and_stay_agent_violations()
    {
        var violations = new[]
        {
            new ReviewFinding(ReviewCategory.Testing, Severity.Minor, "[C1] tasks has no RLS policy.", Source: FindingSource.Agent),
            new ReviewFinding(ReviewCategory.Testing, Severity.Minor, "Some edge cases are untested.", Source: FindingSource.Agent),
        };

        var mapped = PresetReviewChecklist.Apply(violations, Supabase);

        Assert.Equal((ReviewCategory.Security, Severity.Critical, FindingSource.Agent), (mapped[0].Category, mapped[0].Severity, mapped[0].Source));
        Assert.Equal(violations[1], mapped[1]);
        Assert.Contains("[C1] (security, critical)", PresetGuidance.Review(Supabase), StringComparison.Ordinal);
    }

    // ---------- FR-75 spec ----------

    [Fact]
    public void FR75_Spec_guidance_has_the_stack_sections_with_guidance_and_conventions()
    {
        var guidance = PresetGuidance.Spec(Supabase)!;

        Assert.Contains("`nextjs-supabase@", guidance, StringComparison.Ordinal);
        Assert.Contains("#### Data Model & RLS Policies\n\nFor each table", guidance, StringComparison.Ordinal);
        Assert.Contains("### Stack conventions", guidance, StringComparison.Ordinal);
        Assert.Null(PresetGuidance.Spec(null));
    }

    [Fact]
    public void FR75_A_nextjs_supabase_spec_without_Data_Model_and_RLS_Policies_fails_naming_it()
    {
        var context = SpecValidationContext.ForVersion(false, null, Supabase.SpecSectionTitles);
        var spec = string.Join("\n\n", SpecSections.Required.Select((t, i) => t == "Functional Requirements"
                ? $"## {i + 1}. {t}\n- **FR-1** Tasks. Given a user, when they add a task, then it is saved."
                : $"## {i + 1}. {t}\nText."))
            + string.Concat(Supabase.SpecSectionTitles.Where(t => t != "Data Model & RLS Policies").Select(t => $"\n\n## {t}\nText."));

        var result = SpecValidator.Check(spec, context);

        Assert.Equal(ErrorCodes.MissingSections, result.Error?.Code);
        Assert.Contains("Data Model & RLS Policies", SpecValidator.CorrectiveFeedback(result.Error!, context), StringComparison.Ordinal);
    }

    // ---------- FR-76 plan guidance, specialist prompt ----------

    [Fact]
    public void FR76_Plan_guidance_lists_the_catalogue_and_rules()
    {
        var guidance = PresetGuidance.Plan(Supabase, repoEmpty: true)!;

        Assert.Contains("- `db-engineer` (Database engineer):", guidance, StringComparison.Ordinal);
        Assert.Contains("`supabase/migrations/**`", guidance, StringComparison.Ordinal);
        Assert.Contains("This repository is empty, so this applies to this plan.", guidance, StringComparison.Ordinal);
        Assert.Contains("At most 1 file(s) matching `supabase/migrations/**` per step.", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void FR76_A_db_engineer_step_prompt_is_the_base_contract_then_conventions_role_and_the_supabase_migration_skill()
    {
        var template = new EmbeddedPromptLibrary().Template(AgentKind.CodeGenerator);
        var rendered = template.Render(template.RequiredInputs.ToDictionary(n => n, n => (string?)n, StringComparer.Ordinal)).Value;

        var prompt = SpecialistPrompt.Compose(rendered, Supabase, "db-engineer").Value;

        var order = SpecialistMarkers
            .Select(s => prompt.System.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order(), order);
        Assert.Equal(rendered.User, prompt.User);
    }

    // ---------- §8 Fake scripted specialist outputs ----------

    private static LlmRequest Rendered(AgentKind agent, Dictionary<string, string?> values)
    {
        var rendered = new EmbeddedPromptLibrary().Template(agent).Render(values).Value;
        return new LlmRequest { Model = "fake-1", System = rendered.System, Messages = [new LlmMessage(LlmRole.User, rendered.User)], Agent = agent };
    }

    [Theory]
    [InlineData("nextjs-frontend")]
    [InlineData("nextjs-supabase")]
    public void FR76_The_Fake_plan_for_a_shipped_preset_is_valid_without_warnings(string presetId)
    {
        var preset = Shipped.Find(presetId)!;
        var reply = new FakePlanGeneratorResponder().Respond(Rendered(AgentKind.PlanGenerator, new()
        {
            ["PROJECT_REQUEST"] = "# Demo: Recipe Finder\n\n## Summary\nRecipes.",
            ["TECHNICAL_SPECIFICATION"] = "## 2. Functional Requirements\n- **FR-1** x. Given a, when b, then c.",
            ["MAX_FILES_PER_STEP"] = "20",
            ["PRESET_GUIDANCE"] = PresetGuidance.Plan(preset, repoEmpty: true),
        }));

        var agents = PlanAgents.For(preset, repoEmpty: true, new HashSet<string>(StringComparer.Ordinal));
        var plan = PlanDocument.ParseAgentOutput(reply, 20, agents);

        Assert.True(plan.IsSuccess, plan.Error is null ? null : string.Join(" | ", plan.Error.Details));
        Assert.Equal("project-scaffolder", plan.Value[0].Agent);
        Assert.Equal("devops-engineer", plan.Value[^1].Agent);
        Assert.Empty(PresetPlanValidator.Warnings(plan.Value, preset));
        Assert.True(PlanDocument.RenderChecked(Domain.Artifacts.ArtifactType.ImplementationPlan, plan.Value, 20, agents).IsSuccess);
    }

    [Fact]
    public void FR76_The_Fake_scaffolder_writes_package_json_layout_vercel_json_with_aide_disabled_and_nvmrc()
    {
        var template = new EmbeddedPromptLibrary().Template(AgentKind.CodeGenerator);
        var values = template.RequiredInputs.ToDictionary(n => n, n => (string?)n, StringComparer.Ordinal);
        values["STEP_NUMBER"] = "1";
        values["PROJECT_REQUEST"] = "# Demo: Recipe Finder";
        values["DECLARED_FILES"] = "- package.json (create)\n- app/layout.tsx (create)\n- vercel.json (create)\n- .nvmrc (create)";
        var rendered = SpecialistPrompt.Compose(template.Render(values).Value, Frontend, "project-scaffolder").Value;

        var reply = new FakeCodeGeneratorResponder().Respond(new LlmRequest
        {
            Model = "fake-1",
            System = rendered.System,
            Messages = [new LlmMessage(LlmRole.User, rendered.User)],
            Agent = AgentKind.CodeGenerator,
        });

        var files = CodeGeneration.Parse(reply).Value.Files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        Assert.Equal(["package.json", "app/layout.tsx", "vercel.json", ".nvmrc"], files.Keys);
        using var vercel = JsonDocument.Parse(files["vercel.json"]);
        Assert.False(vercel.RootElement.GetProperty("git").GetProperty("deploymentEnabled").GetProperty("aide/**").GetBoolean());
        using var package = JsonDocument.Parse(files["package.json"]);
        Assert.Equal("next build", package.RootElement.GetProperty("scripts").GetProperty("build").GetString());
        Assert.Equal("22\n", files[".nvmrc"]);
        Assert.Contains("RootLayout", files["app/layout.tsx"], StringComparison.Ordinal);
    }

    [Fact]
    public void FR77_The_Fake_db_engineer_migration_enables_RLS()
    {
        var sql = FakePresetScripts.Content("db-engineer", "supabase/migrations/0001_recipes.sql", "Demo", 2, "Recipes")!;

        Assert.Empty(PresetReviewChecks.RlsOnEveryTable([new SourceFile("supabase/migrations/0001_recipes.sql", sql)]));
        Assert.Contains("create table if not exists public.recipes", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FR75_The_Fake_spec_adds_the_preset_sections()
    {
        var reply = new FakeSpecGeneratorResponder().Respond(Rendered(AgentKind.SpecGenerator, new()
        {
            ["PROJECT_REQUEST"] = "# Demo: Recipe Finder\n\n## Summary\nRecipes.",
            ["PRESET_GUIDANCE"] = PresetGuidance.Spec(Frontend),
        }));

        Assert.True(SpecValidator.Check(reply, SpecValidationContext.ForVersion(false, null, Frontend.SpecSectionTitles)).IsSuccess);
    }
}
