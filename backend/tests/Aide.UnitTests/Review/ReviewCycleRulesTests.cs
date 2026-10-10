using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;

namespace Aide.UnitTests.Review;

/// <summary>FR-38 (review reply, inputs), FR-39 (decision from a whole review), §4.7 (penalties, overall, drift).</summary>
public class ReviewCycleRulesTests
{
    private static readonly string[] Categories = ["feature_coverage", "api_contracts", "data_model", "architecture", "security", "testing"];

    private static string Reply(
        decimal score = 0.9m,
        object? plan = null,
        IEnumerable<string>? categories = null,
        object[]? violations = null,
        object[]? drift = null) => JsonSerializer.Serialize(new
        {
            categoryScores = (categories ?? Categories).Select(c => new { category = c, score, evidence = new[] { new { file = "src/a.js", reason = "looked" } } }),
            violations = violations ?? [],
            missingFeatures = new object[] { new { id = "FR-7", title = "Export", reason = "no endpoint" } },
            driftAreas = drift ?? [],
            optimizationPlan = plan,
            summary = "ok",
        });

    private static object Plan(params (string Path, int[] Deps)[] steps) => new
    {
        steps = steps.Select((s, i) => new
        {
            number = i + 1,
            title = $"Fix {i + 1}",
            task = "Fix it.",
            files = new[] { new { path = s.Path, action = "modify" } },
            dependencies = s.Deps,
        }),
    };

    private static CodeReviewResult Parsed(string reply) => CodeReviewOutput.Parse(reply, 20).Value;

    private static ReviewAssessmentResult Assess(decimal score, int cycle, decimal? previous = null, int extra = 0, IReadOnlyList<ReviewFinding>? findings = null, object[]? drift = null) =>
        ReviewAssessment.Assess(new ReviewAssessmentInput(Parsed(Reply(score, drift: drift)), findings ?? [], 0.90m, cycle, 3, extra, previous));

    // ---------- FR-38: reading the reply ----------

