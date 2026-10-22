using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Presets;
using Aide.Domain.Specs;

namespace Aide.UnitTests.Presets;

/// <summary>FR-74 pinning rules, FR-76 specialist prompts, §4.11 protected paths and FR-75 spec sections, on the fixture preset.</summary>
public class PresetRulesTests
{
    private static readonly Preset Fixture = TestPresets.Fixture;

    [Fact]
    public void FR74_The_preset_can_change_while_the_version_is_in_development_and_its_plan_never_locked()
    {
        Assert.True(PresetRules.CanChange(ReleaseVersionStatus.InDevelopment, planWasLocked: false).IsSuccess);

        Assert.Equal(ErrorCodes.PresetLocked, PresetRules.CanChange(ReleaseVersionStatus.InDevelopment, planWasLocked: true).Error!.Code);
        Assert.Equal(ErrorCodes.PresetLocked, PresetRules.CanChange(ReleaseVersionStatus.AwaitingMerge, planWasLocked: false).Error!.Code);
        Assert.Equal(ErrorCodes.PresetLocked, PresetRules.CanChange(ReleaseVersionStatus.Released, planWasLocked: false).Error!.Code);
        Assert.Equal(ErrorCodes.PresetLocked, PresetRules.CanChange(null, planWasLocked: false).Error!.Code);
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.PresetLocked));
        Assert.Equal(400, ErrorCodes.StatusCodeFor(ErrorCodes.PresetNotFound));
    }

    [Fact]
    public void FR74_A_plan_counts_as_locked_once_approved_even_if_later_invalidated()
    {
        var approved = DateTimeOffset.UnixEpoch;
        Assert.False(PresetRules.PlanWasLocked([]));
        Assert.False(PresetRules.PlanWasLocked([(ArtifactStatus.Draft, null), (ArtifactStatus.Rejected, null), (ArtifactStatus.Invalidated, null)]));
        Assert.True(PresetRules.PlanWasLocked([(ArtifactStatus.Locked, null)]));
        Assert.True(PresetRules.PlanWasLocked([(ArtifactStatus.Invalidated, approved)]));
    }

    [Fact]
    public void FR74_Runs_record_preset_id_at_version_and_ids_are_normalised()
    {
        Assert.Equal("nextjs-frontend@1.0.0", PresetRules.RunLabel("nextjs-frontend", "1.0.0"));
        Assert.Null(PresetRules.NormaliseId("  "));
        Assert.Null(PresetRules.NormaliseId(null));
        Assert.Equal("nextjs-frontend", PresetRules.NormaliseId(" nextjs-frontend "));
        Assert.True(PresetRules.IsKebabId("db-engineer"));
        Assert.False(PresetRules.IsKebabId("DB_engineer"));
        Assert.False(PresetRules.IsKebabId("-db"));
    }

    [Fact]
    public void FR74_A_run_records_the_pinned_preset_as_id_at_version_and_the_specialist_agent_key()
    {
        var catalog = new Aide.Infrastructure.Presets.PresetCatalog([TestPresets.Source], Microsoft.Extensions.Logging.Abstractions.NullLogger<Aide.Infrastructure.Presets.PresetCatalog>.Instance);
        var version = new ReleaseVersion { PresetId = TestPresets.FixtureId, PresetVersion = "0.9.0" };
        var preset = Aide.Infrastructure.Presets.PresetCatalogExtensions.ForVersion(catalog, version);
        var run = new AgentRun();

        Aide.Infrastructure.Presets.PresetCatalogExtensions.StampRun(run, preset, "db-engineer");

        Assert.Equal(("test-stack", "0.9.0", "db-engineer"), (run.PresetId, run.PresetVersion, run.AgentKey));
        Assert.Equal("test-stack@0.9.0", PresetRules.RunLabel(run.PresetId!, run.PresetVersion!));
        Assert.Null(Aide.Infrastructure.Presets.PresetCatalogExtensions.ForVersion(catalog, new ReleaseVersion()));
        Aide.Infrastructure.Presets.PresetCatalogExtensions.StampRun(run, null);
        Assert.Equal(((string?)null, (string?)null, "db-engineer"), (run.PresetId, run.PresetVersion, run.AgentKey));
    }

    [Fact]
    public void FR76_The_db_engineer_prompt_is_base_then_conventions_then_agent_then_its_skills_then_the_step_input()
    {
        var basePrompt = new RenderedPrompt("BASE code generator contract.", "STEP INPUT: step 3, supabase/migrations/0002_tasks.sql");

        var composed = SpecialistPrompt.Compose(basePrompt, Fixture, "db-engineer");

        Assert.True(composed.IsSuccess);
        var system = composed.Value.System;
        int At(string text)
        {
            var index = system.IndexOf(text, StringComparison.Ordinal);
            Assert.True(index >= 0, $"'{text}' is missing from the prompt.");
            return index;
        }

        var order = new[]
        {
            At("BASE code generator contract."),
            At("## Stack conventions (Test stack preset)"),
            At("Use TypeScript strict."),
            At("## Your role: Database engineer (db-engineer)"),
            At("You own these paths: `supabase/migrations/**`, `supabase/seed.sql`."),
            At("You are the db-engineer of the test stack."),
            At("## Skill: supabase-migration"),
            At("enable row level security on every table"),
        };
        Assert.Equal(order.Order(), order);
        Assert.StartsWith("BASE", system, StringComparison.Ordinal);
        Assert.Equal(basePrompt.User, composed.Value.User);

        Assert.Equal(ErrorCodes.NotFound, SpecialistPrompt.Compose(basePrompt, Fixture, "ml-engineer").Error!.Code);
    }

    [Fact]
    public void S4_11_Protected_paths_cover_existing_migrations_generated_types_and_env_files_except_the_example()
    {
        var onBase = new HashSet<string>(StringComparer.Ordinal) { "supabase/migrations/0001_init.sql" };
        bool Protected(string path) => Fixture.IsProtected(path, onBase.Contains);

        Assert.True(Protected("supabase/migrations/0001_init.sql"));
        Assert.False(Protected("supabase/migrations/0002_tasks.sql")); // new migrations are fine
        Assert.True(Protected("lib/database.types.ts"));
        Assert.True(Protected(".env"));
        Assert.True(Protected(".env.local"));
        Assert.True(Protected("apps/web/.env.production"));
        Assert.False(Protected(".env.example"));
        Assert.False(Protected("app/page.tsx"));
        Assert.Equal("immutable migrations", Fixture.ProtectedPaths[0].Reason);
    }

    [Fact]
    public void FR75_The_preset_sections_are_required_by_spec_validation()
    {
        var context = SpecValidationContext.ForVersion(isIteration: false, baselineSpecification: null, Fixture.SpecSectionTitles);

        Assert.Contains("Data Model & RLS Policies", context.RequiredSections);
        Assert.Contains("Environment Variables", context.RequiredSections);
    }
}
