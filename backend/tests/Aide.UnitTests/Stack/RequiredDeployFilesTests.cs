using System.Text.Json.Nodes;
using Aide.Domain.Agents;
using Aide.Domain.Execution;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-241, FR-242, §4.40: the deploy packs' <c>requiredFiles</c> (loaded and validated), the files a composition needs, the
/// plan guidance that names them, and the plan prompt v7 and Fake plan that follow it.
/// </summary>
public sealed class RequiredDeployFilesTests
{
    private const string VercelPack = "packs/deploy-vercel/pack.json";

    private static StackResolution Resolve(StackSelection selection) => StackResolverTests.Resolve(selection);

    private static StackResolution NextVercel() => Resolve(Sel("fe-nextjs", "none", "none", "vcs-github", frontendHost: "deploy-vercel"));

    // ---------- FR-241: pack.json ----------

    [Theory]
    [InlineData("deploy-vercel", "vercel.json", "", "deploy-engineer", "app")]
    [InlineData("deploy-netlify", "netlify.toml", "", "deploy-engineer", "app")]
    [InlineData("deploy-cloudflare", "wrangler.jsonc", "wrangler.toml,wrangler.json", "deploy-engineer", "app")]
    [InlineData("deploy-render", "render.yaml", "", "deploy-engineer", "root")]
    [InlineData("deploy-railway", "railway.json", "railway.toml", "deploy-engineer", "app")]
    [InlineData("deploy-github-pages", ".github/workflows/pages.yml", "", "deploy-engineer", "root")]
    public void FR241_Every_deploy_pack_declares_its_required_file_with_owner_folder_and_content(string pack, string path, string alternatives, string owner, string folder)
    {
        Assert.Equal("1.2.2", Catalog.Version);
        var file = Assert.Single(Catalog.Packs[pack].Manifest.RequiredFiles);

        Assert.Equal(path, file.Path);
        Assert.Equal(alternatives.Split(',', StringSplitOptions.RemoveEmptyEntries), file.Alternatives);
        Assert.Equal(owner, file.Owner);
        Assert.Equal(folder, file.Folder);
        Assert.NotEmpty(file.Content);
    }

    [Fact]
    public void FR241_The_Vercel_and_Netlify_content_blocks_both_branch_prefixes()
    {
        Assert.Contains(Catalog.Packs["deploy-vercel"].Manifest.RequiredFiles[0].Content, l => l.Contains("\"tevrin/**\": false", StringComparison.Ordinal) && l.Contains("\"aide/**\": false", StringComparison.Ordinal));
        Assert.Contains(Catalog.Packs["deploy-netlify"].Manifest.RequiredFiles[0].Content, l => l.Contains("tevrin/", StringComparison.Ordinal) && l.Contains("aide/", StringComparison.Ordinal));
        Assert.All(Catalog.Packs.Values.Where(p => p.Manifest.Layer != StackLayers.Deploy), p => Assert.Empty(p.Manifest.RequiredFiles));
    }

    [Fact]
    public void FR241_An_unknown_owner_agent_fails_loading_naming_the_pack()
    {
        var files = ShippedFiles();
        EditJson(files, VercelPack, p => p["requiredFiles"]![0]!["owner"] = "release-manager");

        var result = StackCatalogLoader.Load(files);

        Assert.Null(result.Catalog);
        Assert.Contains(result.Errors, e => e.Pack == "deploy-vercel" && e.Message.Contains("'release-manager'", StringComparison.Ordinal));
    }

