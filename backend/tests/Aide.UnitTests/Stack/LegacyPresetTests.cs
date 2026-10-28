using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Review;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-129, §4.23: the frozen v2 preset data (<c>legacy/presets.json</c>) — the agent-key map at plan and run time, the
/// frozen owned globs for migrated in-flight plans, and history labels — plus the v2 preset rules that moved to the
/// composition path (RLS check, client files, starter-only repositories, ownable v1-protected paths).
/// </summary>
public sealed class LegacyPresetTests
{
    private static LegacyPresets Legacy => Catalog.Legacy;

    /// <summary>§4.23: the migrated <c>nextjs-supabase</c> composition (fe-nextjs at ".", Supabase BaaS, GitHub, Vercel).</summary>
    private static StackResolution MigratedSupabase() => StackResolverTests.Resolve(new StackSelection
    {
        Frontend = new StackComponentPick("fe-nextjs", "."),
        Backend = new StackComponentPick(StackIds.None),
        Data = new StackDataPick("data-supabase"),
        Vcs = new StackVcsPick(StackPinning.GitHubVcs),
        Deploy = new StackDeployPick("deploy-vercel", StackIds.None, "supabase", StackIds.None),
        OriginModel = "same-origin",
    });

    private static PlanAgents MigratedAgents(StackResolution stack, string presetId = "nextjs-supabase") =>
        PlanAgents.ForStack(stack, repoEmpty: false) with { Legacy = new LegacyPlanAgents(Legacy, Legacy.Find(presetId)) };

    private static PlanStep Step(int number, string agent, params string[] files) =>
        new(number, $"Step {number}", "Do it.", [.. files.Select(f => new DeclaredFile(f, FileAction.Create))], number == 1 ? [] : [number - 1], Agent: agent);

    // ---------- the shipped legacy data ----------

    [Fact]
    public void FR129_The_shipped_catalog_has_the_frozen_v2_presets_and_the_4_23_agent_key_table()
    {
        Assert.Equal("1.1.0", Catalog.Version);
        Assert.Equal(
            ["project-scaffolder", "db-engineer", "domain-logic-engineer", "api-engineer", "ui-engineer", "test-engineer", "devops-engineer"],
            Legacy.AgentKeys.Select(r => r.From));
        Assert.Equal(("Next.js + Supabase", "1.0.1"), (Legacy.Find("nextjs-supabase")!.Name, Legacy.Find("nextjs-supabase")!.Version));
        Assert.Equal(("Next.js frontend", "1.0.0"), (Legacy.Find("nextjs-frontend")!.Name, Legacy.Find("nextjs-frontend")!.Version));
        Assert.Null(Legacy.Find("rails-postgres"));
        Assert.True(Legacy.Find("nextjs-supabase")!.OwnsOf("db-engineer")!.IsMatch("supabase/migrations/0002_tasks.sql"));
        Assert.Null(Legacy.Find("nextjs-frontend")!.OwnsOf("db-engineer"));
    }

    [Theory]
    [InlineData("project-scaffolder", "scaffolder")]
    [InlineData("db-engineer", "data-engineer")]
    [InlineData("domain-logic-engineer", "frontend-engineer")]
    [InlineData("api-engineer", "frontend-engineer")]
    [InlineData("ui-engineer", "frontend-engineer")]
    [InlineData("test-engineer", "test-engineer")]
    public void S4_23_v2_agent_keys_map_to_v4_specialists(string v2, string v4)
    {
        var mapped = Legacy.Map(v2, ["app/page.tsx"])!;

        Assert.Equal((v4, v2, (string?)null), (mapped.AgentKey, mapped.LegacyAgentKey, mapped.Warning));
    }

    [Fact]
    public void S4_23_devops_engineer_is_ci_engineer_for_github_files_else_deploy_engineer_and_deploy_engineer_with_a_warning_for_both()
    {
        Assert.Equal("ci-engineer", Legacy.Map("devops-engineer", [".github/workflows/ci.yml", ".github/dependabot.yml"])!.AgentKey);
        Assert.Equal("deploy-engineer", Legacy.Map("devops-engineer", ["vercel.json", ".env.example"])!.AgentKey);

        var both = Legacy.Map("devops-engineer", [".github/workflows/ci.yml", "vercel.json"])!;
        Assert.Equal("deploy-engineer", both.AgentKey);
        Assert.Contains(".github/**", both.Warning, StringComparison.Ordinal);
        Assert.Null(Legacy.Map("frontend-engineer", ["app/page.tsx"]));
        Assert.Null(Legacy.Map(null, []));
    }

