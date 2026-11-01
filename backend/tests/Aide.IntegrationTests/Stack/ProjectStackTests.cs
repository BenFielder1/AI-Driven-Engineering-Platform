using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Stack;
using Aide.Api.Features.Versions;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;
using Aide.Infrastructure.Stack;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Stack;

/// <summary>
/// FR-114–FR-117, FR-119, §4.17 R5, §6: <c>stack</c> on <c>POST /api/projects</c> and <c>POST /api/projects/:id/versions</c>,
/// <c>GET/PUT /api/projects/:id/stack</c> (STACK_LOCKED, VCS_MISMATCH, STACK_CHANGED) and the projects list <c>pack</c>
/// filter, on the shipped catalog.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectStackTests(AideApiFactory factory)
{
    private const string MergeSha = "89abcdef0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>§4.17 worked example 1 on GitHub: React + Vite on Netlify, ASP.NET on Render, Render Postgres (vcs left out).</summary>
    private static object Example1(string backend = "be-aspnet", string? backendPath = null, string data = "data-efcore-postgres") => new
    {
        frontend = new { pack = "fe-react-vite" },
        backend = new { pack = backend, path = backendPath },
        data = new { pack = data },
        deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
    };

    private static readonly string[] Example1Packs =
        ["fe-react-vite", "be-aspnet", "data-efcore-postgres", "vcs-github", "deploy-netlify", "deploy-render", "local-docker-desktop"];

    // ---------- POST /api/projects with a stack ----------

    [Fact]
    public async Task FR117_A_project_created_with_a_stack_pins_the_composition_with_the_catalog_and_lists_its_packs()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var name = ProjectApi.UniqueName("stack");

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, name, stack: Example1()), HttpStatusCode.Created);

        // R5: a Local repository has no host; the left-out VCS layer defaults to GitHub.
        Assert.Equal(Example1Packs, project.StackPacks);
        Assert.Null(project.PresetId);
        var catalog = Catalog();
        var version = await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Equal((catalog.Version, catalog.ContentHash), (version.CatalogVersion, version.CatalogHash));
        var stored = StackCompositionJson.Deserialize(version.StackComposition);
        Assert.NotNull(stored);
        Assert.Equal((new StackComponent("fe-react-vite", "frontend"), new StackComponent("be-aspnet", "backend"), "vcs-github", "proxy"),
            (stored.Frontend, stored.Backend, stored.Vcs.Pack, stored.OriginModel));
        var row = await ProjectApi.WithDbAsync(factory, db => db.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id, Ct));
        Assert.Equal(Example1Packs, row.StackPacks);
        var created = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, project.Id, EventTypes.ProjectCreated)).Payload);
        Assert.Equal(Example1Packs, created.GetProperty("stackPacks").EnumerateArray().Select(p => p.GetString()));

        var stack = await GetStackAsync(owner.Client, project.Id);
        Assert.Equal(("fe-react-vite", "be-aspnet", "data-efcore-postgres", "deploy-render"),
            (stack.Composition!.Frontend!.Pack, stack.Composition.Backend!.Pack, stack.Composition.Data!.Pack, stack.Composition.Deploy.BackendHost));
        Assert.Contains(StackAgents.BackendEngineer, stack.Composition.Derived.Agents);
        Assert.Equal((catalog.Version, catalog.ContentHash, catalog.Version, catalog.ContentHash),
            (stack.CatalogVersion, stack.CatalogHash, stack.CurrentCatalogVersion, stack.CurrentCatalogHash));
        Assert.Equal((false, (StackLockReason?)null, (LegacyPresetDto?)null, (string?)null), (stack.Locked, stack.LockedReason, stack.LegacyPreset, stack.RepoVcs));
        Assert.Equal((false, false), (stack.StackChange.BaseHasCode, stack.StackChange.IsStackChange));
        Assert.Empty(stack.StackChange.LayersChanged);

        // The projects list carries the packs and filters on them (§5 stack_packs).
        var plain = await ProjectApi.CreateAsync(owner.Client, name: name + "-plain");
        Assert.Empty(plain.StackPacks);
        var all = await PageAsync(owner.Client, $"search={name}");
        Assert.Equal(Example1Packs, all.Items.Single(p => p.Id == project.Id).StackPacks);
        Assert.Empty(all.Items.Single(p => p.Id == plain.Id).StackPacks);
        Assert.Equal(project.Id, Assert.Single((await PageAsync(owner.Client, $"search={name}&pack=be-aspnet")).Items).Id);
        Assert.Equal(project.Id, Assert.Single((await PageAsync(owner.Client, $"search={name}&pack=deploy-render")).Items).Id);
        Assert.Empty((await PageAsync(owner.Client, $"search={name}&pack=be-fastapi")).Items);
        var kpis = await ProjectApi.ReadAsync<ProjectKpisDto>(await owner.Client.GetAsync(new Uri($"/api/projects/kpis?search={name}&pack=be-aspnet", UriKind.Relative), Ct));
        Assert.Equal(1, kpis.Total);
    }

    [Fact]
    public async Task FR116_Creating_a_project_with_an_incompatible_stack_is_STACK_INCOMPATIBLE_with_violations_and_creates_nothing()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var name = ProjectApi.UniqueName("bad-stack");

        var body = await Problems.AssertAsync(
            await CreateAsync(owner.Client, name, stack: new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-aspnet" }, data = new { pack = "data-prisma" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.StackIncompatible);

        var violation = body.GetProperty("violations").EnumerateArray().Single(v => v.GetProperty("layer").GetString() == "data");
        Assert.Equal(("data-prisma", "R3"), (violation.GetProperty("option").GetString(), violation.GetProperty("rule").GetString()));
        Assert.Empty((await PageAsync(owner.Client, $"search={name}")).Items);

        // Malformed choices are VALIDATION on the stack.* fields (FR-119: overlapping paths).
        var invalid = await Problems.AssertAsync(
            await CreateAsync(owner.Client, name, stack: new { frontend = new { pack = "fe-vue", path = "." }, backend = new { pack = "be-express" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
        Assert.True(invalid.GetProperty("errors").TryGetProperty("stack.frontend.path", out _), invalid.ToString());
    }

    [Fact]
    public async Task FR129_A_presetId_from_an_old_client_is_ignored_and_never_pinned()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, ProjectApi.UniqueName("old-client"), stack: Example1(), presetId: "nextjs-supabase"), HttpStatusCode.Created);

        Assert.Null(project.PresetId);
        Assert.Equal(Example1Packs, project.StackPacks);
        var version = await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Null(version.PresetId);
    }

    [Fact]
    public async Task FR117_R5_A_GitHub_project_with_another_VCS_is_VCS_MISMATCH()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var name = ProjectApi.UniqueName("vcs");
        var git = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo-app", token = "ghp_example-token" };

        var stack = new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-express" }, vcs = new { pack = "vcs-gitlab" } };
        await Problems.AssertAsync(await CreateAsync(owner.Client, name, stack: stack, git: git), HttpStatusCode.BadRequest, ErrorCodes.VcsMismatch);
        Assert.Empty((await PageAsync(owner.Client, $"search={name}")).Items);

        // Left out, the VCS layer is set from the repository host.
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, name, stack: Example1(), git: git), HttpStatusCode.Created);
        Assert.Contains("vcs-github", project.StackPacks);
        Assert.Equal("vcs-github", (await GetStackAsync(owner.Client, project.Id)).RepoVcs);
    }

    // ---------- GET / PUT /api/projects/:id/stack ----------

    [Fact]
    public async Task FR117_The_Owner_changes_the_stack_until_the_plan_is_locked_then_it_is_STACK_LOCKED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        // No stack yet (general): any member reads it.
        var empty = await GetStackAsync(fixture.Viewer.Client, id);
        Assert.Equal(((StackCompositionDto?)null, false, fixture.Project.ReleaseVersionId), (empty.Composition, empty.Locked, (Guid?)empty.ReleaseVersionId));
        await Problems.AssertAsync(await GetStackRawAsync(fixture.Outsider.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        // Only Owners change it; outsiders don't see the project.
        await Problems.AssertAsync(await PutStackAsync(fixture.Contributor.Client, id, Example1()), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await PutStackAsync(fixture.Viewer.Client, id, Example1()), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await PutStackAsync(fixture.Outsider.Client, id, Example1()), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var changed = await ProjectApi.ReadAsync<ProjectStackDto>(await PutStackAsync(fixture.Owner.Client, id, Example1()));
        Assert.Equal(("be-aspnet", "backend"), (changed.Composition!.Backend!.Pack, changed.Composition.Backend.Path));
        var first = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StackChanged)).Payload);
        Assert.Equal(changed.ReleaseVersionId.ToString(), first.GetProperty("releaseVersionId").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("from").ValueKind);
        Assert.Equal("be-aspnet", first.GetProperty("to").GetProperty("backend").GetProperty("pack").GetString());
        Assert.Equal(StackLayers.CatalogLayers, first.GetProperty("layersChanged").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(Example1Packs, (await ProjectApi.ReadAsync<ProjectDto>(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct))).StackPacks);

        // The same composition again changes nothing.
        await ProjectApi.ReadAsync<ProjectStackDto>(await PutStackAsync(fixture.Owner.Client, id, Example1()));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StackChanged));

        // Changing the backend records the layers that changed.
        var express = await ProjectApi.ReadAsync<ProjectStackDto>(await PutStackAsync(fixture.Owner.Client, id, new
        {
            frontend = new { pack = "fe-react-vite" },
            backend = new { pack = "be-express", path = "api" },
            data = new { pack = "data-prisma" },
            deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
        }));
        Assert.Equal(new StackComponentDto("be-express", "api"), express.Composition!.Backend);
        var second = LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(factory, id, EventTypes.StackChanged)).Last().Payload);
        Assert.Equal([StackLayers.Backend, StackLayers.Data], second.GetProperty("layersChanged").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("be-aspnet", second.GetProperty("from").GetProperty("backend").GetProperty("pack").GetString());

        // Incompatible and malformed compositions are refused.
        var incompatible = await Problems.AssertAsync(
            await PutStackAsync(fixture.Owner.Client, id, new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-aspnet" }, data = new { pack = "data-prisma" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.StackIncompatible);
        Assert.Contains(incompatible.GetProperty("violations").EnumerateArray(), v => v.GetProperty("rule").GetString() == "R3");
        var malformed = await Problems.AssertAsync(await PutStackAsync(fixture.Owner.Client, id, new { frontend = new { pack = "fe-svelte" } }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(malformed.GetProperty("errors").TryGetProperty("frontend.pack", out _), malformed.ToString());

        // A draft plan doesn't lock it; the first locked implementation plan does.
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Draft);
        await ProjectApi.ReadAsync<ProjectStackDto>(await PutStackAsync(fixture.Owner.Client, id, Example1()));
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, ArtifactStatus.Locked, version: 2);

        // Given a locked implementation plan, when an Owner changes the backend, then STACK_LOCKED.
        await Problems.AssertAsync(await PutStackAsync(fixture.Owner.Client, id, Example1("be-express")), HttpStatusCode.Conflict, ErrorCodes.StackLocked);
        var locked = await GetStackAsync(fixture.Viewer.Client, id);
        Assert.Equal((true, (StackLockReason?)StackLockReason.PlanLocked, "be-aspnet"), (locked.Locked, locked.LockedReason, locked.Composition!.Backend!.Pack));
        var raw = await (await GetStackRawAsync(fixture.Viewer.Client, id)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"lockedReason\":\"plan_locked\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR117_R5_On_a_GitHub_project_PUT_stack_with_another_VCS_is_VCS_MISMATCH()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/shop", token = "ghp_example-token" });
        var id = fixture.Project.Id;

        var body = await Problems.AssertAsync(
            await PutStackAsync(fixture.Owner.Client, id, new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-express" }, vcs = new { pack = "vcs-bitbucket" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.VcsMismatch);
        Assert.Contains("vcs-github", body.GetProperty("details")[0].GetString(), StringComparison.Ordinal);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StackChanged));

        var stack = await ProjectApi.ReadAsync<ProjectStackDto>(await PutStackAsync(fixture.Owner.Client, id, new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-express" }, vcs = new { pack = "vcs-github" } }));
        Assert.Equal(("vcs-github", "vcs-github"), (stack.Composition!.Vcs.Pack, stack.RepoVcs));
    }

    [Fact]
    public async Task FR129_Changing_the_stack_of_a_migrated_version_keeps_its_retired_preset_as_history()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.Where(v => v.ProjectId == id).ExecuteUpdateAsync(
            s => s.SetProperty(v => v.PresetId, "nextjs-supabase").SetProperty(v => v.PresetVersion, "1.0.1").SetProperty(v => v.PresetHash, "abc"), Ct));

        var legacy = await GetStackAsync(fixture.Viewer.Client, id);
        Assert.Null(legacy.Composition);
        Assert.Equal(("nextjs-supabase", "Next.js + Supabase", (string?)"1.0.1"), (legacy.LegacyPreset!.PresetId, legacy.LegacyPreset.PresetName, legacy.LegacyPreset.PresetVersion));
        Assert.Equal("Preset nextjs-supabase@1.0.1 (retired, migrated)", legacy.LegacyPreset.Label);

        var stack = await ProjectApi.ReadAsync<ProjectStackDto>(await PutStackAsync(fixture.Owner.Client, id, Example1()));

        Assert.Equal("nextjs-supabase", stack.LegacyPreset?.PresetId);
        var version = await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal(("nextjs-supabase", "1.0.1", "abc"), (version.PresetId, version.PresetVersion, version.PresetHash));
        var changed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StackChanged)).Payload);
        Assert.Equal("nextjs-supabase", changed.GetProperty("fromPresetId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await ProjectApi.PatchAsync(fixture.Owner.Client, $"/api/projects/{id}/preset", new { presetId = "nextjs-supabase" })).StatusCode);
    }

    // ---------- POST /api/projects/:id/versions with and without a stack ----------

    [Fact]
    public async Task FR117_A_new_version_keeps_the_base_versions_stack_re_pinned_to_the_current_catalog()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, ProjectApi.UniqueName("keep"), stack: Example1(), initialVersion: "1.0.0"), HttpStatusCode.Created);
        await MarkReleasedAsync(project.Id);

        // A stale pin (an older catalog) is re-pinned when the next version starts.
        await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.Where(v => v.ProjectId == project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.CatalogHash, "stale"), Ct));

        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(owner.Client, project.Id, "1.1.0"), HttpStatusCode.Created);

        var catalog = Catalog();
        var version = await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.Id == started.ReleaseVersion.Id, Ct));
        Assert.Equal((catalog.Version, catalog.ContentHash), (version.CatalogVersion, version.CatalogHash));
        Assert.Equal("be-aspnet", StackCompositionJson.Deserialize(version.StackComposition)!.Backend!.Pack);
        var stack = await GetStackAsync(owner.Client, project.Id);
        Assert.Equal((started.ReleaseVersion.Id, false, true), (stack.ReleaseVersionId, stack.Locked, stack.StackChange.BaseHasCode));
        Assert.Equal((false, 0), (stack.StackChange.IsStackChange, stack.StackChange.LayersChanged.Count));
        Assert.Equal(Example1Packs, (await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct))).StackPacks);
    }

    [Fact]
    public async Task FR117_A_new_version_with_a_changed_backend_is_a_stack_change_on_code()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, ProjectApi.UniqueName("change"), stack: Example1(), initialVersion: "1.0.0"), HttpStatusCode.Created);
        await MarkReleasedAsync(project.Id);

        // Validated like project create.
        await Problems.AssertAsync(
            await StartAsync(owner.Client, project.Id, "1.1.0", new { frontend = new { pack = "fe-vue" }, backend = new { pack = "be-aspnet" }, data = new { pack = "data-prisma" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.StackIncompatible);

        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(
            await StartAsync(owner.Client, project.Id, "1.1.0", Example1("be-express", "server", "data-prisma")),
            HttpStatusCode.Created);

        var stack = await GetStackAsync(owner.Client, project.Id);
        Assert.Equal((started.ReleaseVersion.Id, "be-express"), (stack.ReleaseVersionId, stack.Composition!.Backend!.Pack));
        Assert.Equal((true, true), (stack.StackChange.BaseHasCode, stack.StackChange.IsStackChange));
        Assert.Equal([StackLayers.Backend, StackLayers.Data], stack.StackChange.LayersChanged);
        Assert.Contains("be-express", (await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct))).StackPacks);
        var startedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, project.Id, EventTypes.VersionStarted)).Payload);
        Assert.Contains("be-express", startedEvent.GetProperty("stackPacks").EnumerateArray().Select(p => p.GetString()));
    }

    // ---------- helpers ----------

    private StackCatalog Catalog() => factory.Services.GetRequiredService<IStackCatalog>().Current;

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, object? stack = null, string? presetId = null, object? git = null, string? initialVersion = null) =>
        client.PostAsJsonAsync("/api/projects", new { name, idea = "Build a todo app.", git = git ?? new { provider = "local" }, stack, presetId, initialVersion }, Ct);

    private static Task<HttpResponseMessage> GetStackRawAsync(HttpClient client, Guid id) =>
        client.GetAsync(new Uri($"/api/projects/{id}/stack", UriKind.Relative), Ct);

    private static async Task<ProjectStackDto> GetStackAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ProjectStackDto>(await GetStackRawAsync(client, id));

    private static Task<HttpResponseMessage> PutStackAsync(HttpClient client, Guid id, object composition) =>
        client.PutAsJsonAsync($"/api/projects/{id}/stack", composition, Ct);

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid id, string version, object? stack = null, string? presetId = null) =>
        client.PostAsJsonAsync($"/api/projects/{id}/versions", new { version, request = "Next iteration", stack, presetId }, Ct);

    private static async Task<PagedResult<ProjectSummaryDto>> PageAsync(HttpClient client, string query) =>
        await ProjectApi.ReadAsync<PagedResult<ProjectSummaryDto>>(await client.GetAsync(new Uri($"/api/projects?{query}", UriKind.Relative), Ct));

    /// <summary>The project's only version released at <see cref="MergeSha"/>, the project released with nothing in development.</summary>
    private Task MarkReleasedAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.ReleaseVersions.Where(v => v.ProjectId == projectId).ExecuteUpdateAsync(
                s => s.SetProperty(v => v.Status, ReleaseVersionStatus.Released)
                    .SetProperty(v => v.Tag, v => "v" + v.Version)
                    .SetProperty(v => v.MergeCommitSha, MergeSha)
                    .SetProperty(v => v.ReleasedAt, DateTimeOffset.UtcNow),
                Ct);
            var released = await db.ReleaseVersions.Where(v => v.ProjectId == projectId).Select(v => v.Version).SingleAsync(Ct);
            await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.CurrentPhase, Phase.Release).SetProperty(p => p.Status, ProjectStatus.Released)
                    .SetProperty(p => p.ReleasedVersion, released).SetProperty(p => p.DevelopmentVersion, (string?)null),
                Ct);
        });
}
