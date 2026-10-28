using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Review;
using Aide.Domain.Specs;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-120–FR-128 wiring outside the resolver: plan validation in composition mode, the Guardian's pack protected paths,
/// the stack guidance blocks, the review dedupe, the analyst's stack checks and the Fake provider's composed outputs.
/// </summary>
public sealed class CompositionWiringTests
{
    private static StackResolution Resolve(StackSelection selection, StackChange? change = null) => StackResolverTests.Resolve(selection, change);

    private static StackResolution Aspnet(StackChange? change = null) => Resolve(StackResolverTests.AspnetSpa(), change);

    private static PlanStep Step(int number, string agent, params (string Path, FileAction Action)[] files) =>
        new(number, $"Step {number}", "Do it.", [.. files.Select(f => new DeclaredFile(f.Path, f.Action))], number == 1 ? [] : [number - 1], Agent: agent);

    private static Result<IReadOnlyList<PlanStep>> Generated(PlanAgents agents, params PlanStep[] steps) =>
        PlanValidator.Validate(steps, maxFilesPerStep: 20, allowProtectedPaths: false, agents);

    private static LlmRequest Rendered(AgentKind agent, Dictionary<string, string?> values)
    {
        var rendered = new EmbeddedPromptLibrary().Template(agent).Render(values).Value;
        return new LlmRequest { Model = "fake-1", System = rendered.System, Messages = [new LlmMessage(LlmRole.User, rendered.User)], Agent = agent };
    }

    public static TheoryData<string, string, string, string?, string?> Compositions => new()
    {
        { "fe-react-vite", "be-aspnet", "data-efcore-postgres", null, null },
        { "fe-nextjs", "none", "data-supabase", "deploy-vercel", null },
        { "fe-vue", "be-fastapi", "data-sqlalchemy", null, null },
        { "none", "be-spring-boot", "data-jpa-flyway", null, null },
        { "fe-react-vite", "be-express", "data-prisma", "deploy-netlify", "deploy-render" },
        { "fe-angular", "be-nestjs", "data-drizzle", null, null },
    };

    private static StackResolution Of(string fe, string be, string data, string? feHost, string? beHost) =>
        Resolve(Sel(fe, be, data, "vcs-github", frontendHost: feHost, backendHost: beHost));

    // ---------- FR-124: plan ----------