    [Fact]
    public void S4_23_At_run_time_a_mapped_specialist_missing_from_the_stack_falls_back_to_the_rules_other_one_then_generalist()
    {
        var mapped = Legacy.MapInPlay("devops-engineer", ["vercel.json"], ["frontend-engineer", "ci-engineer"])!;
        var none = Legacy.MapInPlay("db-engineer", ["supabase/migrations/0002.sql"], ["frontend-engineer"])!;
        var inPlay = Legacy.MapInPlay("db-engineer", ["supabase/migrations/0002.sql"], ["data-engineer"])!;

        Assert.Equal("ci-engineer", mapped.AgentKey);
        Assert.Contains("deploy-engineer", mapped.Warning, StringComparison.Ordinal);
        Assert.Equal(PlanAgents.Generalist, none.AgentKey);
        Assert.NotNull(none.Warning);
        Assert.Equal(("data-engineer", (string?)null), (inPlay.AgentKey, inPlay.Warning));
    }

    [Fact]
    public void FR129_A_migrated_nextjs_supabase_db_engineer_step_runs_as_data_engineer_with_the_data_supabase_skill()
    {
        var stack = MigratedSupabase();
        var mapped = Legacy.MapInPlay("db-engineer", ["supabase/migrations/0004_tags.sql"], stack.AgentKeys)!;

        var specialist = stack.Specialist(mapped.AgentKey)!;
        var prompt = StackPrompt.Compose(new Domain.Agents.RenderedPrompt("You are the Code Generator agent.", "Step 4"), stack, mapped.AgentKey);

        Assert.Equal(("data-engineer", "db-engineer"), (mapped.AgentKey, mapped.LegacyAgentKey));
        Assert.Contains(specialist.Packs, p => p.Id == "data-supabase");
        Assert.Contains("## Your role: Data engineer (data-engineer)", prompt.Value.System, StringComparison.Ordinal);
    }

    [Fact]
    public void FR129_The_history_label_names_the_retired_preset_and_its_version()
    {
        Assert.Equal("Preset nextjs-supabase@1.0.1 (retired, migrated)", LegacyPreset.HistoryLabel("nextjs-supabase", "1.0.1"));
        Assert.Equal("Preset nextjs-frontend (retired, migrated)", LegacyPreset.HistoryLabel("nextjs-frontend", null));
    }

    // ---------- plans of migrated versions ----------

    [Fact]
    public void S4_23_A_migrated_plan_naming_v2_keys_validates_and_keeps_the_v2_key_on_each_step()
    {
        var stack = MigratedSupabase();
        var plan = PlanValidator.Validate(
            [Step(1, "db-engineer", "supabase/migrations/0002_tasks.sql"), Step(2, "api-engineer", "app/api/tasks/route.ts"), Step(3, "test-engineer", "e2e/tasks.spec.ts")],
            maxFilesPerStep: 20,
            allowProtectedPaths: true,
            MigratedAgents(stack));

        Assert.True(plan.IsSuccess, plan.Error?.Message + " " + string.Join(" | ", plan.Error?.Details ?? []));
        Assert.Equal(
            [("data-engineer", "db-engineer"), ("frontend-engineer", "api-engineer"), ("test-engineer", "test-engineer")],
            plan.Value.Select(s => (s.Agent!, s.LegacyAgent!)));
    }

    [Fact]
    public void S4_23_Without_the_legacy_map_v2_keys_are_unknown_agents_and_a_new_plan_of_a_migrated_version_keeps_v4_keys()
    {
        var stack = MigratedSupabase();
        var unknown = PlanValidator.Validate([Step(1, "db-engineer", "supabase/migrations/0002_tasks.sql")], 20, true, PlanAgents.ForStack(stack));
        var v4 = PlanValidator.Validate([Step(1, "test-engineer", "e2e/tasks.spec.ts")], 20, true, MigratedAgents(stack));

        Assert.Equal(ErrorCodes.InvalidPlan, unknown.Error?.Code);
        Assert.Null(Assert.Single(v4.Value).LegacyAgent);
    }

    [Fact]
    public void S4_23_A_migrated_plans_owned_path_checks_use_the_frozen_v2_globs()
    {
        var stack = MigratedSupabase();
        var legacy = Legacy.Find("nextjs-supabase");
        var steps = PlanValidator.Validate(
            [Step(1, "db-engineer", "supabase/migrations/0002_tasks.sql", "lib/database.types.ts"), Step(2, "ui-engineer", "app/tasks/page.tsx", "app/api/tasks/route.ts")],
            20,
            true,
            MigratedAgents(stack)).Value;

        var warning = Assert.Single(CompositionPlanValidator.Warnings(steps, stack, legacy), w => w.Code == ErrorCodes.AgentScope);

        var detail = Assert.Single(warning.Details);
        Assert.StartsWith("Step 2 (ui-engineer) declares 'app/api/tasks/route.ts' outside its owned paths (app/**, !app/api/**", detail, StringComparison.Ordinal);
    }

    // ---------- loader checks ----------

