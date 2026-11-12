using System.Text.Json;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Execution;
using Aide.Domain.Stack;
using Aide.UnitTests.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Execution;

/// <summary>
/// FR-243, FR-244, §4.41: Tevrin adds a step for each required deployment file a plan or optimisation plan leaves out, and
/// the stored plan document carries <c>addedBy</c>/<c>addedReason</c> for those steps only.
/// </summary>
public sealed class DeployFileStepsTests
{
    private static readonly HashSet<string> EmptyBase = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] NetlifyThenCloudflare = ["deploy-netlify", "deploy-cloudflare"];

    /// <summary>Next.js (full-stack, at <c>frontend/</c>) deployed to Vercel.</summary>
    private static StackResolution NextVercel() => StackResolverTests.Resolve(Sel("fe-nextjs", "none", "none", "vcs-github", frontendHost: "deploy-vercel"));

    private static PlanStep Step(int number, string agent, string path, params int[] dependencies) =>
        new(number, $"Step {number}", $"Do step {number}.", [new DeclaredFile(path, FileAction.Create)], dependencies, Agent: agent);

    /// <summary>A 6-step Next.js plan with no step touching vercel.json (step 4 depends on steps 1 and 3).</summary>
    private static List<PlanStep> SixStepPlan() =>
    [
        Step(1, StackAgents.Scaffolder, "frontend/package.json"),
        Step(2, StackAgents.FrontendEngineer, "frontend/src/app/page.tsx", 1),
        Step(3, StackAgents.FrontendEngineer, "frontend/src/app/api/recipes/route.ts", 2),
        Step(4, StackAgents.FrontendEngineer, "frontend/src/app/recipes/page.tsx", 1, 3),
        Step(5, StackAgents.TestEngineer, "frontend/e2e/recipes.spec.ts", 4),
        Step(6, StackAgents.DocsWriter, "README.md", 5),
    ];

    private static DeployFileCheck Check(StackResolution stack, IReadOnlySet<string>? repoFiles, bool optimisation = false) =>
        new(StackRequiredFiles.For(stack), repoFiles, optimisation);

    private static Result<IReadOnlyList<PlanStep>> Validate(IReadOnlyList<PlanStep> steps, StackResolution stack) =>
        PlanValidator.Validate(steps, maxFilesPerStep: 20, allowProtectedPaths: false, PlanAgents.ForStack(stack, repoEmpty: true, EmptyBase));

    private static string Reply(IEnumerable<PlanStep> steps) => JsonSerializer.Serialize(new
    {
        steps = steps.Select(s => new
        {
            number = s.Number,
            title = s.Title,
            task = s.Task,
            files = s.Files.Select(f => new { path = f.Path, action = f.Action.ToWire() }),
            dependencies = s.Dependencies,
            agent = s.Agent,
        }),
    });

    [Fact]
    public void FR243_A_6_step_Vercel_plan_without_vercel_json_gets_step_2_from_the_deploy_engineer_and_7_steps()
    {
        var stack = NextVercel();

        var steps = DeployFileSteps.Add(SixStepPlan(), Check(stack, EmptyBase));

        Assert.Equal(7, steps.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], steps.Select(s => s.Number));
        var added = steps[1];
        Assert.Equal(StackAgents.DeployEngineer, added.Agent);
        Assert.Equal("Add frontend/vercel.json for Vercel", added.Title);
        Assert.Equal([new DeclaredFile("frontend/vercel.json", FileAction.Create)], added.Files);
        Assert.Equal([1], added.Dependencies);
        Assert.Equal(PlanStep.AddedByTevrin, added.AddedBy);
        Assert.Equal("frontend/vercel.json was missing from the plan", added.AddedReason);
        Assert.Contains("\"tevrin/**\": false", added.Task, StringComparison.Ordinal);
        Assert.Contains("\"aide/**\": false", added.Task, StringComparison.Ordinal);
        Assert.Contains("$schema", added.Task, StringComparison.Ordinal);

        // Steps 2–6 become 3–7 and their dependencies follow; step 1 keeps its number.
        Assert.Equal(["Step 1", "Add frontend/vercel.json for Vercel", "Step 2", "Step 3", "Step 4", "Step 5", "Step 6"], steps.Select(s => s.Title));
        Assert.Equal([[], [1], [1], [3], [1, 4], [5], [6]], steps.Select(s => s.Dependencies.ToArray()));
        Assert.All(steps.Where(s => s != added), s => Assert.Null(s.AddedBy));
    }

    [Fact]
    public void FR243_The_added_plan_passes_PlanValidator_and_MISSING_VERCEL_CONFIG_is_no_longer_raised()
    {
        var stack = NextVercel();
        Assert.NotNull(VercelConfigRules.PlanWarning(SixStepPlan(), deploysToVercel: true, EmptyBase, stack.Composition.Frontend!.Path));

        var steps = DeployFileSteps.Add(SixStepPlan(), Check(stack, EmptyBase));
        var validated = Validate(steps, stack);

        Assert.True(validated.IsSuccess, validated.Error is null ? null : string.Join(" | ", validated.Error.Details));
        Assert.Equal(PlanStep.AddedByTevrin, validated.Value[1].AddedBy);
        Assert.Null(VercelConfigRules.PlanWarning(validated.Value, deploysToVercel: true, EmptyBase, stack.Composition.Frontend.Path));
    }

    [Fact]
    public void FR243_The_plan_generator_reply_is_stored_with_the_added_step_and_reads_back_with_its_provenance()
    {
        var stack = NextVercel();
        var agents = PlanAgents.ForStack(stack, repoEmpty: true, EmptyBase);

        var parsed = PlanDocument.ParseAgentOutput(Reply(SixStepPlan()), 20, agents, Check(stack, EmptyBase));

        Assert.True(parsed.IsSuccess, parsed.Error is null ? null : string.Join(" | ", parsed.Error.Details));
        Assert.Equal(7, parsed.Value.Count);
        var markdown = PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, parsed.Value, 20, agents);
        Assert.True(markdown.IsSuccess, markdown.Error?.Message);
        Assert.Contains("**Added by Tevrin:** frontend/vercel.json was missing from the plan", markdown.Value, StringComparison.Ordinal);
        var stored = PlanDocument.ParseMarkdown(markdown.Value, 20, agents).Value;
        Assert.Equal(PlanStep.AddedByTevrin, stored[1].AddedBy);
        Assert.Equal("frontend/vercel.json was missing from the plan", stored[1].AddedReason);
        Assert.All(stored.Where(s => s.Number != 2), s => Assert.Null(s.AddedBy));
    }

    [Fact]
    public void FR243_A_plan_whose_step_already_declares_vercel_json_is_unchanged()
    {
        var plan = SixStepPlan();
        plan[5] = plan[5] with { Files = [new DeclaredFile("README.md", FileAction.Create), new DeclaredFile("frontend/vercel.json", FileAction.Create)] };

        Assert.Same(plan, DeployFileSteps.Add(plan, Check(NextVercel(), EmptyBase)));
    }

    [Fact]
    public void FR243_A_plan_is_unchanged_when_the_base_repo_has_vercel_json()
    {
        var plan = SixStepPlan();
        var repo = new HashSet<string>(["frontend/package.json", "frontend/vercel.json"], StringComparer.OrdinalIgnoreCase);

        Assert.Same(plan, DeployFileSteps.Add(plan, Check(NextVercel(), repo)));
    }

    [Fact]
    public void FR243_Netlify_and_Cloudflare_files_missing_add_two_steps_in_pack_order_after_the_scaffold_step()
    {
        var stack = StackResolverTests.Resolve(Sel("fe-react-vite", "none", "none", "vcs-github", frontendHost: "deploy-netlify"));
        IReadOnlyList<StackRequiredFile> required = [.. NetlifyThenCloudflare.SelectMany(id =>
        {
            var manifest = Catalog.Packs[id].Manifest;
            return manifest.RequiredFiles.Select(f => new StackRequiredFile(id, manifest.Name, $"frontend/{f.Path}", [.. f.Alternatives.Select(a => $"frontend/{a}")], f.Owner, f.Content));
        })];
        var plan = new List<PlanStep>
        {
            Step(1, StackAgents.Scaffolder, "frontend/package.json"),
            Step(2, StackAgents.FrontendEngineer, "frontend/src/main.tsx", 1),
            Step(3, StackAgents.DocsWriter, "README.md", 2),
        };

        var steps = DeployFileSteps.Add(plan, new DeployFileCheck(required, EmptyBase, Optimisation: false));

        Assert.Equal(
            ["Step 1", "Add frontend/netlify.toml for Netlify", "Add frontend/wrangler.jsonc for Cloudflare", "Step 2", "Step 3"],
            steps.Select(s => s.Title));
        Assert.Equal([[], [1], [1], [1], [4]], steps.Select(s => s.Dependencies.ToArray()));
        Assert.All(steps.Skip(1).Take(2), s => Assert.Equal((StackAgents.DeployEngineer, PlanStep.AddedByTevrin), (s.Agent, s.AddedBy)));
        Assert.True(Validate(steps, stack).IsSuccess);

        // An alternative in the folder counts as present (wrangler.toml for wrangler.jsonc).
        var withToml = DeployFileSteps.Add(plan, new DeployFileCheck(required, new HashSet<string>(["frontend/wrangler.toml"]), Optimisation: false));
        Assert.Equal(["Step 1", "Add frontend/netlify.toml for Netlify", "Step 2", "Step 3"], withToml.Select(s => s.Title));
    }

    [Fact]
    public void FR243_A_stack_with_no_deploy_target_adds_nothing()
    {
        var stack = StackResolverTests.Resolve(StackResolverTests.AspnetSpa());
        var plan = SixStepPlan();

        Assert.Empty(StackRequiredFiles.For(stack));
        Assert.Same(plan, DeployFileSteps.Add(plan, Check(stack, EmptyBase)));
    }

    [Fact]
    public void FR243_Unknown_repository_files_add_nothing()
    {
        var plan = SixStepPlan();

        Assert.Same(plan, DeployFileSteps.Add(plan, Check(NextVercel(), repoFiles: null)));
    }

    [Fact]
    public void FR243_An_optimisation_plan_gets_the_added_step_first_with_every_step_and_dependency_renumbered()
    {
        var stack = NextVercel();
        var plan = new List<PlanStep>
        {
            new(1, "Fix the list page", "Fix it.", [new DeclaredFile("frontend/src/app/page.tsx", FileAction.Modify)], [], Agent: PlanAgents.Generalist),
            new(2, "Add a test", "Test it.", [new DeclaredFile("frontend/e2e/list.spec.ts", FileAction.Create)], [1], Agent: StackAgents.TestEngineer),
        };
        var branch = new HashSet<string>(["frontend/package.json", "frontend/src/app/page.tsx"], StringComparer.OrdinalIgnoreCase);

        var steps = DeployFileSteps.Add(plan, Check(stack, branch, optimisation: true));

        Assert.Equal(["Add frontend/vercel.json for Vercel", "Fix the list page", "Add a test"], steps.Select(s => s.Title));
        Assert.Equal([1, 2, 3], steps.Select(s => s.Number));
        Assert.Equal([[], [], [2]], steps.Select(s => s.Dependencies.ToArray()));
        Assert.Equal(PlanStep.AddedByTevrin, steps[0].AddedBy);
        var agents = PlanAgents.ForStackOptimization(stack, EmptyBase);
        var validated = PlanValidator.Validate(steps, 20, allowProtectedPaths: false, agents);
        Assert.True(validated.IsSuccess, validated.Error is null ? null : string.Join(" | ", validated.Error.Details));
        Assert.True(PlanDocument.RenderChecked(ArtifactType.OptimizationPlan, validated.Value, 20, agents).IsSuccess);

        // The branch already has it (an earlier step wrote it): unchanged. An empty optimisation plan stays empty.
        branch.Add("frontend/vercel.json");
        Assert.Same(plan, DeployFileSteps.Add(plan, Check(stack, branch, optimisation: true)));
        Assert.Empty(DeployFileSteps.Add([], Check(stack, EmptyBase, optimisation: true)));
    }

    [Fact]
    public void FR243_An_added_step_that_breaks_a_limit_is_INVALID_PLAN_and_never_stored()
    {
        var stack = NextVercel();
        var plan = Enumerable.Range(1, PlanValidator.MaxSteps)
            .Select(n => n == 1 ? Step(1, StackAgents.Scaffolder, "frontend/package.json") : Step(n, StackAgents.FrontendEngineer, $"frontend/src/f{n}.ts", n - 1))
            .ToList();

        var parsed = PlanDocument.ParseAgentOutput(Reply(plan), 20, PlanAgents.ForStack(stack, repoEmpty: true, EmptyBase), Check(stack, EmptyBase));

        Assert.True(parsed.IsFailure);
        Assert.Equal(ErrorCodes.InvalidPlan, parsed.Error!.Code);

        // The retry reason is in the agent's terms: the file Tevrin must add, its own step count, and how to make room.
        Assert.Equal(
            "Tevrin must add a step for frontend/vercel.json (no step declares it and the repository doesn't have it), but the plan has 100 steps, "
            + "so adding it would exceed 100. Include it yourself (declare frontend/vercel.json in a deploy-engineer step), or leave room for the added step.",
            parsed.Error.Details[0]);
        Assert.Contains("Tevrin must add a step for frontend/vercel.json", PlanDocument.CorrectiveFeedback(parsed.Error), StringComparison.Ordinal);
    }

    [Fact]
    public void FR243_An_invalid_agent_plan_is_rejected_with_its_own_step_numbers_before_any_step_is_added()
    {
        var stack = NextVercel();
        var plan = SixStepPlan();
        plan[2] = plan[2] with { Dependencies = [5] };

        var parsed = PlanDocument.ParseAgentOutput(Reply(plan), 20, PlanAgents.ForStack(stack, repoEmpty: true, EmptyBase), Check(stack, EmptyBase));

        Assert.True(parsed.IsFailure);
        Assert.Contains("Step 3 depends on step 5; dependencies must point to earlier steps.", parsed.Error!.Details);
        Assert.DoesNotContain(parsed.Error.Details, d => d.Contains("vercel.json", StringComparison.Ordinal));
    }

    [Fact]
    public void FR243_Running_the_rule_twice_adds_nothing_the_second_time()
    {
        var check = Check(NextVercel(), EmptyBase);
        var once = DeployFileSteps.Add(SixStepPlan(), check);

        Assert.Same(once, DeployFileSteps.Add(once, check));
    }

    [Fact]
    public void FR243_A_root_vercel_json_doesnt_count_for_a_frontend_at_frontend()
    {
        var repo = new HashSet<string>(["vercel.json", "frontend/package.json"], StringComparer.OrdinalIgnoreCase);

        var steps = DeployFileSteps.Add(SixStepPlan(), Check(NextVercel(), repo));

        Assert.Equal([new DeclaredFile("frontend/vercel.json", FileAction.Create)], steps[1].Files);
    }

    [Theory]
    [InlineData("deploy-railway", "deploy-railway", "railway-postgres", "frontend/railway.json,backend/railway.json")]
    [InlineData("deploy-github-pages", "deploy-railway", null, ".github/workflows/pages.yml,backend/railway.json")]
    public void FR243_Added_Railway_and_GitHub_Pages_steps_pass_the_composed_rules_of_a_generated_plan(string feHost, string beHost, string? dbHost, string paths)
    {
        var data = dbHost is null ? "none" : "data-efcore-postgres";
        var backend = dbHost is null ? "be-fastapi" : "be-aspnet";
        var stack = StackResolverTests.Resolve(Sel("fe-react-vite", backend, data, "vcs-github", frontendHost: feHost, backendHost: beHost, databaseHost: dbHost));
        var plan = new List<PlanStep>
        {
            Step(1, StackAgents.Scaffolder, "frontend/package.json"),
            Step(2, StackAgents.FrontendEngineer, "frontend/src/main.tsx", 1),
        };

        var steps = DeployFileSteps.Add(plan, Check(stack, EmptyBase));
        var validated = Validate(steps, stack);

        Assert.Equal(paths.Split(','), steps.Where(s => s.AddedBy is not null).Select(s => s.Files[0].Path));
        Assert.True(validated.IsSuccess, validated.Error is null ? null : string.Join(" | ", validated.Error.Details));
        Assert.Empty(CompositionPlanValidator.Errors(validated.Value, stack, repoEmpty: true));
    }

    [Fact]
    public void FR244_An_edit_keeps_provenance_only_on_steps_Tevrin_added_in_the_version_it_edits()
    {
        var previous = DeployFileSteps.Add(SixStepPlan(), Check(NextVercel(), EmptyBase));
        var edited = previous.Select(s => s.Number switch
        {
            2 => s with { AddedReason = "edited reason" },
            3 => s with { AddedBy = PlanStep.AddedByTevrin, AddedReason = "forged" },
            _ => s,
        }).ToList();

        var kept = DeployFileSteps.KeepProvenance(edited, previous);

        Assert.Equal((PlanStep.AddedByTevrin, "frontend/vercel.json was missing from the plan"), (kept[1].AddedBy, kept[1].AddedReason));
        Assert.Equal(((string?)null, (string?)null), (kept[2].AddedBy, kept[2].AddedReason));

        // A step Tevrin added whose files the human changed is no longer Tevrin's.
        edited[1] = edited[1] with { Files = [new DeclaredFile("frontend/vercel.json", FileAction.Create), new DeclaredFile("frontend/next.config.ts", FileAction.Create)] };
        Assert.Null(DeployFileSteps.KeepProvenance(edited, previous)[1].AddedBy);
        Assert.All(DeployFileSteps.KeepProvenance(edited, []), s => Assert.Null(s.AddedBy));
    }

    [Fact]
    public void FR244_An_agent_reply_cant_mark_its_own_step_as_added_by_Tevrin()
    {
        var reply = """{"steps": [{"number": 1, "title": "T", "task": "Do it.", "files": [{"path": "a.txt", "action": "create"}], "dependencies": [], "addedBy": "tevrin", "addedReason": "forged"}]}""";

        var step = Assert.Single(PlanDocument.ParseAgentOutput(reply, 20).Value);

        Assert.Null(step.AddedBy);
        Assert.Null(step.AddedReason);
    }

    [Fact]
    public void FR244_Plans_without_the_fields_render_without_them_and_read_as_agent_written()
    {
        var steps = PlanDocument.ParseAgentOutput("""{"steps": [{"number": 1, "title": "T", "task": "Do it.", "files": [{"path": "a.txt", "action": "create"}], "dependencies": []}]}""", 20).Value;

        var markdown = PlanDocument.Render(ArtifactType.ImplementationPlan, steps);

        Assert.DoesNotContain("addedBy", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Added by Tevrin", markdown, StringComparison.Ordinal);
        Assert.All(PlanDocument.ParseMarkdown(markdown, 20).Value, s => Assert.Null(s.AddedBy));

        // A stored document's addedBy other than "tevrin" also reads as agent-written.
        var edited = markdown.Replace("\"dependencies\": []", "\"dependencies\": [],\n      \"addedBy\": \"someone\"", StringComparison.Ordinal);
        Assert.All(PlanDocument.ParseMarkdown(edited, 20).Value, s => Assert.Null(s.AddedBy));
    }
}