    [Theory]
    [MemberData(nameof(Compositions))]
    public void FR124_The_Fake_plan_for_a_composition_is_valid_with_its_specialists_and_composed_rules(string fe, string be, string data, string? feHost, string? beHost)
    {
        var stack = Of(fe, be, data, feHost, beHost);
        var reply = new FakePlanGeneratorResponder().Respond(Rendered(AgentKind.PlanGenerator, new()
        {
            ["PROJECT_REQUEST"] = "# Demo: Recipe Finder\n\n## Summary\nRecipes.",
            ["TECHNICAL_SPECIFICATION"] = "## 2. Functional Requirements\n- **FR-1** x. Given a, when b, then c.",
            ["MAX_FILES_PER_STEP"] = "20",
            ["STACK_GUIDANCE"] = StackGuidance.Plan(stack, repoEmpty: true),
        }));

        var agents = PlanAgents.ForStack(stack, repoEmpty: true, new HashSet<string>(StringComparer.Ordinal));
        var plan = PlanDocument.ParseAgentOutput(reply, 20, agents);

        Assert.True(plan.IsSuccess, plan.Error is null ? null : string.Join(" | ", plan.Error.Details));
        Assert.Equal(StackAgents.Scaffolder, plan.Value[0].Agent);
        Assert.All(plan.Value, s => Assert.Contains(s.Agent!, stack.AgentKeys));
        Assert.DoesNotContain(CompositionPlanValidator.Warnings(plan.Value, stack), w => w.Code is ErrorCodes.PlanOrder or ErrorCodes.UndeclaredGeneratedFile);
        Assert.True(PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, plan.Value, 20, agents).IsSuccess);
    }

    [Fact]
    public void FR124_The_Fake_iteration_plan_of_a_stack_change_has_a_step_for_the_changed_layer()
    {
        var stack = Aspnet(new StackChange(true, [StackLayers.Frontend, StackLayers.Deploy]));
        var reply = new FakePlanGeneratorResponder().Respond(Rendered(AgentKind.PlanGenerator, new()
        {
            ["PROJECT_REQUEST"] = "# Demo: Recipe Finder",
            ["TECHNICAL_SPECIFICATION"] = "## 2. Functional Requirements\n- **FR-5** [new] x. Given a, when b, then c.",
            ["MAX_FILES_PER_STEP"] = "20",
            ["STACK_GUIDANCE"] = StackGuidance.Plan(stack, repoEmpty: false),
        }));

        var plan = PlanDocument.ParseAgentOutput(reply, 20, PlanAgents.ForStack(stack, repoEmpty: false, new HashSet<string>(StringComparer.Ordinal)));

        Assert.True(plan.IsSuccess, plan.Error is null ? null : string.Join(" | ", plan.Error.Details));
        Assert.DoesNotContain(plan.Value, s => s.Agent == StackAgents.Scaffolder);
        Assert.Contains(plan.Value, s => s.Agent == StackAgents.FrontendEngineer);
        Assert.Contains(plan.Value, s => s.Agent == StackAgents.DeployEngineer);
    }

    [Fact]
    public void FR124_A_backend_engineer_step_creating_a_migration_is_INVALID_PLAN_naming_the_exclusivity_rule()
    {
        var plan = Generated(
            PlanAgents.ForStack(Aspnet(), repoEmpty: false, new HashSet<string>(StringComparer.Ordinal)),
            Step(1, StackAgents.BackendEngineer, ("backend/src/App.Infrastructure/Migrations/20260101000000_Init.cs", FileAction.Create)));

        Assert.Equal(ErrorCodes.InvalidPlan, plan.Error?.Code);
        Assert.Contains(plan.Error!.Details, d => d.Contains("Migrations belong to data-engineer", StringComparison.Ordinal));
    }

    [Fact]
    public void FR124_An_unknown_agent_or_the_generalist_is_INVALID_PLAN_in_a_composed_implementation_plan()
    {
        var agents = PlanAgents.ForStack(Aspnet());

        var unknown = Generated(agents, Step(1, "ml-engineer", ("README.md", FileAction.Create)));
        var generalist = Generated(agents, Step(1, PlanAgents.Generalist, ("README.md", FileAction.Create)));
        var missing = Generated(agents, new PlanStep(1, "Docs", "Do it.", [new DeclaredFile("README.md", FileAction.Create)], []));

        Assert.Contains(unknown.Error!.Details, d => d.Contains("'ml-engineer'", StringComparison.Ordinal) && d.Contains("this stack's specialists", StringComparison.Ordinal));
        Assert.Equal(ErrorCodes.InvalidPlan, generalist.Error?.Code);
        Assert.Contains(missing.Error!.Details, d => d.Contains("has no \"agent\"", StringComparison.Ordinal));
    }

    [Fact]
    public void FR124_A_stack_change_without_a_step_for_the_new_layer_is_STACK_CHANGE_UNPLANNED()
    {
        var stack = Aspnet(new StackChange(true, [StackLayers.Backend]));

        var plan = Generated(PlanAgents.ForStack(stack, repoEmpty: false, new HashSet<string>(StringComparer.Ordinal)), Step(1, StackAgents.DocsWriter, ("docs/x.md", FileAction.Create)));

        Assert.Equal(ErrorCodes.StackChangeUnplanned, plan.Error?.Code);
        Assert.Equal("Your previous plan was rejected with STACK_CHANGE_UNPLANNED for these reasons:", PlanDocument.CorrectiveFeedback(plan.Error!).Split('\n')[0]);
    }

    [Fact]
    public void FR124_Composed_optimisation_plans_allow_the_generalist_and_skip_the_composed_rules()
    {
        var agents = PlanAgents.ForStackOptimization(Aspnet(new StackChange(true, [StackLayers.Backend])));

        var plan = Generated(agents, new PlanStep(1, "Fix", "Do it.", [new DeclaredFile("docs/review/cycle-1-step-1.md", FileAction.Create)], []));

        Assert.True(plan.IsSuccess, plan.Error?.Message);
        Assert.Equal(PlanAgents.Generalist, plan.Value[0].Agent);
    }

    [Fact]
    public void FR124_Plan_guidance_lists_the_specialists_rules_and_contract_file()
    {
        var guidance = StackGuidance.Plan(Aspnet(), repoEmpty: true);

        Assert.StartsWith(StackGuidance.Heading, guidance, StringComparison.Ordinal);
        Assert.Contains("- `backend-engineer` (Backend engineer):", guidance, StringComparison.Ordinal);
        Assert.Contains(StackGuidance.RepoEmptyText, guidance, StringComparison.Ordinal);
        Assert.Contains("Migrations belong to data-engineer.", guidance, StringComparison.Ordinal);
        Assert.Contains("- `backend/openapi/api.json`", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void FR125_A_generated_plan_may_not_modify_a_migration_on_the_base_commit_but_may_add_one()
    {
        var stack = Resolve(Sel("none", "be-spring-boot", "data-jpa-flyway", "vcs-github"));
        var existing = "backend/src/main/resources/db/migration/V2__orders.sql";
        var agents = PlanAgents.ForStack(stack, repoEmpty: false, new HashSet<string>([existing], StringComparer.Ordinal));

        var modify = Generated(agents, Step(1, StackAgents.DataEngineer, (existing, FileAction.Modify)));
        var add = Generated(agents, Step(1, StackAgents.DataEngineer, ("backend/src/main/resources/db/migration/V3__lines.sql", FileAction.Create)));

        Assert.Contains(modify.Error!.Details, d => d.Contains("already exists on the base commit", StringComparison.Ordinal));
        Assert.True(add.IsSuccess, add.Error?.Message);
    }

    [Fact]
    public void FR125_A_ci_engineer_may_declare_the_GitLab_CI_file_and_others_may_not()
    {
        var stack = Resolve(Sel("fe-vue", "none", "none", "vcs-gitlab"));

        Assert.True(Generated(PlanAgents.ForStack(stack), Step(1, StackAgents.CiEngineer, (".gitlab-ci.yml", FileAction.Create))).IsSuccess);
        Assert.Equal(ErrorCodes.InvalidPlan, Generated(PlanAgents.ForStack(stack), Step(1, StackAgents.FrontendEngineer, (".gitlab-ci.yml", FileAction.Create))).Error?.Code);
    }

    // ---------- FR-125: Guardian ----------

    private static GuardianInput Guard(StackResolution stack, string declared, string produced, Func<string, bool> existsOnBase) =>
        new(1, 1, [new StepState(1, StepStatus.InProgress, [], [declared])], [new ProducedFile(produced, "-- sql\n")], 20, existsOnBase, stack);

    [Fact]
    public void FR125_The_Guardian_rejects_a_step_modifying_a_Flyway_migration_that_exists_on_the_base_commit()
    {
        var stack = Resolve(Sel("none", "be-spring-boot", "data-jpa-flyway", "vcs-github"));
        const string path = "backend/src/main/resources/db/migration/V2__orders.sql";

        var result = ExecutionGuardian.Validate(Guard(stack, path, path, p => p == path));

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Message.Contains("immutable", StringComparison.Ordinal));
        Assert.True(ExecutionGuardian.Validate(Guard(stack, path, path, _ => false)).Accepted);
    }

    [Fact]
    public void FR125_The_Guardian_rejects_an_undeclared_EF_Core_migration_path_but_accepts_the_model_snapshot_exception()
    {
        var stack = Aspnet();
        const string declared = "backend/src/App.Api/Program.cs";

        var undeclared = ExecutionGuardian.Validate(new GuardianInput(1, 1, [new StepState(1, StepStatus.InProgress, [], [declared])],
            [new ProducedFile(declared, "x"), new ProducedFile("backend/src/App.Infrastructure/Migrations/1_X.cs", "x")], 20, p => p.EndsWith("1_X.cs", StringComparison.Ordinal), stack));

        Assert.Contains(undeclared.Errors, v => v.Code == ErrorCodes.Guardian.ProtectedPath && v.Path == "backend/src/App.Infrastructure/Migrations/1_X.cs");
    }

    // ---------- FR-123: spec ----------

    [Fact]
    public void FR123_Spec_guidance_has_the_required_sections_and_the_Fake_spec_passes_validation_with_them()
    {
        var stack = Resolve(StackResolverTests.AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));
        var titles = StackGuidance.SpecSections(stack).Select(s => s.Title).ToList();
        var guidance = StackGuidance.Spec(stack);

        Assert.All(titles, t => Assert.Contains($"#### {t}", guidance, StringComparison.Ordinal));
        Assert.Contains("API Contract & Error Codes", titles);
        var reply = new FakeSpecGeneratorResponder().Respond(Rendered(AgentKind.SpecGenerator, new()
        {
            ["PROJECT_REQUEST"] = "# Demo: Recipe Finder\n\n## Summary\nRecipes.",
            ["STACK_GUIDANCE"] = guidance,
        }));
        Assert.True(SpecValidator.Check(reply, SpecValidationContext.ForVersion(false, null, titles)).IsSuccess);
    }

    [Fact]
    public void FR123_FR128_A_mismatch_adds_Stack_Migration_once()
    {
        var stack = Aspnet();
        var mismatch = new StackMismatch(StackLayers.Frontend, "fe-vue", "fe-react-vite", []);

        var titles = StackGuidance.SpecSections(stack, [mismatch]).Select(s => s.Title).ToList();
        var changed = StackGuidance.SpecSections(Aspnet(new StackChange(true, [StackLayers.Frontend])), [mismatch]).Select(s => s.Title).ToList();

        Assert.Contains(StackCoreSpecSections.StackMigrationTitle, titles);
        Assert.Single(changed, t => t == StackCoreSpecSections.StackMigrationTitle);
        Assert.DoesNotContain(StackCoreSpecSections.StackMigrationTitle, StackGuidance.SpecSections(stack).Select(s => s.Title));
    }

    // ---------- FR-126: review ----------

    [Fact]
    public void FR126_Review_guidance_numbers_the_composed_checklist_and_checklist_violations_take_the_item_category()
    {
        var stack = Aspnet();
        var guidance = StackGuidance.Review(stack);
        var first = stack.Checklist[0];

        Assert.Contains($"- [C1] ({first.Category.ToWire()}, {first.Severity.ToWire()}) `{first.Pack}`:", guidance, StringComparison.Ordinal);
        var mapped = StackReviewChecklist.Apply([new ReviewFinding(ReviewCategory.Testing, Severity.Minor, "[C1] fails", null, FindingSource.Agent)], stack);
        Assert.Equal((first.Category, first.Severity, FindingSource.Agent), (mapped[0].Category, mapped[0].Severity, mapped[0].Source));
    }

    [Fact]
    public void FR126_A_committed_secret_found_by_the_v1_scan_is_not_penalised_twice()
    {
        var stack = Aspnet();
        SourceFile[] files = [new("backend/appsettings.json", "{\n  \"x\": 1,\n  \"key\": \"sk_live_abcdefghijklmnopqrstuvwx\"\n}\n")];
        var v1 = DeterministicChecks.SecretScan(files);
        var raw = StackReviewChecks.CommittedSecrets(files);

        var merged = StackReviewFindings.WithoutDuplicateSecrets(raw, v1);

        Assert.NotEmpty(v1);
        Assert.NotEmpty(raw);
        Assert.Empty(merged);
        Assert.Equal(raw.Count, StackReviewFindings.WithoutDuplicateSecrets(raw, []).Count);
        Assert.NotNull(stack);
    }

    [Fact]
    public void FR126_The_Fake_composed_backend_and_deploy_files_pass_the_deterministic_checks()
    {
        var stack = Aspnet();
        string[] paths = ["backend/src/App.Api/Program.cs", "backend/src/App.Api/Features/Recipes/RecipesEndpoints.cs", "backend/Dockerfile", "backend/openapi/api.json", "compose.yaml"];
        SourceFile[] files = [.. paths.Select(p => new SourceFile(p, FakeStackScripts.Content("backend-engineer", p, "Demo", 2, "API")!))];

        Assert.Empty(StackReviewChecks.Run(stack, new StackReviewInput(files)));
        Assert.Contains("/api/recipes/:id", files[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FR126_A_health_endpoint_missing_from_the_backend_is_a_minor_feature_coverage_finding()
    {
        var finding = Assert.Single(StackReviewChecks.Run(Aspnet(), new StackReviewInput([new SourceFile("backend/src/App.Api/Program.cs", "var app = builder.Build();\n")])));

        Assert.Equal((ReviewCategory.FeatureCoverage, Severity.Minor), (finding.Category, finding.Severity));
    }

    // ---------- FR-128: analyst ----------

    private static StackDetection Detected(string option, string path = "frontend") =>
        new([new StackDetectedChoice(StackChoices.Frontend, option, path, null, ["`vue` in `frontend/package.json`"])], []);

    [Fact]
    public void FR128_An_import_detected_as_fe_vue_with_fe_react_vite_chosen_is_a_frontend_mismatch()
    {
        var report = StackAnalysis.Report(Detected("fe-vue"), Aspnet().Composition);

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal((StackLayers.Frontend, "fe-vue", "fe-react-vite"), (mismatch.Layer, mismatch.Detected, mismatch.Chosen));
        Assert.Empty(StackAnalysis.Report(Detected("fe-react-vite"), Aspnet().Composition).Mismatches);
    }

    [Fact]
    public void FR128_A_summary_whose_Stack_section_doesnt_name_the_detected_pack_and_mismatch_fails_then_the_Fake_passes()
    {
        var stack = Aspnet();
        var report = StackAnalysis.Report(Detected("fe-vue"), stack.Composition);
        var expectation = StackAnalysis.Expectation(report, stack.Composition);
        var plain = string.Join("\n\n", CodebaseSummaryRules.RequiredSections.Select(s => $"## {s}\nText."));

        var failed = StackAnalysis.Check(plain, expectation);

        Assert.Equal(ErrorCodes.MissingSections, failed.Error?.Code);
        Assert.Contains(failed.Error!.Details, d => d.Contains("`fe-vue`", StringComparison.Ordinal));
        Assert.Contains("\"Stack & Dependencies\" section must", StackAnalysis.CorrectiveFeedback(failed.Error), StringComparison.Ordinal);

        var reply = new FakeRepositoryAnalystResponder().Respond(Rendered(AgentKind.RepositoryAnalyst, new()
        {
            ["PROJECT_NAME"] = "Demo",
            ["STACK_GUIDANCE"] = StackGuidance.Analysis(stack, report),
            ["STACK_DETECTION"] = StackGuidance.DetectionInput(report),
        }));
        Assert.True(StackAnalysis.Check(reply, expectation).IsSuccess, StackAnalysis.Check(reply, expectation).Error?.Message);
    }

    [Fact]
    public void FR128_The_detection_input_lists_suggestions_with_evidence_and_mismatches()
    {
        var report = StackAnalysis.Report(Detected("fe-vue"), Aspnet().Composition);

        var input = StackGuidance.DetectionInput(report)!;

        Assert.Contains("- frontend: `fe-vue` at `frontend`; evidence: `vue` in `frontend/package.json`", input, StringComparison.Ordinal);
        Assert.Contains("- mismatch on frontend: detected `fe-vue`, the composition has `fe-react-vite`", input, StringComparison.Ordinal);
        Assert.Null(StackGuidance.DetectionInput(null));
    }

    // ---------- FR-121: Fake specialist ----------

    [Fact]
    public void FR121_The_Fake_code_generator_reads_the_composed_role_and_writes_a_non_root_Dockerfile()
    {
        var stack = Aspnet();
        var template = new EmbeddedPromptLibrary().Template(AgentKind.CodeGenerator);
        var values = template.RequiredInputs.ToDictionary(n => n, n => (string?)n, StringComparer.Ordinal);
        values["STEP_NUMBER"] = "7";
        values["DECLARED_FILES"] = "- backend/Dockerfile (create)\n- package.json (create)";
        var rendered = StackPrompt.Compose(template.Render(values).Value, stack, StackAgents.DeployEngineer).Value;

        var reply = new FakeCodeGeneratorResponder().Respond(new LlmRequest { Model = "fake-1", System = rendered.System, Messages = [new LlmMessage(LlmRole.User, rendered.User)], Agent = AgentKind.CodeGenerator });

        var files = CodeGeneration.Parse(reply).Value.Files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        Assert.Contains("USER $APP_UID", files["backend/Dockerfile"], StringComparison.Ordinal);
        using var package = JsonDocument.Parse(files["package.json"]);
        Assert.False(package.RootElement.TryGetProperty("dependencies", out _)); // the generic package.json, not the preset's Next.js one
    }
}