    [Fact]
    public void FR122_A_legacy_file_naming_an_unknown_specialist_or_an_unmapped_owner_fails_the_catalog()
    {
        var files = ShippedFiles();
        files[StackCatalogLoader.LegacyFile] = """
            { "agentKeys": [{ "from": "db-engineer", "to": "dba" }],
              "presets": [{ "id": "nextjs-supabase", "name": "Next.js + Supabase", "version": "1.0.1", "owns": { "ui-engineer": ["app/**"] } }] }
            """;

        var result = StackCatalogLoader.Load(files);

        Assert.Null(result.Catalog);
        Assert.Contains(result.Errors, e => e.Message.Contains("'dba' isn't a specialist key", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Message.Contains("'ui-engineer' has no agentKeys row", StringComparison.Ordinal));
    }

    [Fact]
    public void FR122_A_catalog_without_a_legacy_file_has_no_legacy_data()
    {
        var files = ShippedFiles();
        files.Remove(StackCatalogLoader.LegacyFile);

        var result = StackCatalogLoader.Load(files);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Catalog!.Legacy.Presets);
    }

    // ---------- v2 rules kept on the composition path ----------

    [Fact]
    public void FR126_A_migration_creating_tasks_without_RLS_is_a_critical_security_finding()
    {
        var finding = Assert.Single(StackReviewChecks.RlsOnEveryTableFindings([new SourceFile("Supabase/migrations/0002_tasks.sql", "create table public.tasks (id uuid primary key);")]));

        Assert.Equal((ReviewCategory.Security, Severity.Critical), (finding.Category, finding.Severity));
        Assert.Contains("'tasks'", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create table tasks (id int);\nalter table public.tasks enable row level security;")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"tasks\" (id int);\nALTER TABLE tasks ENABLE ROW LEVEL SECURITY;")]
    [InlineData("-- create table forgotten (id int);\ncreate temporary table scratch (id int);")]
    public void FR126_Tables_with_RLS_enabled_commented_and_temporary_tables_pass(string sql) =>
        Assert.Empty(StackReviewChecks.RlsOnEveryTableFindings([new SourceFile("supabase/migrations/0001_init.sql", sql)]));

    [Fact]
    public void FR126_RLS_enabled_in_a_later_migration_counts_and_sql_outside_migrations_is_ignored() =>
        Assert.Empty(StackReviewChecks.RlsOnEveryTableFindings(
        [
            new SourceFile("supabase/migrations/0001_tasks.sql", "create table tasks (id int);"),
            new SourceFile("supabase/migrations/0002_rls.sql", "alter table tasks enable row level security;"),
            new SourceFile("docs/schema.sql", "create table notes (id int);"),
        ]));

    [Theory]
    [InlineData("\"use client\"\nexport {}", true)]
    [InlineData("/* header */\n  'use client'", true)]
    [InlineData("﻿// c\n'use client'", true)]
    [InlineData("import x from 'y';\n'use client'", false)]
    public void FR126_A_client_file_starts_with_the_use_client_directive(string content, bool client) =>
        Assert.Equal(client, StackReviewChecks.IsClientFile(content));

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "README.md", "LICENSE", ".gitignore" }, true)]
    [InlineData(new[] { "README.md", "package.json" }, false)]
    [InlineData(new[] { "docs/README.md" }, false)]
    public void FR124_A_repository_with_only_starter_files_counts_as_empty(string[] paths, bool empty) =>
        Assert.Equal(empty, StackPathRules.IsStarterOnly(paths));

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("frontend/.env.example", true)]
    [InlineData(".gitlab-ci.yml", true)]
    [InlineData("package-lock.json", false)]
    [InlineData(".env", false)]
    public void FR125_The_v1_protected_paths_an_owning_specialist_may_declare(string path, bool ownable) =>
        Assert.Equal(ownable, StackPathRules.IsOwnableV1Protected(path));

    [Fact]
    public void FR124_Without_a_stack_a_generated_plan_still_may_not_declare_a_CI_workflow()
    {
        var plan = PlanValidator.Validate([Step(1, "generalist", ".github/workflows/ci.yml")], 20, allowProtectedPaths: false, PlanAgents.None);

        Assert.Equal(
            "Step 1 declares the protected path '.github/workflows/ci.yml'; a generated plan may not create or modify protected paths (.git/, .aide/, CI workflows, lockfiles, .env files).",
            Assert.Single(plan.Error!.Details));
    }

    [Fact]
    public void FR124_Without_a_stack_a_step_naming_a_specialist_is_invalid()
    {
        var plan = PlanValidator.Validate([Step(1, "data-engineer", "src/a.js")], 20, true, PlanAgents.None);

        Assert.Contains("without a stack every step is \"generalist\"", Assert.Single(plan.Error!.Details), StringComparison.Ordinal);
    }
}
