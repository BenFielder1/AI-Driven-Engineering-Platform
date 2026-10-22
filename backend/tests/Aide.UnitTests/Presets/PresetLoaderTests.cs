using Aide.Domain.Common;
using Aide.Domain.Presets;
using Aide.Domain.Review;
using Aide.Infrastructure.Presets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aide.UnitTests.Presets;

/// <summary>FR-74, §4.11, D-V2-12: loading presets — the format, referenced files, the content hash and the shipped presets.</summary>
public class PresetLoaderTests
{
    /// <summary>The presets v2 ships (§4.12); more may be added.</summary>
    private static readonly string[] RequiredShippedIds = ["nextjs-frontend", "nextjs-supabase"];

    [Fact]
    public void FR74_The_fixture_preset_loads_with_its_agents_skills_conventions_rules_and_protected_paths()
    {
        var preset = TestPresets.Fixture;

        Assert.Equal(("test-stack", "Test stack", "0.9.0", "test-stack@0.9.0"), (preset.Id, preset.Name, preset.Version, preset.RunLabel));
        Assert.Equal(64, preset.ContentHash.Length);
        Assert.Equal(["Next.js", "Supabase"], preset.Stack);
        Assert.Equal(["vercel"], preset.DeployTargets);
        Assert.Equal(["project-scaffolder", "db-engineer", "domain-logic-engineer", "api-engineer", "ui-engineer"], preset.AgentKeys);
        Assert.Equal(["Data Model & RLS Policies", "Environment Variables"], preset.SpecSectionTitles);
        Assert.StartsWith("# Stack conventions", preset.SpecConventions, StringComparison.Ordinal);

        var db = preset.Agent("db-engineer")!;
        Assert.Equal("agents/db-engineer.md", db.PromptFile);
        Assert.Equal("You are the db-engineer of the test stack.", db.Prompt);
        Assert.Equal("supabase-migration", Assert.Single(db.SkillTexts).Name);
        Assert.Contains("row level security", db.SkillTexts[0].Text, StringComparison.Ordinal);

        var env = preset.RequiredEnv[1];
        Assert.Equal(("SERVICE_KEY", true), (env.Key, env.Sensitive));
        Assert.Equal(["production"], env.Targets);

        Assert.True(preset.PlanRules.ScaffoldFirstWhenEmpty);
        Assert.Equal(PresetRules.DefaultScaffolder, preset.PlanRules.Scaffolder);
        Assert.Equal([false, true], preset.PlanRules.Order.Select(o => o.Soft));
        Assert.Equal("db-engineer", Assert.Single(preset.PlanRules.Exclusive).Agent);
        Assert.Equal(("supabase/migrations/**", 1), (preset.PlanRules.FileLimits[0].Glob.Pattern, preset.PlanRules.FileLimits[0].Max));

        Assert.Equal([(ReviewCategory.Security, Severity.Critical), (ReviewCategory.ApiContracts, Severity.Major)], preset.Review.Checklist.Select(c => (c.Category, c.Severity)));
        Assert.Equal(PresetVocabulary.DeterministicRules, preset.Review.Deterministic);
        Assert.Equal(3, preset.ProtectedPaths.Count);
    }