    [Fact]
    public void FR241_A_bad_folder_glob_path_or_empty_content_fails_loading()
    {
        var files = ShippedFiles();
        EditJson(files, VercelPack, p => p["requiredFiles"] = JsonNode.Parse("""
            [
              { "path": "**/vercel.json", "alternatives": ["../x.json"], "owner": "deploy-engineer", "folder": "everywhere", "content": [] }
            ]
            """));

        var errors = StackCatalogLoader.Load(files).Errors.Where(e => e.Pack == "deploy-vercel").Select(e => e.Message).ToList();

        Assert.Contains(errors, m => m.Contains("requiredFiles[0].path", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("requiredFiles[0].alternatives[0]", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("requiredFiles[0].folder 'everywhere'", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("requiredFiles[0].content", StringComparison.Ordinal));
    }

    [Fact]
    public void FR241_Required_files_are_for_deploy_packs_only()
    {
        var files = ShippedFiles();
        EditJson(files, "packs/be-aspnet/pack.json", p => p["requiredFiles"] = JsonNode.Parse("""
            [{ "path": "Dockerfile", "owner": "deploy-engineer", "folder": "app", "content": ["FROM …"] }]
            """));

        Assert.Contains(StackCatalogLoader.Load(files).Errors, e => e.Pack == "be-aspnet" && e.Message.Contains("deploy packs only", StringComparison.Ordinal));
    }

    // ---------- §4.40: the files a composition needs ----------

    [Fact]
    public void FR242_The_files_follow_the_folder_rule_and_the_catalog_order()
    {
        Assert.Equal(["frontend/vercel.json"], StackRequiredFiles.For(NextVercel()).Select(f => f.Path));

        // Netlify hosts the frontend (its folder), Render the backend (render.yaml at the root, once).
        var split = Resolve(StackResolverTests.AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));
        Assert.Equal(["frontend/netlify.toml", "render.yaml"], StackRequiredFiles.For(split).Select(f => f.Path));

        // One pack hosting both components: one file in each folder.
        var railway = Resolve(StackResolverTests.AspnetSpa(frontendHost: "deploy-railway", backendHost: "deploy-railway", databaseHost: "railway-postgres"));
        var files = StackRequiredFiles.For(railway);
        Assert.Equal(["frontend/railway.json", "backend/railway.json"], files.Select(f => f.Path));
        Assert.Equal(["backend/railway.toml"], files[1].Alternatives);
        Assert.All(files, f => Assert.Equal(("deploy-railway", "Railway", StackAgents.DeployEngineer), (f.Pack, f.Platform, f.Owner)));
    }

    [Fact]
    public void FR242_A_database_only_host_needs_no_file()
    {
        var stack = Resolve(StackResolverTests.AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));
        Assert.Contains(stack.Packs, p => p.Id == "deploy-render");

        var vercelDb = Resolve(Sel("fe-nextjs", "none", "data-prisma", "vcs-github", frontendHost: "deploy-vercel", databaseHost: "railway-postgres"));
        Assert.Equal(["frontend/vercel.json"], StackRequiredFiles.For(vercelDb).Select(f => f.Path));
    }

    // ---------- FR-242: the plan guidance and prompt ----------

    [Fact]
    public void FR242_The_plan_guidance_of_Next_js_on_Vercel_lists_vercel_json_with_its_owner_and_content()
    {
        var guidance = StackGuidance.Plan(NextVercel(), repoEmpty: true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains(StackGuidance.DeploymentFilesHeading, guidance, StringComparison.Ordinal);
        Assert.Contains("- `frontend/vercel.json` for Vercel, owner `deploy-engineer`. Required content:", guidance, StringComparison.Ordinal);
        foreach (var line in Catalog.Packs["deploy-vercel"].Manifest.RequiredFiles[0].Content)
        {
            Assert.Contains("  - " + line, guidance, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FR242_A_file_the_base_already_has_is_marked_and_a_stack_without_deploy_target_has_no_section()
    {
        var present = StackGuidance.Plan(NextVercel(), repoEmpty: false, new HashSet<string>(["frontend/vercel.json"], StringComparer.OrdinalIgnoreCase));
        Assert.Contains("- `frontend/vercel.json` for Vercel, owner `deploy-engineer`: already in the repository.", present, StringComparison.Ordinal);
        Assert.DoesNotContain("Required content:", present, StringComparison.Ordinal);

        Assert.DoesNotContain(StackGuidance.DeploymentFilesHeading, StackGuidance.Plan(Resolve(StackResolverTests.AspnetSpa()), repoEmpty: true), StringComparison.Ordinal);
    }

    [Fact]
    public void FR242_plan_generator_v7_asks_for_a_step_for_each_missing_deployment_file()
    {
        var template = new EmbeddedPromptLibrary().Template(AgentKind.PlanGenerator);

        Assert.Equal("7", template.Version);
        var rendered = template.Render(new Dictionary<string, string?>
        {
            ["PROJECT_REQUEST"] = "x",
            ["TECHNICAL_SPECIFICATION"] = "y",
            ["MAX_FILES_PER_STEP"] = "20",
            ["STACK_GUIDANCE"] = StackGuidance.Plan(NextVercel(), repoEmpty: true, new HashSet<string>()),
        }).Value;
        Assert.Contains("**Deployment files**", rendered.System, StringComparison.Ordinal);
        Assert.Contains("Tevrin adds a step for any", rendered.System, StringComparison.Ordinal);
        Assert.Contains("`frontend/vercel.json` for Vercel", rendered.System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fe-nextjs", "none", "none", "deploy-vercel", null, null)]
    [InlineData("fe-react-vite", "be-fastapi", "none", "deploy-github-pages", "deploy-railway", null)]
    [InlineData("fe-react-vite", "be-aspnet", "data-efcore-postgres", "deploy-railway", "deploy-railway", "railway-postgres")]
    public void FR242_The_Fake_plan_for_a_new_repository_creates_every_deployment_file_so_Tevrin_adds_none(
        string fe, string be, string data, string feHost, string? beHost, string? dbHost)
    {
        var stack = Resolve(Sel(fe, be, data, "vcs-github", frontendHost: feHost, backendHost: beHost, databaseHost: dbHost));
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var system = new EmbeddedPromptLibrary().Template(AgentKind.PlanGenerator).Render(new Dictionary<string, string?>
        {
            ["PROJECT_REQUEST"] = "# Demo: Recipe Finder",
            ["TECHNICAL_SPECIFICATION"] = "## 2. Functional Requirements\n- **FR-1** x. Given a, when b, then c.",
            ["MAX_FILES_PER_STEP"] = "20",
            ["STACK_GUIDANCE"] = StackGuidance.Plan(stack, repoEmpty: true, empty),
        }).Value;
        var reply = new FakePlanGeneratorResponder().Respond(new LlmRequest
        {
            Model = "fake-1",
            System = system.System,
            Messages = [new LlmMessage(LlmRole.User, system.User)],
            Agent = AgentKind.PlanGenerator,
        });
        var check = new DeployFileCheck(StackRequiredFiles.For(stack), empty, Optimisation: false);

        var plan = PlanDocument.ParseAgentOutput(reply, 20, PlanAgents.ForStack(stack, repoEmpty: true, empty), check);

        Assert.True(plan.IsSuccess, plan.Error is null ? null : string.Join(" | ", plan.Error.Details));
        Assert.NotEmpty(check.Required);
        Assert.All(plan.Value, s => Assert.Null(s.AddedBy));
        Assert.All(check.Required, f => Assert.Contains(plan.Value, s => s.Agent == StackAgents.DeployEngineer && s.Files.Any(d => d.Path == f.Path)));
    }
}