    [Fact]
    public void FR38_A_valid_reply_reads_every_category_with_evidence_violations_missing_features_and_the_plan()
    {
        var result = CodeReviewOutput.Parse(
            Reply(0.8m, Plan(("src/a.js", []), ("src/b.js", [1])), violations: [new { category = "security", severity = "major", message = "No input validation", path = "./src/a.js" }]),
            20);

        Assert.True(result.IsSuccess);
        var review = result.Value;
        Assert.Equal(Enum.GetValues<ReviewCategory>(), review.Categories.Select(c => c.Category));
        Assert.All(review.Categories, c => Assert.Equal(new ScoreEvidence("src/a.js", "looked"), Assert.Single(c.Evidence)));
        var violation = Assert.Single(review.Violations);
        Assert.Equal((ReviewCategory.Security, Severity.Major, FindingSource.Agent, "src/a.js"), (violation.Category, violation.Severity, violation.Source, violation.Path));
        Assert.Equal(["FR-7 Export: no endpoint"], review.MissingFeatures);
        Assert.Equal(2, review.OptimizationPlan.Count);
        Assert.Contains("```aide-plan", review.OptimizationPlanMarkdown, StringComparison.Ordinal);
        Assert.StartsWith("# Optimization plan", review.OptimizationPlanMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void FR38_A_missing_category_or_out_of_range_score_is_INVALID_OUTPUT_with_every_reason()
    {
        var missing = CodeReviewOutput.Parse(Reply(categories: Categories.Take(5)), 20);
        var outOfRange = CodeReviewOutput.Parse(Reply(1.2m), 20);
        var notJson = CodeReviewOutput.Parse("I think the code is fine.", 20);

        Assert.Equal(ErrorCodes.InvalidOutput, missing.Error?.Code);
        Assert.Contains(missing.Error!.Details, d => d.Contains("testing", StringComparison.Ordinal));
        Assert.Equal(ErrorCodes.InvalidOutput, outOfRange.Error?.Code);
        Assert.Equal(6, outOfRange.Error!.Details.Count(d => d.Contains("from 0 to 1", StringComparison.Ordinal)));
        Assert.Equal(ErrorCodes.InvalidOutput, notJson.Error?.Code);
        Assert.Contains("INVALID_OUTPUT", CodeReviewOutput.CorrectiveFeedback(missing.Error), StringComparison.Ordinal);
    }

    [Fact]
    public void FR38_An_optimisation_plan_with_a_forward_dependency_or_a_protected_path_is_INVALID_PLAN()
    {
        var forward = CodeReviewOutput.Parse(Reply(plan: Plan(("src/a.js", [2]), ("src/b.js", []))), 20);
        var protectedPath = CodeReviewOutput.Parse(Reply(plan: Plan((".aide/rules.md", []))), 20);

        Assert.Equal(ErrorCodes.InvalidPlan, forward.Error?.Code);
        Assert.Equal(ErrorCodes.InvalidPlan, protectedPath.Error?.Code);
    }

    [Fact]
    public void FR38_An_empty_or_missing_optimisation_plan_is_valid_and_drafts_nothing()
    {
        var empty = Parsed(Reply(plan: new { steps = Array.Empty<object>() }));
        var none = Parsed(Reply());

        Assert.Empty(empty.OptimizationPlan);
        Assert.Null(empty.OptimizationPlanMarkdown);
        Assert.Empty(none.OptimizationPlan);
        Assert.False(ReviewAssessment.DraftsOptimizationPlan(ReviewDecision.ReviewRequired, 0));
        Assert.True(ReviewAssessment.DraftsOptimizationPlan(ReviewDecision.ReviewRequired, 1));
        Assert.True(ReviewAssessment.DraftsOptimizationPlan(ReviewDecision.EscalationRequired, 1));
        Assert.False(ReviewAssessment.DraftsOptimizationPlan(ReviewDecision.ReleaseEligible, 3));
    }

    // ---------- §4.7: penalties, overall, drift ----------

    [Fact]
    public void S4_7_Deterministic_findings_apply_fixed_penalties_to_their_category_and_the_overall_is_weighted()
    {
        ReviewFinding[] findings =
        [
            new(ReviewCategory.Security, Severity.Critical, "Possible aws_access_key at line 1.", "src/config.js"),
            new(ReviewCategory.ApiContracts, Severity.Major, "Endpoint '/api/x' from the specification was not found in the code."),
            new(ReviewCategory.Architecture, Severity.Major, "Dependency 'lodash' is not in the specification's architecture."),
        ];

        var result = Assess(0.9m, 1, findings: findings);

        var byCategory = result.Categories.ToDictionary(c => c.Category);
        Assert.Equal((0.9m, 0.20m, 0.70m), (byCategory[ReviewCategory.Security].AgentScore, byCategory[ReviewCategory.Security].Penalty, byCategory[ReviewCategory.Security].Score));
        Assert.Equal((0.10m, 0.80m), (byCategory[ReviewCategory.ApiContracts].Penalty, byCategory[ReviewCategory.ApiContracts].Score));
        Assert.Equal((0.10m, 0.80m), (byCategory[ReviewCategory.Architecture].Penalty, byCategory[ReviewCategory.Architecture].Score));
        Assert.Equal((0m, 0.9m), (byCategory[ReviewCategory.Testing].Penalty, byCategory[ReviewCategory.Testing].Score));

        // 0.9×0.30 + 0.8×0.20 + 0.9×0.15 + 0.8×0.15 + 0.7×0.10 + 0.9×0.10
        Assert.Equal(0.845m, result.Overall);
        Assert.Equal(3, result.Violations.Count);
        Assert.All(result.Violations, v => Assert.Equal(FindingSource.Deterministic, v.Source));
    }

    [Fact]
    public void S4_7_A_category_score_is_floored_at_zero()
    {
        ReviewFinding[] findings =
        [
            new(ReviewCategory.Security, Severity.Critical, "secret 1"),
            new(ReviewCategory.Security, Severity.Critical, "secret 2"),
            new(ReviewCategory.Security, Severity.Critical, "secret 3"),
            new(ReviewCategory.Security, Severity.Critical, "secret 4"),
            new(ReviewCategory.Security, Severity.Critical, "secret 5"),
        ];

        var security = Assess(0.9m, 1, findings: findings).Categories.Single(c => c.Category == ReviewCategory.Security);

        Assert.Equal((1.00m, 0m), (security.Penalty, security.Score));
    }

    [Fact]
    public void S4_7_The_agents_own_violations_are_reported_but_carry_no_penalty()
    {
        var review = Parsed(Reply(0.9m, violations: [new { category = "testing", severity = "critical", message = "No tests" }]));

        var result = ReviewAssessment.Assess(new ReviewAssessmentInput(review, [], 0.90m, 1, 3, 0, null));

        Assert.Equal(0.9m, result.Overall);
        Assert.Equal(FindingSource.Agent, Assert.Single(result.Violations).Source);
    }

    [Theory]
    [InlineData(new string[0], DriftLevel.None)]
    [InlineData(new[] { "minor", "major" }, DriftLevel.Low)]
    [InlineData(new[] { "critical", "major" }, DriftLevel.Moderate)]
    [InlineData(new[] { "critical", "critical", "critical" }, DriftLevel.Severe)]
    public void S4_7_Drift_level_comes_from_the_agents_drift_areas(string[] severities, DriftLevel expected) =>
        Assert.Equal(expected, Assess(0.95m, 1, drift: [.. severities.Select(s => (object)new { area = "x", severity = s, description = "d" })]).Drift);

    // ---------- FR-39 / §4.5 worked examples through a whole review ----------

    [Fact]
    public void FR39_Example_093_cycle1_is_release_eligible() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.ReleaseEligible, null), Assess(0.93m, 1).Decision);