    [Fact]
    public void FR74_The_content_hash_is_stable_independent_of_line_endings_and_changes_with_any_file()
    {
        var files = TestPresets.FixtureFiles;
        var hash = PresetLoader.ContentHash(files);

        var crlf = files.ToDictionary(f => f.Key, f => f.Value.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(hash, PresetLoader.ContentHash(crlf));
        Assert.Equal(hash, TestPresets.Fixture.ContentHash);

        // Order of enumeration doesn't matter; content and paths do, referenced or not.
        Assert.Equal(hash, PresetLoader.ContentHash(files.Reverse().ToDictionary(f => f.Key, f => f.Value)));
        Assert.NotEqual(hash, PresetLoader.ContentHash(TestPresets.With(f => f["skills/supabase-migration.md"] += "More.")));
        Assert.NotEqual(hash, PresetLoader.ContentHash(TestPresets.With(f => f["notes.txt"] = "unreferenced")));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Theory]
    [InlineData("agents/db-engineer.md", "agents[1].prompt references 'agents/db-engineer.md', which is missing.")]
    [InlineData("skills/supabase-migration.md", "agents[1].skills 'supabase-migration' references 'skills/supabase-migration.md', which is missing.")]
    [InlineData("conventions.md", "specConventions references 'conventions.md', which is missing.")]
    [InlineData("preset.json", "preset.json is missing.")]
    public void FR74_A_missing_referenced_file_fails_loading(string removed, string reason)
    {
        var result = PresetLoader.Load(TestPresets.FixtureId, TestPresets.With(f => f.Remove(removed)));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.Contains(reason, result.Error.Details);
    }

    [Theory]
    [InlineData("\"id\": \"test-stack\"", "\"id\": \"other\"", "id 'other' must equal the folder name 'test-stack'.")]
    [InlineData("\"version\": \"0.9.0\"", "\"version\": \"0.9 beta\"", "version '0.9 beta' may not contain whitespace or '@'.")]
    [InlineData("\"deployTargets\": [\"vercel\"]", "\"deployTargets\": [\"netlify\"]", "deployTargets: 'netlify' is not one of vercel.")]
    [InlineData("\"targets\": [\"production\"]", "\"targets\": [\"staging\"]", "requiredEnv[1].targets: 'staging' is not one of production, preview, development.")]
    [InlineData("\"key\": \"SERVICE_KEY\"", "\"key\": \"service-key\"", "requiredEnv[1].key 'service-key' must be UPPER_SNAKE_CASE.")]
    [InlineData("\"severity\": \"major\"", "\"severity\": \"blocker\"", "review.checklist[1].severity 'blocker' is not one of minor, major, critical.")]
    [InlineData("\"category\": \"api_contracts\"", "\"category\": \"style\"", "review.checklist[1].category 'style' is not one of feature_coverage, api_contracts, data_model, architecture, security, testing.")]
    [InlineData("\"no_secret_env_in_client\"]", "\"lint_everything\"]", "review.deterministic: 'lint_everything' is not one of rls_on_every_table, no_secret_env_in_client.")]
    [InlineData("{ \"agent\": \"db-engineer\",", "{ \"agent\": \"ml-engineer\",", "planRules.exclusive[0].agent 'ml-engineer' is not in the catalogue.")]
    [InlineData("\"key\": \"project-scaffolder\"", "\"key\": \"scaffolder\"", "planRules.scaffoldFirstWhenEmpty needs the agent 'project-scaffolder' in the catalogue (or set planRules.scaffolder).")]
    [InlineData("\"key\": \"ui-engineer\"", "\"key\": \"api-engineer\"", "agents[4].key 'api-engineer' is listed twice.")]
    [InlineData("\"key\": \"ui-engineer\"", "\"key\": \"generalist\"", "agents[4].key 'generalist' is reserved for plans without a preset.")]
    [InlineData("\"owns\": [\"app/api/**\", \"lib/api/**\"]", "\"owns\": [\"/app/api/**\"]", "agents[3].owns: The glob '/app/api/**' must be a relative path with forward slashes (no leading '/', '.', '..' or empty segments).")]
    [InlineData("\"max\": 1", "\"max\": 0", "planRules.maxFilesPerStep[0].max must be from 1 to 100.")]
    [InlineData("\"existingOnBase\": true,", "\"existingOnBase\": \"yes\",", "protectedPaths[0]: unknown or mistyped property 'existingOnBase' (glob: string, existingOnBase: boolean, except: string[], reason: string).")]
    public void FR74_An_invalid_manifest_value_fails_loading_with_the_reason(string find, string replace, string reason)
    {
        var files = TestPresets.WithManifest(m =>
        {
            Assert.Contains(find, m, StringComparison.Ordinal);
            return m.Replace(find, replace, StringComparison.Ordinal);
        });

        var result = PresetLoader.Load(TestPresets.FixtureId, files);

        Assert.True(result.IsFailure);
        Assert.Contains(reason, result.Error!.Details);
    }

    [Fact]
    public void FR74_Unknown_properties_and_malformed_json_fail_loading()
    {
        var typo = PresetLoader.Load(TestPresets.FixtureId, TestPresets.WithManifest(m => m.Replace("\"stack\":", "\"stacks\": [], \"stack\":", StringComparison.Ordinal)));
        Assert.True(typo.IsFailure);
        Assert.Contains("stacks", typo.Error!.Message, StringComparison.Ordinal);

        var broken = PresetLoader.Load(TestPresets.FixtureId, TestPresets.WithManifest(m => m[..^5]));
        Assert.True(broken.IsFailure);
        Assert.StartsWith("preset.json is not valid", broken.Error!.Details[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FR74_Spec_conventions_may_be_inline_markdown_and_one_migration_per_step_is_a_shorthand()
    {
        var files = TestPresets.WithManifest(m => m
            .Replace("\"specConventions\": \"conventions.md\"", "\"specConventions\": \"Use **strict** TypeScript.\\nNo any.\"", StringComparison.Ordinal)
            .Replace("\"maxFilesPerStep\": [{ \"glob\": \"supabase/migrations/**\", \"max\": 1 }]", "\"oneMigrationPerStep\": { \"glob\": \"db/**\" }", StringComparison.Ordinal));
        files.Remove("conventions.md");

        var preset = TestPresets.Load(files);

        Assert.Equal("Use **strict** TypeScript.\nNo any.", preset.SpecConventions);
        Assert.Equal(("db/**", 1), (preset.PlanRules.FileLimits[0].Glob.Pattern, preset.PlanRules.FileLimits[0].Max));
        Assert.True(PresetLoader.IsFileReference("docs/conventions.md"));
        Assert.False(PresetLoader.IsFileReference("See conventions.md"));
    }

    [Fact]
    public void FR74_The_catalogue_keeps_presets_that_load_and_reports_the_ones_that_fail()
    {
        var broken = new PresetFolder("broken", TestPresets.With(f => f.Remove("agents/db-engineer.md")));
        var (presets, errors) = PresetCatalog.Load([new PresetFolder(TestPresets.FixtureId, TestPresets.FixtureFiles), broken]);

        Assert.Equal(TestPresets.FixtureId, Assert.Single(presets).Id);
        var error = Assert.Single(errors);
        Assert.Equal("broken", error.Id);
        Assert.Contains("id 'test-stack' must equal the folder name 'broken'.", error.Reasons);
        Assert.Contains("agents[1].prompt references 'agents/db-engineer.md', which is missing.", error.Reasons);

        var catalog = new PresetCatalog([TestPresets.Source], NullLogger<PresetCatalog>.Instance);
        Assert.Same(catalog.Find(TestPresets.FixtureId), catalog.All.Single(p => p.Id == TestPresets.FixtureId));
        Assert.Null(catalog.Find("nope"));
        Assert.Null(catalog.Find(null));
        Assert.Empty(catalog.Errors);
    }

    /// <summary>
    /// §4.11: "a unit test asserts every preset loads". Enumerates whatever ships under
    /// <c>Aide.Infrastructure/Agents/Presets/</c>, and requires at least the two v2 presets (§4.12).
    /// </summary>
    [Fact]
    public void FR74_Every_shipped_preset_loads()
    {
        var folders = EmbeddedPresetSource.Shipped.ReadFolders();
        var (presets, errors) = PresetCatalog.Load(folders);

        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Id}: {string.Join(" | ", e.Reasons)}")));
        Assert.Equal(folders.Select(f => f.Id), presets.Select(p => p.Id));
        foreach (var id in RequiredShippedIds)
        {
            Assert.Contains(id, presets.Select(p => p.Id));
        }

        foreach (var preset in presets)
        {
            Assert.All(preset.Agents, a => Assert.False(string.IsNullOrWhiteSpace(a.Prompt)));
            Assert.Equal(preset.Agents.Sum(a => a.Skills.Count), preset.Agents.Sum(a => a.SkillTexts.Count));
        }
    }
}
