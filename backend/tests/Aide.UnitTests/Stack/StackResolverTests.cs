using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Specs;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-120, FR-121, FR-123, FR-127, §4.18, §4.22, PROGRESS "M26 inputs": resolving a composition into specialists, skills,
/// spec sections, env names, gates and the composition summary, on the shipped catalog.
/// </summary>
public class StackResolverTests
{
    private static readonly string[] PromptOrder =
    [
        "BASE PROMPT", "## Stack composition", "## Your role: Frontend engineer (frontend-engineer)", "You are the **frontend engineer**",
        "## Skill: fe-react-vite — Detect", "## Skill: stack-api-contract — Conventions",
    ];

    internal static StackResolution Resolve(StackSelection selection, StackChange? change = null, StackComposition? baseline = null)
    {
        var result = StackResolver.Resolve(Valid(selection), Catalog, change, baseline);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    internal static StackSelection AspnetSpa(string? frontendHost = null, string? backendHost = null, string? databaseHost = null, string? local = null) =>
        Sel("fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", frontendHost, backendHost, databaseHost, local);

    // ── FR-120 roster ────────────────────────────────────────────────────

    [Fact]
    public void FR120_No_backend_with_Supabase_BaaS_has_every_specialist_but_the_backend_engineer()
    {
        var resolution = Resolve(Sel("fe-react-vite", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel"));

        Assert.Equal(
            ["scaffolder", "data-engineer", "frontend-engineer", "test-engineer", "ci-engineer", "deploy-engineer", "docs-writer"],
            resolution.AgentKeys);
    }

    [Fact]
    public void FR120_The_specialists_follow_the_layers_and_never_include_the_generalist()
    {
        var apiOnly = Resolve(Sel("none", "be-fastapi", "none", "vcs-github", local: "none"));
        Assert.Equal(["scaffolder", "backend-engineer", "test-engineer", "ci-engineer", "docs-writer"], apiOnly.AgentKeys);

        var full = Resolve(AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));
        Assert.Equal(StackAgents.All, full.AgentKeys);
        Assert.All(full.Specialists, s =>
        {
            Assert.NotEqual("generalist", s.Key);
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.NotEmpty(s.OwnedGlobs);
            Assert.False(string.IsNullOrWhiteSpace(s.Prompt));
        });
    }

    [Fact]
    public void FR120_Owned_paths_are_expanded_from_the_layer_paths()
    {
        var resolution = Resolve(AspnetSpa());

        Assert.Equal(["**"], resolution.Specialist("scaffolder")!.OwnedGlobs);
        var backend = resolution.Specialist("backend-engineer")!;
        Assert.True(backend.Owns.IsMatch("backend/src/Shop.Api/Features/Orders/OrderEndpoints.cs"));
        Assert.True(backend.Owns.IsMatch("backend/openapi/api.json"));
        Assert.False(backend.Owns.IsMatch("backend/src/Shop.Infrastructure/Migrations/20261028_Init.cs"));
        Assert.False(backend.Owns.IsMatch("backend/Dockerfile"));
        Assert.True(resolution.Specialist("data-engineer")!.Owns.IsMatch("backend/src/Shop.Infrastructure/Migrations/20261028_Init.cs"));
        var tests = resolution.Specialist("test-engineer")!;
        Assert.True(tests.Owns.IsMatch("frontend/e2e/orders.spec.ts"));
        Assert.True(tests.Owns.IsMatch("backend/tests/Shop.IntegrationTests/OrdersTests.cs"));
        var docs = resolution.Specialist("docs-writer")!;
        Assert.True(docs.Owns.IsMatch("docs/architecture.md"));
        Assert.False(docs.Owns.IsMatch("docs/pr/1.md"));
        Assert.DoesNotContain(resolution.Specialists.SelectMany(s => s.OwnedGlobs), g => g.Contains('{', StringComparison.Ordinal));
    }

    [Fact]
    public void FR119_A_frontend_at_the_repo_root_owns_from_the_root()
    {
        var resolution = Resolve(new StackSelection
        {
            Frontend = new StackComponentPick("fe-nextjs", "."),
            Backend = new StackComponentPick("none"),
            Data = new StackDataPick("none"),
            Vcs = new StackVcsPick("vcs-github"),
        });

        var frontend = resolution.Specialist("frontend-engineer")!;
        Assert.Equal("**", frontend.OwnedGlobs[0]);
        Assert.Contains("src/app/api/**", frontend.OwnedGlobs);
        Assert.Contains("!e2e/**", frontend.OwnedGlobs);
        Assert.True(frontend.Owns.IsMatch("src/app/page.tsx"));
        Assert.False(frontend.Owns.IsMatch("e2e/home.spec.ts"));
        Assert.Contains("cd . && npm ci && npm run typecheck && npm run lint && npm test && npm run build", resolution.Gates);
    }

    // ── placeholders ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("{fe}/src/**", "web", null, "web/src/**")]
    [InlineData("!{fe}/e2e/**", "web", null, "!web/e2e/**")]
    [InlineData("{fe}/src/**", ".", null, "src/**")]
    [InlineData("{be}/openapi/api.json", "web", null, null)]
    [InlineData("{app}/prisma/**", "web", null, "web/prisma/**")]
    [InlineData("{app}/prisma/**", "web", "api", "api/prisma/**")]
    [InlineData("supabase/**", null, null, "supabase/**")]
    public void M26_Globs_expand_fe_be_and_app_and_a_missing_layer_drops_them(string glob, string? frontend, string? backend, string? expected) =>
        Assert.Equal(expected, StackPlaceholders.ExpandGlob(glob, new StackLayerPaths(frontend, backend)));

    [Fact]
    public void M26_Commands_and_prose_expand_and_missing_layers_drop_or_say_so()
    {
        var apiOnly = new StackLayerPaths(null, "api");
        Assert.Equal("cd api && dotnet test", StackPlaceholders.ExpandCommand("cd {be} && dotnet test", apiOnly));
        Assert.Null(StackPlaceholders.ExpandCommand("cd {fe} && npm test", apiOnly));
        Assert.Equal("`(no frontend)/**` and `api/**`", StackPlaceholders.ExpandProse("`{fe}/**` and `{be}/**`", apiOnly));
        Assert.Equal("`**` is yours", StackPlaceholders.ExpandProse("`{fe}/**` is yours", new StackLayerPaths(".", null)));
    }

    [Fact]
    public void M26_Rules_gates_and_generated_files_of_a_missing_layer_are_dropped()
    {
        var noBackend = Resolve(Sel("fe-react-vite", "none", "data-supabase", "vcs-github"));
        Assert.DoesNotContain(noBackend.PlanRules.Order, r => r.Before.Patterns.Concat(r.After.Patterns).Any(p => p.Contains("openapi", StringComparison.Ordinal)));
        Assert.DoesNotContain(noBackend.GeneratedFiles, g => g.Glob == "frontend/src/api/generated/**");
        Assert.Empty(noBackend.ContractFiles);
        Assert.All(noBackend.Gates, g => Assert.DoesNotContain("{", g, StringComparison.Ordinal));

        var withBackend = Resolve(AspnetSpa());
        Assert.Contains(withBackend.GeneratedFiles, g => g.Glob == "frontend/src/api/generated/**" && g.CiCheck == "cd frontend && npm run gen:api && git diff --exit-code src/api/generated");
        Assert.Contains(withBackend.GeneratedFiles, g => g.Glob == "frontend/package-lock.json" && g.Handling == "ci_generated");
        Assert.Equal(["backend/openapi/api.json"], withBackend.ContractFiles);
        Assert.Contains("cd backend && dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes", withBackend.Gates);
        Assert.Equal("/app/efbundle", withBackend.MigrateCommand);
    }

    // ── FR-121 skills ────────────────────────────────────────────────────

    [Fact]
    public void FR121_A_FastAPI_backend_engineer_loads_be_fastapi_the_contract_skill_and_SQLAlchemy_Setup_and_Modelling()
    {
        var resolution = Resolve(Sel("none", "be-fastapi", "data-sqlalchemy", "vcs-github"));
        var backend = resolution.Specialist("backend-engineer")!;

        Assert.Equal(["be-fastapi@1.0.0+aide.1", "stack-api-contract@1.0.0+aide.1", "data-sqlalchemy@1.0.0+aide.1"], backend.Packs.Select(p => p.Label));
        Assert.Equal(Catalog.Packs["be-fastapi"].Sections, backend.Packs[0].Sections);
        Assert.Equal(["Setup", "Modelling"], backend.Packs[2].Sections);

        var prompt = Assert.IsType<RenderedPrompt>(StackPrompt.Compose(new RenderedPrompt("BASE", "STEP"), resolution, "backend-engineer").Value);
        Assert.Contains("## Skill: be-fastapi — Contract export", prompt.System, StringComparison.Ordinal);
        Assert.Contains("## Skill: stack-api-contract — Breaking-change rules", prompt.System, StringComparison.Ordinal);
        Assert.Contains("## Skill: data-sqlalchemy — Setup", prompt.System, StringComparison.Ordinal);
        Assert.Contains("## Skill: data-sqlalchemy — Modelling", prompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("## Skill: data-sqlalchemy — Migrations", prompt.System, StringComparison.Ordinal);
        Assert.Equal("STEP", prompt.User);
    }

    [Fact]
    public void FR121_Switching_the_backend_to_ASPNET_before_planning_loads_be_aspnet_instead()
    {
        var resolution = Resolve(Sel("none", "be-aspnet", "data-efcore-postgres", "vcs-github"));
        var backend = resolution.Specialist("backend-engineer")!;

        Assert.Equal(["be-aspnet@1.0.0+aide.1", "stack-api-contract@1.0.0+aide.1", "data-efcore-postgres@1.0.0+aide.1"], backend.Packs.Select(p => p.Label));
        var system = StackPrompt.ComposeSystem("BASE", resolution, backend);
        Assert.Contains("## Skill: be-aspnet — Contract export", system, StringComparison.Ordinal);
        Assert.DoesNotContain("be-fastapi", system, StringComparison.Ordinal);
    }

    [Fact]
    public void FR121_The_prompt_is_base_then_summary_then_role_then_skills_with_the_user_message_unchanged()
    {
        var resolution = Resolve(AspnetSpa());
        var system = StackPrompt.ComposeSystem("BASE PROMPT", resolution, resolution.Specialist("frontend-engineer")!);

        var positions = PromptOrder
            .Select(s => system.IndexOf(s, StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("You own these paths: `frontend/**`", system, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", system, StringComparison.Ordinal);
        Assert.DoesNotContain("{fe}", system, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", system, StringComparison.Ordinal);

        var unknown = StackPrompt.Compose(new RenderedPrompt("BASE", "STEP"), resolution, "generalist");
        Assert.Equal(ErrorCodes.NotFound, unknown.Error?.Code);
    }

    [Fact]
    public void FR121_The_scaffolder_loads_setup_sections_and_the_contract_skill_last_and_the_docs_writer_only_the_summary()
    {
        var resolution = Resolve(AspnetSpa());
        var scaffolder = resolution.Specialist("scaffolder")!;

        Assert.Equal(["fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", "stack-api-contract"], scaffolder.Packs.Select(p => p.Id));
        Assert.Equal(["Detect", "Scaffold", "Conventions", "Config and database URL"], scaffolder.Packs[1].Sections);
        Assert.Equal(["Setup"], scaffolder.Packs[2].Sections);
        Assert.Empty(resolution.Specialist("docs-writer")!.Skills);
        Assert.All(resolution.Specialists.SelectMany(s => s.Skills), s =>
        {
            Assert.NotEqual(PackVocabulary.AgentAutonomySection, s.Section);
            Assert.False(s.Text.StartsWith("## ", StringComparison.Ordinal));
            Assert.DoesNotContain("<!--", s.Text, StringComparison.Ordinal);
        });
    }

    // ── mode-dependent sections ──────────────────────────────────────────

    [Fact]
    public void M26_Prisma_Setup_is_the_frontend_engineers_only_in_Nextjs_full_stack()
    {
        var fullStack = Resolve(Sel("fe-nextjs", "none", "data-prisma", "vcs-github"));
        Assert.Contains(fullStack.Specialist("frontend-engineer")!.Packs, p => p.Id == "data-prisma" && p.Sections.SequenceEqual(["Setup"]));
        Assert.Contains("frontend/src/app/api/**", fullStack.Specialist("frontend-engineer")!.OwnedGlobs);
        Assert.DoesNotContain(fullStack.GeneratedFiles, g => g.Glob == "frontend/src/api/generated/**");

        var ui = Resolve(Sel("fe-nextjs", "be-express", "data-prisma", "vcs-github"));
        Assert.DoesNotContain(ui.Specialist("frontend-engineer")!.Packs, p => p.Id == "data-prisma");
        Assert.DoesNotContain("frontend/src/app/api/**", ui.Specialist("frontend-engineer")!.OwnedGlobs);
        Assert.Contains(ui.GeneratedFiles, g => g.Glob == "frontend/src/api/generated/**");
        Assert.Contains(ui.Specialist("data-engineer")!.OwnedGlobs, g => g == "backend/prisma/**");
    }

    [Fact]
    public void M26_Supabase_as_the_database_host_lends_only_its_Production_notes()
    {
        var host = Resolve(AspnetSpa(backendHost: "deploy-render", databaseHost: "supabase"));

        Assert.Contains(host.Packs, p => p.IsSupabaseHost);
        var data = host.Specialist("data-engineer")!;
        Assert.Contains(data.Packs, p => p.Id == "data-supabase" && p.Sections.SequenceEqual(["Production notes"]));
        Assert.Contains(host.Specialist("deploy-engineer")!.Packs, p => p.Id == "data-supabase" && p.Sections.SequenceEqual(["Production notes"]));
        Assert.DoesNotContain(host.Specialist("frontend-engineer")!.Packs, p => p.Id == "data-supabase");
        Assert.DoesNotContain(host.Specialist("scaffolder")!.Packs, p => p.Id == "data-supabase");
        Assert.DoesNotContain("supabase/**", data.OwnedGlobs);
        Assert.DoesNotContain(host.ReviewChecks, id => id is StackReviewChecks.RlsOnEveryTable or StackReviewChecks.GrantsForExposedTables);
        Assert.DoesNotContain("Auth Flows", host.SpecSectionTitles);
        Assert.Contains("Data Model & Migrations", host.SpecSectionTitles);
        Assert.Contains("Supabase host mode", host.Summary, StringComparison.Ordinal);

        var baas = Resolve(Sel("fe-react-vite", "none", "data-supabase", "vcs-github"));
        Assert.Contains(StackReviewChecks.RlsOnEveryTable, baas.ReviewChecks);
        Assert.Contains(StackReviewChecks.GrantsForExposedTables, baas.ReviewChecks);
        Assert.Contains(baas.Specialist("frontend-engineer")!.Packs, p => p.Id == "data-supabase" && p.Sections.Count > 2);
        Assert.Equal(["Data Model & RLS Policies", "Auth Flows"], baas.SpecSectionTitles);
    }

    // ── FR-123 spec sections ─────────────────────────────────────────────

    [Fact]
    public void FR123_A_backend_with_a_cross_origin_deployment_requires_the_contract_and_origin_sections()
    {
        var resolution = Resolve(AspnetSpa(frontendHost: "deploy-vercel", backendHost: "deploy-render", databaseHost: "render-postgres"));
        Assert.Equal("cross-origin", resolution.Composition.OriginModel);
        Assert.Contains("API Contract & Error Codes", resolution.SpecSectionTitles);
        Assert.Contains("Origin Model (CORS, Cookies, Auth)", resolution.SpecSectionTitles);
        Assert.All(resolution.SpecSections, s => Assert.False(string.IsNullOrWhiteSpace(s.Guidance)));
        Assert.Single(resolution.SpecSectionTitles, t => t == "Environments & Configuration");

        var spec = "# Spec\n\n## Overview\n\nNothing else.\n";
        var validation = SpecValidator.Validate(spec, new SpecValidationContext { StackSections = resolution.SpecSectionTitles });
        Assert.Contains("API Contract & Error Codes", validation.MissingSections);
        Assert.Contains("Origin Model (CORS, Cookies, Auth)", validation.MissingSections);
        Assert.Equal(ErrorCodes.MissingSections, validation.Error?.Code);
    }

    [Fact]
    public void FR123_Same_origin_needs_no_origin_section_and_a_stack_change_adds_Stack_Migration()
    {
        var sameOrigin = Resolve(AspnetSpa(frontendHost: "deploy-railway", backendHost: "deploy-railway", databaseHost: "railway-postgres", local: "none") with { OriginModel = "same-origin" });
        Assert.DoesNotContain("Origin Model (CORS, Cookies, Auth)", sameOrigin.SpecSectionTitles);
        Assert.DoesNotContain("Stack Migration", sameOrigin.SpecSectionTitles);

        var changed = Resolve(AspnetSpa(), new StackChange(true, ["backend"]));
        Assert.Contains("Stack Migration", changed.SpecSectionTitles);
        Assert.Contains("### Stack change", changed.Summary, StringComparison.Ordinal);

        var notOnCode = Resolve(AspnetSpa(), new StackChange(false, ["backend"]));
        Assert.DoesNotContain("Stack Migration", notOnCode.SpecSectionTitles);
    }

    // ── §4.22 env ────────────────────────────────────────────────────────

    [Fact]
    public void M26_Public_env_keys_get_the_frontend_packs_prefix()
    {
        var vite = Resolve(Sel("fe-react-vite", "none", "data-supabase", "vcs-github"));
        Assert.Contains("VITE_SUPABASE_URL", vite.EnvNames);
        Assert.Contains("VITE_SUPABASE_PUBLISHABLE_KEY", vite.EnvNames);
        Assert.DoesNotContain("SUPABASE_URL", vite.EnvNames);

        var next = Resolve(Sel("fe-nextjs", "none", "data-supabase", "vcs-github"));
        Assert.Single(next.EnvNames, n => n == "NEXT_PUBLIC_SUPABASE_URL");
        var secret = Assert.Single(next.Env, e => e.Key == "SUPABASE_SECRET_KEY");
        Assert.True(secret.Sensitive);
        Assert.False(secret.Public);

        var angular = Resolve(Sel("fe-angular", "none", "data-supabase", "vcs-github"));
        Assert.Contains("SUPABASE_URL", angular.EnvNames);
        Assert.Contains("SUPABASE_PUBLISHABLE_KEY", angular.EnvNames);
    }

    [Fact]
    public void M26_Backend_env_applies_to_the_Nextjs_app_in_full_stack_mode()
    {
        var resolution = Resolve(Sel("fe-nextjs", "none", "data-prisma", "vcs-github"));
        Assert.Equal(["frontend"], Assert.Single(resolution.Env, e => e.Key == "DATABASE_URL").AppliesTo);
        Assert.Contains("DIRECT_URL", resolution.EnvNames);

        var crossOrigin = Resolve(AspnetSpa(frontendHost: "deploy-vercel", backendHost: "deploy-render"));
        Assert.Contains("CORS_ALLOWED_ORIGINS", crossOrigin.EnvNames);
        Assert.Contains("VITE_API_BASE_URL", crossOrigin.EnvNames);
        Assert.Equal(["backend"], Assert.Single(crossOrigin.Env, e => e.Key == "DATABASE_URL").AppliesTo);
    }

    [Fact]
    public void M26_AIDE_RUN_MIGRATIONS_is_computed_for_a_serverless_app_with_an_ORM_and_a_database_host()
    {
        var nextOnVercel = Resolve(Sel("fe-nextjs", "none", "data-prisma", "vcs-github", frontendHost: "deploy-vercel", databaseHost: "supabase"));
        Assert.Equal(["frontend"], Assert.Single(nextOnVercel.Env, e => e.Key == StackResolver.RunMigrationsKey).AppliesTo);

        // Vercel's own declaration lists frontend and backend; the computed one names only the app that owns the database.
        var apiOnVercel = Resolve(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-vercel", databaseHost: "external"));
        Assert.Equal(["backend"], Assert.Single(apiOnVercel.Env, e => e.Key == StackResolver.RunMigrationsKey).AppliesTo);

        Assert.DoesNotContain(StackResolver.RunMigrationsKey, Resolve(Sel("fe-nextjs", "none", "data-prisma", "vcs-github", frontendHost: "deploy-vercel")).EnvNames);
        Assert.DoesNotContain(StackResolver.RunMigrationsKey, Resolve(AspnetSpa(frontendHost: "deploy-vercel", backendHost: "deploy-render", databaseHost: "render-postgres")).EnvNames);
        Assert.DoesNotContain(StackResolver.RunMigrationsKey, Resolve(Sel("fe-nextjs", "none", "data-supabase", "vcs-github", frontendHost: "deploy-vercel")).EnvNames);
    }

    // ── composition summary ──────────────────────────────────────────────

    [Fact]
    public void FR121_The_summary_renders_every_placeholder_without_the_template_comments()
    {
        Assert.Equal(CompositionSummary.Placeholders.Order(), CompositionSummary.Used(Catalog.CompositionSummaryTemplate).Order());

        var resolution = Resolve(AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));
        var summary = resolution.Summary;
        Assert.StartsWith("## Stack composition", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("{be}", summary, StringComparison.Ordinal);
        Assert.Contains("| Frontend | `fe-react-vite@1.0.0+aide.1` (React + Vite) | `frontend/` |", summary, StringComparison.Ordinal);
        Assert.Contains("| Data | `data-efcore-postgres@1.0.0+aide.1` (EF Core + PostgreSQL) | inside `backend/` |", summary, StringComparison.Ordinal);
        Assert.Contains("| Database host | Render Postgres |", summary, StringComparison.Ordinal);
        Assert.Contains("- **Origin model:** proxy. ", summary, StringComparison.Ordinal);
        Assert.Contains("`backend/openapi/api.json` → `frontend/src/api/generated/`", summary, StringComparison.Ordinal);
        Assert.Contains("- `DATABASE_URL` — applies to backend; secret;", summary, StringComparison.Ordinal);
        Assert.Contains("migrate (one-off, before the app starts): /app/efbundle", summary, StringComparison.Ordinal);
        Assert.Contains("- `data-engineer` — Data engineer — `backend/src/*Infrastructure*/Persistence/**`", summary, StringComparison.Ordinal);
        Assert.Contains("| Local dev loop | Docker Compose (`local-docker-desktop@1.0.0+aide.1`) |", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FR121_The_summary_renderer_fills_required_and_optional_placeholders_in_one_pass()
    {
        var rendered = CompositionSummary.Render(
            "<!-- provenance -->\n<!--\nnotes {{X}}\n-->\n# T\n\nA {{X}} B {{Y?}} C {{Z}}\n\n\n\nD {{W}}",
            new Dictionary<string, string?> { ["X"] = "{{Z}}", ["W"] = "  w  " });

        Assert.Equal("# T\n\nA {{Z}} B  C n/a\n\nD w", rendered);
    }

    [Fact]
    public void M26_Leading_provenance_comments_are_stripped_from_prompts_and_the_summary()
    {
        Assert.Equal("# Title\n<!-- kept -->", CompositionSummary.StripLeadingComments("﻿<!-- Adapted from x -->\r\n\r\n<!--\nmulti\n-->\n# Title\n<!-- kept -->"));
        Assert.Equal("no comment", CompositionSummary.StripLeadingComments("no comment"));

        var resolution = Resolve(AspnetSpa());
        Assert.All(resolution.Specialists, s =>
        {
            Assert.False(s.Prompt.StartsWith("<!--", StringComparison.Ordinal));
            Assert.DoesNotContain("Adapted from stack-packs", s.Prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void M26_A_composition_naming_an_unknown_pack_is_a_validation_error()
    {
        var composition = Valid(AspnetSpa()) with { Frontend = new StackComponent("fe-svelte", "frontend") };

        var result = StackResolver.Resolve(composition, Catalog);

        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Contains("fe-svelte", result.Error!.Message, StringComparison.Ordinal);
    }
}