    [Fact]
    public void FR39_Example_082_cycle1_is_review_required() =>
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.ReviewRequired, null), Assess(0.82m, 1).Decision);

    [Fact]
    public void FR39_Example_082_cycle3_escalates_max_cycles_unless_continue_granted_an_extra_cycle()
    {
        Assert.Equal(EscalationReason.MaxCycles, Assess(0.82m, 3).Decision.Reason);
        Assert.Equal(ReviewDecision.ReviewRequired, Assess(0.82m, 3, previous: 0.80m, extra: 1).Decision.Decision);
    }

    [Fact]
    public void FR39_Example_084_then_083_escalates_no_improvement() =>
        Assert.Equal(EscalationReason.NoImprovement, Assess(0.83m, 2, previous: 0.84m).Decision.Reason);

    [Fact]
    public void FR39_Example_095_with_severe_drift_escalates() =>
        Assert.Equal(
            EscalationReason.SevereDrift,
            Assess(0.95m, 1, drift: [new { area = "a", severity = "critical", description = "d" }, new { area = "b", severity = "critical", description = "d" }, new { area = "c", severity = "critical", description = "d" }]).Decision.Reason);

    [Fact]
    public void FR39_Example_070_escalates_low_score() =>
        Assert.Equal(EscalationReason.LowScore, Assess(0.70m, 1).Decision.Reason);

    // ---------- FR-38 / §4.7: stack-agnostic inputs ----------

    private const string Spec = """
        ## 3. Technical Architecture
        Node.js service.

        ### Dependencies
        - `express@5` — web framework
        - **jest**
        - requests==2.31

        ## 4. Data Models
        - not-a-dependency

        ## 5. API Contracts
        | Method | Path |
        | GET | /api/todos |
        | GET | /api/todos/{id} |
        See https://example.com/docs/api for more. Health: `/health`.

        ## 6. Feature Specifications
        Mentions /api/elsewhere outside the section.
        """;

    [Fact]
    public void S4_7_Endpoint_paths_come_from_the_API_Contracts_section_only_and_skip_URLs() =>
        Assert.Equal(["/api/todos", "/api/todos/{id}", "/health"], SpecReading.EndpointPaths(Spec));

    [Fact]
    public void S4_7_The_dependency_allowlist_comes_from_the_architecture_sections_Dependencies_list()
    {
        Assert.Equal(["express", "jest", "requests"], SpecReading.AllowedDependencies(Spec));
        Assert.Null(SpecReading.AllowedDependencies("## Technical Architecture\nA monolith.\n## API Contracts\n- GET /x"));
    }

    [Fact]
    public void S4_7_Used_dependencies_are_read_from_common_manifests_whatever_the_stack()
    {
        SourceFile[] files =
        [
            new("package.json", """{"dependencies":{"express":"^5"},"devDependencies":{"jest":"^30"}}"""),
            new("node_modules/x/package.json", """{"dependencies":{"ignored":"1"}}"""),
            new("api/requirements.txt", "# comment\nflask==3.0\nrequests[socks]>=2\n-r other.txt\n"),
            new("src/App/App.csproj", """<Project><ItemGroup><PackageReference Include="Serilog" Version="4" /></ItemGroup></Project>"""),
            new("go.mod", "module x\n\nrequire (\n\tgithub.com/gin-gonic/gin v1.9.0\n)\nrequire golang.org/x/text v0.3.0\n"),
            new("Cargo.toml", "[package]\nname = \"x\"\n[dependencies]\nserde = \"1\"\n"),
            new("Gemfile", "source 'https://rubygems.org'\ngem 'rails'\n"),
            new("package-broken/package.json", "{ not json"),
        ];

        Assert.Equal(
            ["express", "jest", "flask", "requests", "Serilog", "github.com/gin-gonic/gin", "golang.org/x/text", "serde", "rails"],
            DependencyManifests.Used(files));
    }

    [Fact]
    public void FR38_Review_reads_changed_files_first_within_size_count_and_total_limits()
    {
        RepositoryFile[] tree = [new("a.js", 10), new("b.js", 10), new("big.bin", 5000), new("c.js", 10), new("d.js", 10)];
        var limits = new ReviewLimits(MaxFiles: 3, MaxFileBytes: 1000, MaxTotalBytes: 25, MaxPromptChars: 10_000, MaxListedPaths: 10);

        Assert.Equal(["d.js", "a.js"], ReviewCode.SelectForReading(tree, ["d.js"], limits));
        Assert.Equal(["a.js", "b.js", "c.js"], ReviewCode.SelectForReading(tree, [], limits with { MaxTotalBytes = 1000 }));
    }

    [Fact]
    public void FR38_The_prompt_code_lists_the_tree_marks_changes_and_fences_contents_safely()
    {
        var text = ReviewCode.RenderForPrompt(
            [new RepositoryFile("README.md", 20), new RepositoryFile("src/a.js", 5)],
            new Dictionary<string, string> { ["src/a.js"] = "added" },
            [new SourceFile("README.md", "has ``` inside"), new SourceFile("src/a.js", "x")],
            ReviewLimits.Default);

        Assert.Contains("- src/a.js (added)", text, StringComparison.Ordinal);
        Assert.Contains("````\nhas ``` inside\n````", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("### src/a.js", StringComparison.Ordinal) < text.IndexOf("### README.md", StringComparison.Ordinal));
    }

    // ---------- Fake responder (D13) ----------

    [Fact]
    public void FR38_The_Fake_review_follows_its_script_by_cycle_and_is_valid()
    {
        var responder = new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.8m, 0.95m], OptimizationStepsByCycle = [3, 0] });
        static LlmRequest Request(int cycle) => new()
        {
            Model = "fake-1",
            System = "s",
            Messages = [new LlmMessage(LlmRole.User, $"<review_cycle>\n{cycle}\n</review_cycle>\n<code>\n- src/a.js\n</code>")],
            MaxTokens = 100,
            Agent = AgentKind.CodeReview,
        };

        var first = Parsed(responder.Respond(Request(1)));
        var later = Parsed(responder.Respond(Request(5)));

        Assert.All(first.Categories, c => Assert.Equal(0.8m, c.Score));
        Assert.Equal(3, first.OptimizationPlan.Count);
        Assert.Equal([2], first.OptimizationPlan[2].Dependencies);
        Assert.All(later.Categories, c => Assert.Equal(0.95m, c.Score));
        Assert.Empty(later.OptimizationPlan);
        Assert.Equal(responder.Respond(Request(1)), responder.Respond(Request(1)));
    }

    // ---------- FR-38/FR-39: review_required needs an optimisation plan ----------

    [Fact]
    public void FR38_A_review_required_reply_without_optimisation_steps_needs_a_corrective_re_run()
    {
        Assert.True(ReviewAssessment.LacksOptimizationPlan(ReviewDecision.ReviewRequired, 0));
        Assert.False(ReviewAssessment.LacksOptimizationPlan(ReviewDecision.ReviewRequired, 1));
        Assert.False(ReviewAssessment.LacksOptimizationPlan(ReviewDecision.EscalationRequired, 0));
        Assert.False(ReviewAssessment.LacksOptimizationPlan(ReviewDecision.ReleaseEligible, 0));
        var error = ReviewAssessment.MissingOptimizationPlanError();
        Assert.Equal(ErrorCodes.InvalidOutput, error.Code);
        Assert.Contains("optimizationPlan", Assert.Single(error.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void FR39_A_review_required_decision_still_without_optimisation_steps_escalates_no_optimization_plan()
    {
        var assessment = Assess(0.82m, 1);

        var withoutPlan = ReviewAssessment.RequireOptimizationPlan(assessment, 0);
        var withPlan = ReviewAssessment.RequireOptimizationPlan(assessment, 2);

        Assert.Equal(new ReviewDecisionResult(ReviewDecision.EscalationRequired, EscalationReason.NoOptimizationPlan), withoutPlan.Decision);
        Assert.Equal(assessment.Overall, withoutPlan.Overall);
        Assert.Equal(new ReviewDecisionResult(ReviewDecision.ReviewRequired, null), withPlan.Decision);
        Assert.Equal(EscalationReason.LowScore, ReviewAssessment.RequireOptimizationPlan(Assess(0.70m, 1), 0).Decision.Reason);
        Assert.Equal(ReviewDecision.ReleaseEligible, ReviewAssessment.RequireOptimizationPlan(Assess(0.93m, 1), 0).Decision.Decision);
    }

    // ---------- NFR-SEC: secrets never reach the review prompt ----------

    [Fact]
    public void FR38_Secrets_the_scan_flags_are_redacted_line_for_line_before_the_code_is_rendered_for_the_prompt()
    {
        var awsKey = "AKIA" + "Z7Q3EXAMPLE4K2LM";
        var content = string.Join('\n', ["const a = 1;", $"const key = '{awsKey}'; // aws", "-----BEGIN RSA PRIVATE KEY-----", "MIIEsecretbody", "-----END RSA PRIVATE KEY-----", "const b = 2;"]);
        SourceFile[] files = [new("src/config.js", content)];

        var redacted = ReviewCode.RedactSecrets(files);
        var lines = redacted[0].Content.Split('\n');

        Assert.Equal(6, lines.Length);
        Assert.Equal("const a = 1;", lines[0]);
        Assert.Equal("const key = '[REDACTED:aws_access_key_id]'; // aws", lines[1]);
        Assert.Equal(["[REDACTED:private_key]", "[REDACTED:private_key]", "[REDACTED:private_key]"], lines[2..5]);
        Assert.Equal("const b = 2;", lines[5]);
        Assert.Empty(Aide.Domain.Execution.SecretScanner.Scan(redacted[0].Content));
        var prompt = ReviewCode.RenderForPrompt([new RepositoryFile("src/config.js", content.Length)], new Dictionary<string, string>(), redacted, ReviewLimits.Default);
        Assert.DoesNotContain(awsKey, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEsecretbody", prompt, StringComparison.Ordinal);
    }
}
