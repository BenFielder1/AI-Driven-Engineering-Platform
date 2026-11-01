using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Runs;
using Aide.Api.Features.Stack;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Stack;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Stack;

/// <summary>
/// FR-129, §4.23, D-V4-4 end to end with the Fake provider and Local Git: the preset API is gone, a project migrated from
/// <c>nextjs-supabase</c> keeps the retired preset as history, and its in-flight plan still runs: a step naming the v2 key
/// <c>db-engineer</c> runs as <c>data-engineer</c> with the <c>data-supabase</c> skill and the run reports
/// <c>legacyAgentKey: db-engineer</c>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PresetRetirementTests(AideApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Task Board\n\n## Summary\nA task board.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>§4.23: what the RetirePresets migration pins for <c>nextjs-supabase</c> without Vercel settings.</summary>
    private static readonly StackSelection MigratedSupabase = new()
    {
        Frontend = new StackComponentPick("fe-nextjs", "."),
        Backend = new StackComponentPick(StackIds.None),
        Data = new StackDataPick("data-supabase"),
        Vcs = new StackVcsPick("vcs-github"),
        Deploy = new StackDeployPick(StackIds.None, StackIds.None, "supabase", StackIds.None),
        OriginModel = "same-origin",
    };

    /// <summary>Pins the migrated composition (catalog hash NULL, as the migration writes it) and keeps the retired preset as history.</summary>
    private static async Task MigrateAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var validation = StackCompatibility.Validate(MigratedSupabase, app.Services.GetRequiredService<IStackCatalog>().Current);
        Assert.True(validation.IsValid, string.Join(" | ", validation.Violations.Select(v => v.Reason)));
        var composition = validation.Composition! with { CatalogHash = null };
        var json = StackCompositionJson.Serialize(composition);
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == projectId).ExecuteUpdateAsync(
            s => s.SetProperty(v => v.StackComposition, json)
                .SetProperty(v => v.CatalogVersion, composition.CatalogVersion)
                .SetProperty(v => v.CatalogHash, (string?)null)
                .SetProperty(v => v.PresetId, "nextjs-supabase")
                .SetProperty(v => v.PresetVersion, "1.0.1"),
            Ct));
    }

    private static async Task<IReadOnlyList<ExecutionStep>> SeedExecutionAsync(
        WebApplicationFactory<Program> app, Guid id, (PlanStep Step, string? LegacyAgentKey)[] plan, int completedThrough)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nNext.js + Supabase.");
        var planArtifact = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: PlanDocument.Render(ArtifactType.ImplementationPlan, [.. plan.Select(p => p.Step)]));
        return await ProjectApi.WithDbAsync<IReadOnlyList<ExecutionStep>>(app, async db =>
        {
            var steps = plan.Select(p => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = planArtifact.Id,
                StepNumber = p.Step.Number,
                Title = p.Step.Title,
                Task = p.Step.Task,
                DeclaredFiles = p.Step.Files,
                Dependencies = p.Step.Dependencies,
                AgentKey = p.Step.Agent,
                LegacyAgentKey = p.LegacyAgentKey,
                Status = p.Step.Number <= completedThrough ? StepStatus.Completed : StepStatus.Pending,
                Attempt = p.Step.Number <= completedThrough ? 1 : 0,
            }).ToList();
            db.ExecutionSteps.AddRange(steps);
            await db.SaveChangesAsync(Ct);
            return steps;
        });
    }

    private static PlanStep Step(int number, string title, string path, string agent, FileAction action = FileAction.Create) =>
        new(number, title, $"{title}.", [new DeclaredFile(path, action)], number == 1 ? [] : [number - 1], Agent: agent);

    // ---------- D-V4-4: the preset API is gone ----------

    [Fact]
    public async Task FR129_The_preset_endpoints_are_gone()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Owner.Client.GetAsync("/api/presets", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Owner.Client.GetAsync("/api/presets/nextjs-supabase", Ct)).StatusCode);
        var patch = await fixture.Owner.Client.PatchAsJsonAsync($"{fixture.Path}/preset", new { presetId = "nextjs-supabase" }, Ct);
        Assert.Contains(patch.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    // ---------- §4.23: history ----------

    [Fact]
    public async Task FR129_A_migrated_version_shows_its_retired_preset_as_history_and_resolves_its_composition()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await MigrateAsync(factory, fixture.Project.Id);

        var stack = await ProjectApi.ReadAsync<ProjectStackDto>(await fixture.Owner.Client.GetAsync($"{fixture.Path}/stack", Ct), HttpStatusCode.OK);

        Assert.Equal(("nextjs-supabase", "Next.js + Supabase", "1.0.1"), (stack.LegacyPreset!.PresetId, stack.LegacyPreset.PresetName, stack.LegacyPreset.PresetVersion));
        Assert.Equal("Preset nextjs-supabase@1.0.1 (retired, migrated)", stack.LegacyPreset.Label);
        Assert.Equal(("fe-nextjs", "data-supabase", (string?)null), (stack.Composition!.Frontend!.Pack, stack.Composition.Data!.Pack, stack.CatalogHash));
    }

    // ---------- §4.23: in-flight plans ----------

    [Fact]
    public async Task FR129_An_in_flight_db_engineer_step_runs_as_data_engineer_with_the_data_supabase_skill_and_records_legacyAgentKey()
    {
        var recording = new CompositionAgentTests.CapturingResponder(new FakeCodeGeneratorResponder());
        await using var app = AgentJobs.WithResponders(factory, recording);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await MigrateAsync(app, id);
        var steps = await SeedExecutionAsync(
            app,
            id,
            [
                (Step(1, "Skeleton", "package.json", "project-scaffolder"), null),
                (Step(2, "Profiles", "supabase/migrations/0001_profiles.sql", "db-engineer"), null),
                (Step(3, "Tasks", "app/api/tasks/route.ts", "api-engineer"), null),
                (Step(4, "Tags", "supabase/migrations/0002_tags.sql", "db-engineer"), null),
            ],
            completedThrough: 3);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        await AgentJobs.RunQueuedAsync(app, id);

        var run = (await AgentJobs.RunsAsync(app, id)).Single(r => r.StepId == steps[3].Id);
        Assert.Equal((AgentKind.CodeGenerator, "data-engineer", (string?)null), (run.Agent, run.AgentKey, run.PresetId));
        Assert.Contains("data-supabase", run.Packs, StringComparison.Ordinal);
        var system = recording.Requests[0].System;
        Assert.Contains("## Your role: Data engineer (data-engineer)", system, StringComparison.Ordinal);
        Assert.Contains("## Skill: data-supabase", system, StringComparison.Ordinal);

        var step = await ProjectApi.WithDbAsync(app, db => db.ExecutionSteps.AsNoTracking().SingleAsync(s => s.Id == steps[3].Id, Ct));
        Assert.Equal((StepStatus.Completed, "data-engineer", "db-engineer"), (step.Status, step.AgentKey, step.LegacyAgentKey));

        var dto = await ProjectApi.ReadAsync<AgentRunDetailDto>(await fixture.Owner.Client.GetAsync($"{fixture.Path}/runs/{run.Id}", Ct), HttpStatusCode.OK);
        Assert.Equal(("data-engineer", "db-engineer"), (dto.AgentKey, dto.LegacyAgentKey));
        var list = await ProjectApi.ReadAsync<PagedResult<AgentRunDto>>(await fixture.Owner.Client.GetAsync($"{fixture.Path}/runs", Ct), HttpStatusCode.OK);
        Assert.Equal("db-engineer", list.Items.Single(r => r.Id == run.Id).LegacyAgentKey);
    }

    [Fact]
    public async Task FR129_A_step_the_migration_already_mapped_runs_as_its_v4_specialist_and_reports_the_v2_key()
    {
        await using var app = AgentJobs.WithResponders(factory, new FakeCodeGeneratorResponder());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await MigrateAsync(app, id);
        var steps = await SeedExecutionAsync(app, id, [(Step(1, "Tags", "supabase/migrations/0001_tags.sql", StackAgents.DataEngineer), "db-engineer")], completedThrough: 0);
        await ProjectApi.ReadAsync<ExecutionStateDto>(await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);

        await AgentJobs.RunQueuedAsync(app, id);

        var run = (await AgentJobs.RunsAsync(app, id)).Single(r => r.StepId == steps[0].Id);
        Assert.Equal("data-engineer", run.AgentKey);
        var dto = await ProjectApi.ReadAsync<AgentRunDetailDto>(await fixture.Owner.Client.GetAsync($"{fixture.Path}/runs/{run.Id}", Ct), HttpStatusCode.OK);
        Assert.Equal("db-engineer", dto.LegacyAgentKey);
        Assert.DoesNotContain(AgentRunWarnings.LegacyAgentMapped, dto.Warnings);
    }
}
