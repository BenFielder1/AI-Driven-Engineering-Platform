using System.Text.Json.Nodes;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>FR-113, FR-122, §4.16: loading the stack catalog — the shipped catalog, the format checks and the content hash.</summary>
public class StackCatalogLoaderTests
{
    private const string Snapshot = "2026-10-25";

    [Fact]
    public void FR122_The_shipped_catalog_loads()
    {
        var result = StackCatalogLoader.Load(ShippedFiles());

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Catalog);
        Assert.Equal("1.0.0", result.Catalog.Version);
        Assert.Matches("^[0-9a-f]{64}$", result.Catalog.ContentHash);
        Assert.Equal("stack@1.0.0", result.Catalog.Label);
        Assert.All(result.Packs, p => Assert.True(p.Loaded, string.Join(" | ", p.Errors)));
    }

    [Fact]
    public void FR113_The_shipped_catalog_lists_5_frontends_5_backends_7_data_3_vcs_and_6_deployment_platforms_with_None_where_allowed()
    {
        var catalog = Catalog;
        IReadOnlyList<string> Packs(string choice) => [.. catalog.Choice(choice).Options.Where(o => o.Id != StackIds.None).Select(o => o.Id)];

        Assert.Equal(["fe-react-vite", "fe-nextjs", "fe-angular", "fe-vue", "fe-vanilla"], Packs(StackChoices.Frontend));
        Assert.Equal(["be-aspnet", "be-spring-boot", "be-fastapi", "be-express", "be-nestjs"], Packs(StackChoices.Backend));
        Assert.Equal(7, Packs(StackChoices.Data).Count);
        Assert.Equal(["vcs-github", "vcs-gitlab", "vcs-bitbucket"], Packs(StackChoices.Vcs));
        Assert.Equal(["deploy-vercel", "deploy-netlify", "deploy-cloudflare", "deploy-github-pages", "deploy-render", "deploy-railway"], Packs(StackChoices.FrontendHost));
        Assert.True(catalog.Choice(StackChoices.Frontend).HasNone);
        Assert.True(catalog.Choice(StackChoices.Backend).HasNone);
        Assert.True(catalog.Choice(StackChoices.Data).HasNone);
        Assert.False(catalog.Choice(StackChoices.Vcs).HasNone);

        // The 27 packs in scope: 5 + 5 + 7 + 3 + 6 layer packs and the Compose dev loop.
        Assert.Equal(27, catalog.Packs.Count);
        Assert.Contains("local-docker-desktop", catalog.Packs.Keys);
        Assert.DoesNotContain("local-docker-desktop-k8s", catalog.Packs.Keys);
        Assert.Equal(["frontend", "backend", "data", "vcs", "deploy"], catalog.Layers.Select(l => l.Id));
    }

    [Fact]
    public void FR113_FR122_Every_pack_records_its_stack_packs_source_and_version()
    {
        foreach (var pack in Catalog.Packs.Values)
        {
            Assert.Equal(new PackSource(pack.Id, "1.0.0", Snapshot), pack.Manifest.Source);
            Assert.Equal("1.0.0+aide.1", pack.Manifest.Version);
            Assert.StartsWith($"<!-- Adapted from stack-packs {pack.Id}@1.0.0 skills/{pack.Id}/SKILL.md, snapshot {Snapshot} -->", pack.Skill, StringComparison.Ordinal);
            Assert.Equal($"{pack.Id}@1.0.0+aide.1", pack.Label);
            Assert.Matches("^[0-9a-f]{64}$", pack.ContentHash);
        }

        Assert.Equal(["full-stack", "ui"], Catalog.Packs["fe-nextjs"].Manifest.Modes.Select(m => m.Id));
        Assert.Equal(["baas", "host"], Catalog.Packs["data-supabase"].Manifest.Modes.Select(m => m.Id));
        Assert.Equal("baas", Catalog.Choice(StackChoices.Data).Find("data-supabase")!.Mode);
        Assert.Equal(("NEXT_PUBLIC_", "node"), (Catalog.Packs["fe-nextjs"].Manifest.PublicEnvPrefix, Catalog.Packs["fe-nextjs"].Manifest.Runtime));
        Assert.Equal("dotnet", Catalog.Packs["be-aspnet"].Manifest.Runtime);
        Assert.Equal("render", Catalog.Packs["deploy-render"].Manifest.Deploy!.Provider);
    }

    [Fact]
    public void FR122_Section_headings_are_matched_without_a_trailing_parenthetical()
    {
        Assert.Equal("Modelling", SkillSections.Normalise("Modelling (BaaS)"));
        Assert.Equal("Config as code", SkillSections.Normalise("Config as code (`netlify.toml` at the site's base directory)"));
        Assert.Equal("Errors, permissions, states", SkillSections.Normalise("Errors, permissions, states"));
        Assert.Equal(["Detect", "Setup"], SkillSections.Parse("# T\n## Detect\ntext\n```md\n## Not a section\n```\n## Setup (host)\n"));

        Assert.Contains("Modelling", Catalog.Packs["data-supabase"].Sections);
        Assert.Contains("Config as code", Catalog.Packs["deploy-netlify"].Sections);
        Assert.Contains("Base path", Catalog.Packs["deploy-github-pages"].Sections);

        // A pack may name a suffixed heading by its matched name.
        var files = ShippedFiles();
        EditJson(files, "packs/deploy-netlify/pack.json", p => p["agentSections"] = new JsonObject { ["deploy-engineer"] = new JsonArray("Config as code", "Environment variables") });
        Assert.Empty(StackCatalogLoader.Load(files).Errors);
    }

    [Fact]
    public void FR122_An_agent_section_the_skill_doesnt_have_fails_loading_naming_the_pack_and_section()
    {
        var files = ShippedFiles();
        EditJson(files, "packs/be-aspnet/pack.json", p => p["agentSections"] = new JsonObject { ["test-engineer"] = new JsonArray("Testing", "Gates") });

        var result = StackCatalogLoader.Load(files);

        Assert.Null(result.Catalog);
        var error = Assert.Single(result.Errors);
        Assert.Equal("be-aspnet", error.Pack);
        Assert.Contains("'Gates'", error.Message, StringComparison.Ordinal);
        Assert.False(result.Packs.Single(p => p.Id == "be-aspnet").Loaded);
    }

    [Fact]
    public void FR122_Agent_autonomy_and_unknown_agent_keys_in_agent_sections_fail()
    {
        var files = ShippedFiles();
        EditJson(files, "packs/deploy-vercel/pack.json", p => p["agentSections"] = new JsonObject
        {
            ["deploy-engineer"] = new JsonArray("Agent autonomy"),
            ["release-manager"] = new JsonArray("*"),
        });

        var errors = StackCatalogLoader.Load(files).Errors;

        Assert.Contains(errors, e => e.Pack == "deploy-vercel" && e.Message.Contains("Agent autonomy", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Pack == "deploy-vercel" && e.Message.Contains("release-manager", StringComparison.Ordinal));
    }

    [Fact]
    public void FR122_A_missing_skill_or_pack_or_core_file_fails_loading()
    {
        var noSkill = ShippedFiles();
        noSkill.Remove("packs/fe-vue/skill.md");
        Assert.Contains(StackCatalogLoader.Load(noSkill).Errors, e => e.Pack == "fe-vue" && e.Message.Contains("skill.md", StringComparison.Ordinal));

        var noPack = ShippedFiles();
        foreach (var path in noPack.Keys.Where(k => k.StartsWith("packs/fe-vue/", StringComparison.Ordinal)).ToList())
        {
            noPack.Remove(path);
        }

        var noPackResult = StackCatalogLoader.Load(noPack);
        Assert.Null(noPackResult.Catalog);
        Assert.Contains(noPackResult.Errors, e => e.Message.Contains("'fe-vue'", StringComparison.Ordinal));

        var noCore = ShippedFiles();
        noCore.Remove(StackCatalogLoader.StackApiContractFile);
        Assert.Contains(StackCatalogLoader.Load(noCore).Errors, e => e.Message.Contains(StackCatalogLoader.StackApiContractFile, StringComparison.Ordinal));

        var noCatalog = ShippedFiles();
        noCatalog.Remove(StackCatalogLoader.CatalogFile);
        Assert.Contains(StackCatalogLoader.Load(noCatalog).Errors, e => e.Message.Contains("catalog.json is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void FR122_An_unknown_pack_or_option_id_in_the_rules_fails_loading()
    {
        var files = ShippedFiles();
        EditJson(files, StackCatalogLoader.CatalogFile, c =>
        {
            var constraints = c["rules"]!["constraints"]!.AsArray();
            constraints.Add(JsonNode.Parse("""{ "rule": "R3", "choices": ["data"], "options": ["data-mongoose"], "state": "forbidden", "when": [{ "backend": ["be-rails"] }], "text": "x" }"""));
        });

        var errors = StackCatalogLoader.Load(files).Errors;

        Assert.Contains(errors, e => e.Message.Contains("'data-mongoose'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("'be-rails'", StringComparison.Ordinal));
    }

    [Fact]
    public void FR122_A_rule_depending_on_a_later_choice_fails_loading()
    {
        var files = ShippedFiles();
        EditJson(files, StackCatalogLoader.CatalogFile, c => c["rules"]!["constraints"]!.AsArray().Add(
            JsonNode.Parse("""{ "rule": "R3", "choices": ["data"], "options": ["data-prisma"], "state": "forbidden", "when": [{ "backendHost": ["deploy-vercel"] }], "text": "x" }""")));

        Assert.Contains(StackCatalogLoader.Load(files).Errors, e => e.Message.Contains("isn't chosen before", StringComparison.Ordinal));
    }

    [Fact]
    public void FR122_Unknown_properties_invalid_globs_and_a_missing_provenance_line_fail_loading()
    {
        var unknown = ShippedFiles();
        EditJson(unknown, "packs/fe-vue/pack.json", p => p["framework"] = "vue");
        Assert.Contains(StackCatalogLoader.Load(unknown).Errors, e => e.Pack == "fe-vue" && e.Message.Contains("framework", StringComparison.Ordinal));

        var glob = ShippedFiles();
        EditJson(glob, "packs/be-express/pack.json", p => p["ownership"] = new JsonObject { ["backend-engineer"] = new JsonArray("{be}/**", "/etc/**", "{web}/**") });
        var globErrors = StackCatalogLoader.Load(glob).Errors;
        Assert.Contains(globErrors, e => e.Pack == "be-express" && e.Message.Contains("'/etc/**'", StringComparison.Ordinal));
        Assert.Contains(globErrors, e => e.Pack == "be-express" && e.Message.Contains("{web}", StringComparison.Ordinal));

        var provenance = ShippedFiles();
        provenance["packs/vcs-github/skill.md"] = "# GitHub\n\n## Detect\n";
        Assert.Contains(StackCatalogLoader.Load(provenance).Errors, e => e.Pack == "vcs-github" && e.Message.Contains("provenance", StringComparison.Ordinal));

        var catalogProperty = ShippedFiles();
        EditJson(catalogProperty, StackCatalogLoader.CatalogFile, c => c["presets"] = new JsonArray());
        Assert.Contains(StackCatalogLoader.Load(catalogProperty).Errors, e => e.Message.Contains("presets", StringComparison.Ordinal));
    }

    [Fact]
    public void FR122_Later_manifest_fields_are_parsed_and_validated()
    {
        var files = ShippedFiles();
        EditJson(files, "packs/data-supabase/pack.json", p =>
        {
            p["specSections"] = JsonNode.Parse("""[{ "title": "Data Model & RLS Policies", "guidance": "Tables, policies.", "when": "mode == baas" }]""");
            p["ownership"] = JsonNode.Parse("""{ "data-engineer": ["supabase/**", "{fe}/src/lib/database.types.ts"] }""");
            p["agentSections"] = JsonNode.Parse("""{ "data-engineer": ["*"], "frontend-engineer": ["Setup", "Modelling"] }""");
            p["planRules"] = JsonNode.Parse("""{ "order": [{ "before": ["supabase/migrations/**"], "after": ["{fe}/**"] }], "exclusive": [{ "agent": "data-engineer", "notWithGlobs": ["{fe}/src/**"] }], "maxFilesPerStep": [{ "glob": "supabase/migrations/*.sql", "max": 1 }] }""");
            p["protectedPaths"] = JsonNode.Parse("""[{ "glob": "supabase/migrations/**", "existingOnBase": true, "reason": "immutable migrations" }]""");
            p["generatedFiles"] = JsonNode.Parse("""[{ "glob": "{fe}/src/lib/database.types.ts", "handling": "hand_written" }]""");
            p["requiredEnv"] = JsonNode.Parse("""[{ "key": "SUPABASE_URL", "appliesTo": ["frontend"], "targets": ["production", "preview"], "sensitive": false, "public": true, "description": "Project URL.", "when": "mode == baas" }]""");
            p["review"] = JsonNode.Parse("""{ "checklist": [{ "item": "RLS on every table", "category": "security", "severity": "critical" }], "deterministic": ["rls_on_every_table"] }""");
            p["migrate"] = JsonNode.Parse("""{ "command": "supabase db push", "needsDirectUrl": true }""");
        });

        var result = StackCatalogLoader.Load(files);

        Assert.Empty(result.Errors);
        var manifest = result.Catalog!.Packs["data-supabase"].Manifest;
        Assert.Equal("mode == baas", manifest.SpecSections[0].When);
        Assert.Equal(["supabase/**", "{fe}/src/lib/database.types.ts"], manifest.Ownership[StackAgents.DataEngineer]);
        Assert.Equal(["Setup", "Modelling"], manifest.AgentSections[StackAgents.FrontendEngineer]);
        Assert.Equal(1, manifest.PlanRules.MaxFilesPerStep[0].Max);
        Assert.True(manifest.ProtectedPaths[0].ExistingOnBase);
        Assert.Equal("hand_written", manifest.GeneratedFiles[0].Handling);
        Assert.True(manifest.RequiredEnv[0].Public);
        Assert.Equal(["rls_on_every_table"], manifest.Review.Deterministic);
        Assert.Equal(new PackMigrate("supabase db push", true), manifest.Migrate);

        var bad = ShippedFiles();
        EditJson(bad, "packs/data-prisma/pack.json", p =>
        {
            p["requiredEnv"] = JsonNode.Parse("""[{ "key": "database_url", "appliesTo": ["edge"], "targets": ["staging"], "sensitive": true, "public": true, "description": "x", "when": "colour = red" }]""");
            p["review"] = JsonNode.Parse("""{ "checklist": [], "deterministic": ["no_magic"] }""");
            p["gate"] = new JsonArray("npm test");
            p["planRules"] = JsonNode.Parse("""{ "order": [], "exclusive": [{ "agent": "dba", "notWithGlobs": ["**"] }], "maxFilesPerStep": [{ "glob": "prisma/**", "max": 0 }] }""");
        });
        var reasons = StackCatalogLoader.Load(bad).Errors.Where(e => e.Pack == "data-prisma").Select(e => e.Message).ToList();
        Assert.Contains(reasons, r => r.Contains("UPPER_SNAKE_CASE", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'edge'", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'staging'", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("both sensitive and public", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("colour = red", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'no_magic'", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("gate is for frontend and backend packs only", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("'dba'", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("from 1 to 100", StringComparison.Ordinal));
    }

    [Fact]
    public void FR113_The_content_hash_is_the_v2_preset_algorithm_over_sorted_paths_with_normalised_line_endings_and_no_BOM()
    {
        var a = new Dictionary<string, string> { ["b.md"] = "two\r\n", ["a.json"] = "﻿one\n" };
        var b = new Dictionary<string, string> { ["a.json"] = "one\n", ["b.md"] = "two\n" };

        Assert.Equal(StackCatalogLoader.ContentHash(a), StackCatalogLoader.ContentHash(b));
        Assert.Equal(Domain.Presets.PresetLoader.ContentHash(b), StackCatalogLoader.ContentHash(b));
        Assert.NotEqual(StackCatalogLoader.ContentHash(b), StackCatalogLoader.ContentHash(new Dictionary<string, string> { ["a.json"] = "one\n", ["b.md"] = "three\n" }));

        var files = ShippedFiles();
        var before = StackCatalogLoader.Load(files).ContentHash;
        files["packs/fe-vue/skill.md"] += "\n";
        Assert.NotEqual(before, StackCatalogLoader.Load(files).ContentHash);
    }

    [Fact]
    public void FR122_Agent_and_legacy_placeholders_are_tolerated_and_unknown_agent_prompts_fail()
    {
        Assert.Equal(StackAgents.All.OrderBy(k => k, StringComparer.Ordinal), Catalog.AgentPrompts.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var files = ShippedFiles();
        files["agents/backend-engineer.md"] = "<!-- Adapted from stack-packs stack-core@1.0.0 agents/stack-backend-engineer.md, snapshot 2026-10-25 -->\n# Backend engineer\n";
        files["legacy/nextjs-supabase.json"] = "{}";
        var loaded = StackCatalogLoader.Load(files);
        Assert.Empty(loaded.Errors);
        Assert.StartsWith("<!--", loaded.Catalog!.AgentPrompts[StackAgents.BackendEngineer], StringComparison.Ordinal);

        files["agents/release-manager.md"] = "<!-- x -->\n";
        Assert.Contains(StackCatalogLoader.Load(files).Errors, e => e.Message.Contains("release-manager", StringComparison.Ordinal));
    }
}
