using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Stack;
using Aide.Domain.Common;
using Aide.Domain.Stack;
using Aide.Infrastructure.Stack;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Stack;

/// <summary>
/// FR-113, FR-116, FR-122, §6: <c>GET /api/stack/catalog</c>, <c>POST /api/stack/options</c>, <c>POST /api/stack/validate</c>,
/// <c>GET /api/admin/stack</c> and the <c>stack-catalog</c> readiness check, on the shipped catalog.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StackEndpointsTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Route(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task FR113_GET_stack_catalog_lists_every_layer_option_with_its_source_pack_and_version()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var catalog = await ProjectApi.ReadAsync<StackCatalogDto>(await user.Client.GetAsync(Route("/api/stack/catalog"), Ct));

        Assert.Equal("1.1.0", catalog.Version);
        Assert.Matches("^[0-9a-f]{64}$", catalog.ContentHash);
        Assert.Equal(["frontend", "backend", "data", "vcs", "deploy"], catalog.Layers.Select(l => l.Id));
        Assert.Equal(StackChoices.Order, catalog.Choices.Select(c => c.Id));
        IReadOnlyList<StackOptionDto> Packs(string choice) => [.. catalog.Choices.Single(c => c.Id == choice).Options.Where(o => o.Id != StackIds.None)];
        Assert.Equal(["fe-react-vite", "fe-nextjs", "fe-angular", "fe-vue", "fe-vanilla"], Packs(StackChoices.Frontend).Select(o => o.Id));
        Assert.Equal(["be-aspnet", "be-spring-boot", "be-fastapi", "be-express", "be-nestjs"], Packs(StackChoices.Backend).Select(o => o.Id));
        Assert.Equal(7, Packs(StackChoices.Data).Count);
        Assert.Equal(3, Packs(StackChoices.Vcs).Count);
        Assert.Equal(6, Packs(StackChoices.FrontendHost).Count);
        Assert.Contains(catalog.Choices.Single(c => c.Id == StackChoices.Frontend).Options, o => o.Id == StackIds.None);

        foreach (var option in new[] { StackChoices.Frontend, StackChoices.Backend, StackChoices.Data, StackChoices.Vcs, StackChoices.FrontendHost }.SelectMany(Packs))
        {
            Assert.Equal(new StackPackSourceDto(option.Pack!, "1.0.0", "2026-10-25"), option.Source);
            Assert.Equal("1.0.0+aide.1", option.PackVersion);
            Assert.NotEmpty(option.Chips);
        }

        var nextjs = Packs(StackChoices.Frontend).Single(o => o.Id == "fe-nextjs");
        Assert.Equal(["full-stack", "ui"], nextjs.Modes.Select(m => m.Id));
        Assert.Equal("baas", Packs(StackChoices.Data).Single(o => o.Id == "data-supabase").Mode);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"R{i}"), catalog.Rules.Select(r => r.Id));
        Assert.Contains(catalog.Recommendations, r => r.Choices["backend"].Contains("be-aspnet"));

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route("/api/stack/catalog"), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR114_POST_stack_options_returns_option_states_with_reasons_and_invalidated_selections()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var options = await ProjectApi.ReadAsync<StackOptionsDto>(await user.Client.PostAsJsonAsync(
            "/api/stack/options", new { frontend = new { pack = "fe-react-vite" }, backend = new { pack = "be-aspnet" } }, Ct));

        var data = options.Choices.Single(c => c.Choice == StackChoices.Data);
        Assert.Equal(StackOptionState.Allowed, data.Options.Single(o => o.Id == "data-efcore-postgres").State);
        Assert.Equal(StackOptionState.Caveat, data.Options.Single(o => o.Id == "data-efcore-sqlserver").State);
        var prisma = data.Options.Single(o => o.Id == "data-prisma");
        Assert.Equal(StackOptionState.Forbidden, prisma.State);
        Assert.Equal(new StackReasonDto("R3", "Prisma needs Express, NestJS or Next.js full-stack"), Assert.Single(prisma.Reasons));

        // The wire format uses the spec's lower-case states.
        var raw = await user.Client.PostAsJsonAsync("/api/stack/options", new { frontend = new { pack = "fe-react-vite" }, backend = new { pack = "be-aspnet" }, data = new { pack = "data-sqlalchemy" } }, Ct);
        var json = JsonDocument.Parse(await raw.Content.ReadAsStringAsync(Ct)).RootElement;
        var dataChoice = json.GetProperty("choices").EnumerateArray().Single(c => c.GetProperty("choice").GetString() == "data");
        Assert.Equal("data-sqlalchemy", dataChoice.GetProperty("selected").GetString());
        var sqlalchemy = dataChoice.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("id").GetString() == "data-sqlalchemy");
        Assert.Equal("invalidated", sqlalchemy.GetProperty("state").GetString());
        Assert.Equal("SQLAlchemy needs FastAPI", sqlalchemy.GetProperty("reasons")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task FR115_POST_stack_options_gives_database_host_states_and_the_proxy_origin_model()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var options = await ProjectApi.ReadAsync<StackOptionsDto>(await user.Client.PostAsJsonAsync("/api/stack/options", new
        {
            frontend = new { pack = "fe-react-vite" },
            backend = new { pack = "be-aspnet" },
            data = new { pack = "data-efcore-postgres" },
            vcs = new { pack = "vcs-github" },
            deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres", local = "compose" },
        }, Ct));

        var db = options.Choices.Single(c => c.Choice == StackChoices.DatabaseHost).Options;
        Assert.Equal(StackOptionState.Caveat, db.Single(o => o.Id == "railway-postgres").State);
        Assert.Equal(StackOptionState.Allowed, db.Single(o => o.Id == "render-postgres").State);
        Assert.Equal("proxy", options.Choices.Single(c => c.Choice == StackChoices.OriginModel).Default);
    }

    [Fact]
    public async Task FR114_POST_stack_options_with_an_unknown_pack_is_VALIDATION()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PostAsJsonAsync("/api/stack/options", new { frontend = new { pack = "fe-svelte" } }, Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty("frontend.pack", out _));

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/stack/options", new { }, Ct)).StatusCode);
    }

    [Fact]
    public async Task FR116_POST_stack_validate_returns_the_normalised_composition_with_derived_fields()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var composition = await ProjectApi.ReadAsync<StackCompositionDto>(await user.Client.PostAsJsonAsync("/api/stack/validate", new
        {
            frontend = new { pack = "fe-react-vite" },
            backend = new { pack = "be-aspnet" },
            data = new { pack = "data-efcore-postgres" },
            vcs = new { pack = "vcs-gitlab" },
            deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
        }, Ct));

        Assert.Equal("1.1.0", composition.CatalogVersion);
        Assert.Equal(new StackComponentDto("fe-react-vite", "frontend"), composition.Frontend);
        Assert.Equal(new StackComponentDto("be-aspnet", "backend"), composition.Backend);
        Assert.Equal(new StackDeployDto("deploy-netlify", "deploy-render", "render-postgres", "compose"), composition.Deploy);
        Assert.Equal("proxy", composition.OriginModel);
        Assert.Equal(["proxy", "cross-origin"], composition.Derived.OriginModelOptions);
        Assert.Contains(StackAgents.BackendEngineer, composition.Derived.Agents);
        Assert.Equal("R9", Assert.Single(composition.Caveats).Rule);
    }

    [Fact]
    public async Task FR116_POST_stack_validate_rejects_Vue_AspNet_Prisma_with_STACK_INCOMPATIBLE_and_violations()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PostAsJsonAsync("/api/stack/validate", new
        {
            frontend = new { pack = "fe-vue" },
            backend = new { pack = "be-aspnet" },
            data = new { pack = "data-prisma" },
            vcs = new { pack = "vcs-github" },
        }, Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.StackIncompatible);
        var violation = Assert.Single(body.GetProperty("violations").EnumerateArray());
        Assert.Equal("data", violation.GetProperty("layer").GetString());
        Assert.Equal("data-prisma", violation.GetProperty("option").GetString());
        Assert.Equal("R3", violation.GetProperty("rule").GetString());
        Assert.Equal("Prisma needs Express, NestJS or Next.js full-stack", violation.GetProperty("reason").GetString());

        var missingVcs = await user.Client.PostAsJsonAsync("/api/stack/validate", new { frontend = new { pack = "fe-vue" } }, Ct);
        await Problems.AssertAsync(missingVcs, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR113_GET_admin_stack_lists_packs_with_source_version_hash_and_status_for_Admins_only()
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var catalog = await ProjectApi.ReadAsync<AdminStackCatalogDto>(await admin.Client.GetAsync(Route("/api/admin/stack"), Ct));

        Assert.Equal(("1.1.0", "loaded"), (catalog.Version, catalog.Status));
        Assert.Empty(catalog.Errors);
        Assert.Equal(27, catalog.Packs.Count);
        var aspnet = catalog.Packs.Single(p => p.Id == "be-aspnet");
        Assert.Equal(("backend", "1.0.0+aide.1", "loaded"), (aspnet.Layer, aspnet.Version, aspnet.Status));
        Assert.Equal(new StackPackSourceDto("be-aspnet", "1.0.0", "2026-10-25"), aspnet.Source);
        Assert.Matches("^[0-9a-f]{64}$", aspnet.ContentHash);

        using var user = await ApiSession.RegisterAsync(factory);
        await Problems.AssertAsync(await user.Client.GetAsync(Route("/api/admin/stack"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task FR122_A_broken_catalog_makes_the_stack_catalog_readiness_check_unhealthy()
    {
        using var client = factory.CreateClient();
        var healthy = await client.GetStringAsync(Route("/api/health/ready"), Ct);
        Assert.Contains("stack-catalog", healthy, StringComparison.Ordinal);

        await using var broken = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStackCatalogSource>(new BrokenSource())));
        using var brokenClient = broken.CreateClient();

        var response = await brokenClient.GetAsync(Route("/api/health/ready"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("stack-catalog", text, StringComparison.Ordinal);
        Assert.Contains("Gates", text, StringComparison.Ordinal);
    }

    /// <summary>The shipped catalog with a pack whose agent mapping names a "Gates" section its skill doesn't have (FR-122).</summary>
    private sealed class BrokenSource : IStackCatalogSource
    {
        public IReadOnlyDictionary<string, string> ReadFiles()
        {
            var files = new Dictionary<string, string>(EmbeddedStackCatalogSource.Shipped.ReadFiles(), StringComparer.Ordinal);
            var pack = System.Text.Json.Nodes.JsonNode.Parse(files["packs/be-aspnet/pack.json"])!;
            pack["agentSections"]!["test-engineer"] = new System.Text.Json.Nodes.JsonArray("Testing", "Gates");
            files["packs/be-aspnet/pack.json"] = pack.ToJsonString();
            return files;
        }
    }
}
