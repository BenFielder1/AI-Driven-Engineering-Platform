using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Presets;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Versions;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Review;
using Aide.Infrastructure.Presets;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Presets;

/// <summary>
/// FR-74, §6: <c>GET /api/presets</c>, <c>GET /api/presets/:id</c>, <c>PATCH /api/projects/:id/preset</c>, <c>presetId</c> on
/// <c>POST /api/projects</c>, the preset fields of project rows and versions and the projects list <c>preset</c> filter —
/// on the test-only fixture preset <c>test-stack</c>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PresetEndpointsTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR74_GET_presets_lists_preset_cards_and_the_detail_has_agents_sections_env_review_and_protected_paths()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var list = await ProjectApi.ReadAsync<List<PresetSummaryDto>>(await user.Client.GetAsync(new Uri("/api/presets", UriKind.Relative), Ct));
        var card = Assert.Single(list, p => p.Id == TestPresetId.Id);
        Assert.Equal(("Test stack", TestPresetId.Version, "A fixture preset for tests."), (card.Name, card.Version, card.Description));
        Assert.Equal(["Next.js", "Supabase"], card.Stack);
        Assert.Equal(["vercel"], card.DeployTargets);
        Assert.Matches("^[0-9a-f]{64}$", card.ContentHash);
        Assert.Equal(list.Select(p => p.Id).Order(StringComparer.Ordinal), list.Select(p => p.Id));

        var detail = await ProjectApi.ReadAsync<PresetDetailDto>(await user.Client.GetAsync(new Uri($"/api/presets/{TestPresetId.Id}", UriKind.Relative), Ct));
        Assert.Equal(card.ContentHash, detail.ContentHash);
        var db = Assert.Single(detail.Agents, a => a.Key == "db-engineer");
        Assert.Equal(("Database engineer", "Migrations and RLS."), (db.Name, db.Description));
        Assert.Equal(["supabase/migrations/**", "supabase/seed.sql"], db.Owns);
        Assert.Equal(["supabase-migration"], db.Skills);
        Assert.Equal(["app/**", "!app/api/**", "components/**"], detail.Agents.Single(a => a.Key == "ui-engineer").Owns);
        Assert.Equal(["Data Model & RLS Policies", "Environment Variables"], detail.SpecSections.Select(s => s.Title));
        Assert.Equal(("SERVICE_KEY", true), (detail.RequiredEnv[1].Key, detail.RequiredEnv[1].Sensitive));
        Assert.Equal((ReviewCategory.Security, Severity.Critical), (detail.Review.Checklist[0].Category, detail.Review.Checklist[0].Severity));
        Assert.Equal(["rls_on_every_table", "no_secret_env_in_client"], detail.Review.Deterministic);
        Assert.Equal(new PresetProtectedPathDto("supabase/migrations/**", true, [], "immutable migrations"), detail.ProtectedPaths[0] with { Except = [] });
        Assert.Equal([".env.example"], detail.ProtectedPaths[2].Except);

        // No prompt bodies in the API.
        var raw = await user.Client.GetStringAsync(new Uri($"/api/presets/{TestPresetId.Id}", UriKind.Relative), Ct);
        Assert.DoesNotContain("You are the db-engineer", raw, StringComparison.Ordinal);

        await Problems.AssertAsync(await user.Client.GetAsync(new Uri("/api/presets/ml-stack", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/presets", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR74_A_project_created_with_a_preset_pins_id_version_and_hash_and_shows_the_preset_chip()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var name = ProjectApi.UniqueName("preset");

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, name, TestPresetId.Id), HttpStatusCode.Created);

        Assert.Equal((TestPresetId.Id, "Test stack", TestPresetId.Version), (project.PresetId, project.PresetName, project.PresetVersion));
        var version = await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Equal((TestPresetId.Id, TestPresetId.Version, Catalog().Find(TestPresetId.Id)!.ContentHash), (version.PresetId, version.PresetVersion, version.PresetHash));
        var created = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, project.Id, EventTypes.ProjectCreated)).Payload);
        Assert.Equal(TestPresetId.Id, created.GetProperty("presetId").GetString());

        var versions = await ProjectApi.ReadAsync<List<ReleaseVersionDto>>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/versions", UriKind.Relative), Ct));
        Assert.Equal((TestPresetId.Id, TestPresetId.Version, "Test stack"), (versions[0].PresetId, versions[0].PresetVersion, versions[0].PresetName));

        // The list carries the chip and filters on it (§7.2); a project without a preset has none.
        var plain = await ProjectApi.CreateAsync(owner.Client, name: name + "-plain");
        Assert.Equal(((string?)null, (string?)null, (string?)null), (plain.PresetId, plain.PresetName, plain.PresetVersion));
        var all = await PageAsync(owner.Client, $"search={name}");
        Assert.Equal([(name, (string?)TestPresetId.Id, (string?)"Test stack"), (name + "-plain", null, null)],
            all.Items.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (p.Name, p.PresetId, p.PresetName)));
        var filtered = await PageAsync(owner.Client, $"search={name}&preset={TestPresetId.Id}");
        Assert.Equal(project.Id, Assert.Single(filtered.Items).Id);
        Assert.Empty((await PageAsync(owner.Client, $"search={name}&preset=nextjs-unknown")).Items);
    }

    [Fact]
    public async Task FR74_Creating_a_project_with_an_unknown_preset_is_PRESET_NOT_FOUND()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var name = ProjectApi.UniqueName("nopreset");

        await Problems.AssertAsync(await CreateAsync(owner.Client, name, "ml-stack"), HttpStatusCode.BadRequest, ErrorCodes.PresetNotFound);

        Assert.Empty((await PageAsync(owner.Client, $"search={name}")).Items);
    }

    [Fact]
    public async Task FR74_The_Owner_changes_the_preset_until_the_plan_is_locked_then_it_is_PRESET_LOCKED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var changed = await ProjectApi.ReadAsync<ProjectDto>(await PatchAsync(fixture.Owner.Client, id, TestPresetId.Id));
        Assert.Equal((TestPresetId.Id, "Test stack", TestPresetId.Version), (changed.PresetId, changed.PresetName, changed.PresetVersion));
        var version = await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal((TestPresetId.Id, TestPresetId.Version, Catalog().Find(TestPresetId.Id)!.ContentHash), (version.PresetId, version.PresetVersion, version.PresetHash));
        var changedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PresetChanged)).Payload);
        Assert.Equal((version.Id.ToString(), (string?)null, TestPresetId.Id, TestPresetId.Version),
            (changedEvent.GetProperty("releaseVersionId").GetString(), changedEvent.GetProperty("fromPresetId").GetString(), changedEvent.GetProperty("presetId").GetString(), changedEvent.GetProperty("presetVersion").GetString()));

        // The same preset again changes nothing; the project reads it back.
        await ProjectApi.ReadAsync<ProjectDto>(await PatchAsync(fixture.Owner.Client, id, TestPresetId.Id));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PresetChanged));
        var read = await ProjectApi.ReadAsync<ProjectDto>(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct));
        Assert.Equal(TestPresetId.Id, read.PresetId);

        // Only Owners; outsiders don't see the project; unknown presets are refused.
        await Problems.AssertAsync(await PatchAsync(fixture.Contributor.Client, id, null), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await PatchAsync(fixture.Viewer.Client, id, null), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await PatchAsync(fixture.Outsider.Client, id, null), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await PatchAsync(fixture.Owner.Client, id, "ml-stack"), HttpStatusCode.BadRequest, ErrorCodes.PresetNotFound);

        // null = no preset (general).
        var cleared = await ProjectApi.ReadAsync<ProjectDto>(await PatchAsync(fixture.Owner.Client, id, null));
        Assert.Null(cleared.PresetId);
        Assert.Equal(2, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.PresetChanged)).Count);

        // A draft plan doesn't lock it; the first locked implementation plan does.
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft);
        await ProjectApi.ReadAsync<ProjectDto>(await PatchAsync(fixture.Owner.Client, id, TestPresetId.Id));
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Locked, version: 2);

        await Problems.AssertAsync(await PatchAsync(fixture.Owner.Client, id, null), HttpStatusCode.Conflict, ErrorCodes.PresetLocked);
        Assert.Equal(TestPresetId.Id, (await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct))).PresetId);
    }

    [Fact]
    public async Task FR74_A_preset_that_fails_to_load_makes_readiness_unhealthy_and_is_left_out()
    {
        using (var client = factory.CreateClient())
        {
            var ready = await client.GetAsync(new Uri("/api/health/ready", UriKind.Relative), Ct);
            var body = await ready.Content.ReadAsStringAsync(Ct);
            Assert.True(ready.StatusCode == HttpStatusCode.OK, body);
            Assert.Contains("\"presets\"", body, StringComparison.Ordinal);
        }

        await using var broken = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IPresetSource>(new BrokenPresetSource())));
        using var brokenClient = broken.CreateClient();

        var response = await brokenClient.GetAsync(new Uri("/api/health/ready", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("1 preset(s) failed to load: broken-stack.", text, StringComparison.Ordinal);
        var catalog = broken.Services.GetRequiredService<IPresetCatalog>();
        Assert.Null(catalog.Find("broken-stack"));
        Assert.Contains("agents[0].prompt references 'agents/a.md', which is missing.", Assert.Single(catalog.Errors).Reasons);
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("Preset broken-stack failed to load", StringComparison.Ordinal));
    }

    /// <summary>A preset whose agent prompt file is missing (§4.11: loading fails).</summary>
    private sealed class BrokenPresetSource : IPresetSource
    {
        public IReadOnlyList<PresetFolder> ReadFolders() =>
        [
            new PresetFolder("broken-stack", new Dictionary<string, string>
            {
                ["preset.json"] = """
                    {"id": "broken-stack", "name": "Broken", "version": "1.0.0", "description": "Missing a prompt.", "stack": ["x"],
                     "agents": [{"key": "a", "name": "A", "description": "A.", "owns": ["**"], "skills": [], "prompt": "agents/a.md"}]}
                    """,
            }),
        ];
    }

    private IPresetCatalog Catalog() => factory.Services.GetRequiredService<IPresetCatalog>();

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string presetId) =>
        client.PostAsJsonAsync("/api/projects", new { name, idea = "Build a todo app.", git = new { provider = "local" }, presetId }, Ct);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string? presetId) =>
        ProjectApi.PatchAsync(client, $"/api/projects/{id}/preset", new { presetId });

    private static async Task<PagedResult<ProjectSummaryDto>> PageAsync(HttpClient client, string query) =>
        await ProjectApi.ReadAsync<PagedResult<ProjectSummaryDto>>(await client.GetAsync(new Uri($"/api/projects?{query}", UriKind.Relative), Ct));
}
