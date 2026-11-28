using System.Text.Json;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;
using static Tevrin.Engine.Tests.Pipelines.DefinitionYaml;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// FR-350 (M69): validator agents, <c>validate: [{ agent: &lt;key&gt; }]</c>: the definition syntax on
/// <c>fixture-factcheck</c>, and the pure verdict rules (the schema, error vs warning, the attempt's reasons and the
/// stored findings).
/// </summary>
public class ValidatorAgentRulesTests
{
    private const string Checker = "fact-checker";

    private static string Yaml => FlowFixturePack.Yaml(FlowFixturePack.FactCheck);

    private static List<(string Code, string Path)> FactCheckErrors(string find, string replace) =>
        Errors(Edit(find, replace, Yaml), FlowFixturePack.Catalog);

    // ---- definition ----

    [Fact]
    public void FR350_validate_takes_an_agent_entry_beside_guards()
    {
        var write = PipelineDefinitionLoader.Parse(Yaml).Definition!.FindStage("write")!;

        var validation = Assert.Single(write.Validate);
        Assert.Equal((null, Checker), (validation.Guard, validation.Agent));
        Assert.Empty(Errors(Yaml, FlowFixturePack.Catalog));
    }

    [Fact]
    public void FR350_a_validator_agent_must_be_bound_under_agents()
    {
        var errors = FactCheckErrors("- { agent: fact-checker }", "- { agent: copy-editor }");

        Assert.Contains((DefinitionErrorCodes.FanOutAgent, "stages[0].validate[0].agent"), errors);
    }

    [Fact]
    public void FR350_an_entry_with_both_guard_and_agent_is_refused()
    {
        var errors = FactCheckErrors("- { agent: fact-checker }", "- { agent: fact-checker, guard: flow.position-limit }");

        Assert.Contains((DefinitionErrorCodes.Parse, "stages[0].validate[0]"), errors);
    }

    [Fact]
    public void FR350_a_bindings_own_validate_takes_guards_only()
    {
        var errors = FactCheckErrors(
            "prompt: flow.fact-check }",
            "prompt: flow.fact-check, validate: [{ agent: fact-checker }] }");

        Assert.Contains(errors, e => e.Code == DefinitionErrorCodes.Parse && e.Path.EndsWith(".validate", StringComparison.Ordinal));
    }

    [Fact]
    public void FR350_the_agent_is_hashed_and_a_guard_entry_hashes_without_it()
    {
        var definition = PipelineDefinitionLoader.Parse(Yaml).Definition!;
        var json = DefinitionHash.CanonicalJson(definition);
        Assert.Contains("\"agent\":\"fact-checker\"", json, StringComparison.Ordinal);

        var review = DefinitionHash.CanonicalJson(Fixtures.Load("fixture-review.pipeline.yaml"));
        Assert.DoesNotContain("\"validate\":[{\"guard\":\"fixture.report-consistent\",\"agent\"", review, StringComparison.Ordinal);
        Assert.Equal(AgentBindingHashTests.FixtureReviewHash, DefinitionHash.Compute(Fixtures.Load("fixture-review.pipeline.yaml")));

        var reread = PipelineDefinitionLoader.Parse(json);
        Assert.Empty(reread.Errors);
        Assert.Equal(DefinitionHash.Compute(definition), DefinitionHash.Compute(reread.Definition!));
    }

    // ---- verdict ----

    [Fact]
    public void FR350_a_pass_with_no_findings_reads_and_does_not_reject()
    {
        var verdict = ValidatorAgentRules.Read(Checker, """{ "verdict": "pass", "findings": [] }""");

        Assert.NotNull(verdict);
        Assert.False(verdict.Rejects);
        Assert.Empty(ValidatorAgentRules.Reasons(verdict, Checker));
    }

    [Fact]
    public void FR350_error_findings_reject_with_one_reason_each()
    {
        var verdict = ValidatorAgentRules.Read(Checker, """
            ```json
            { "verdict": "reject", "findings": [
              { "path": "/para/2", "claim": "Water boils at 90 C", "reason": "It boils at 100 C at sea level.", "severity": "error" },
              { "claim": "Kettles are old", "reason": "Unsourced.", "severity": "warning" },
              { "reason": "The date is wrong.", "severity": "error" }
            ] }
            ```
            """)!;

        Assert.True(verdict.Rejects);
        Assert.Equal(3, verdict.Findings.Count);
        Assert.Equal(
            ["fact-checker: /para/2: It boils at 100 C at sea level.", "fact-checker: The date is wrong."],
            ValidatorAgentRules.Reasons(verdict, Checker));
    }

    [Fact]
    public void FR350_warning_findings_alone_do_not_reject()
    {
        var verdict = ValidatorAgentRules.Read(Checker, """
            { "verdict": "pass", "findings": [{ "claim": "Kettles are old", "reason": "Unsourced.", "severity": "warning" }] }
            """)!;

        Assert.False(verdict.Rejects);
        Assert.Empty(ValidatorAgentRules.Reasons(verdict, Checker));
        Assert.Equal(ValidatorSeverity.Warning, Assert.Single(verdict.Findings).Severity);
    }

    [Fact]
    public void FR350_a_reject_verdict_without_error_findings_still_rejects_with_one_reason()
    {
        var verdict = ValidatorAgentRules.Read(Checker, """{ "verdict": "reject", "findings": [] }""")!;

        Assert.True(verdict.Rejects);
        Assert.Equal(["fact-checker: rejected the output."], ValidatorAgentRules.Reasons(verdict, Checker));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Looks fine to me.")]
    [InlineData("{ not json }")]
    [InlineData("""{ "findings": [] }""")]
    [InlineData("""{ "verdict": "maybe", "findings": [] }""")]
    [InlineData("""{ "verdict": "pass" }""")]
    [InlineData("""{ "verdict": "pass", "findings": {} }""")]
    [InlineData("""{ "verdict": "pass", "findings": [], "score": 1 }""")]
    [InlineData("""{ "verdict": "reject", "findings": [{ "severity": "error" }] }""")]
    [InlineData("""{ "verdict": "reject", "findings": [{ "reason": " ", "severity": "error" }] }""")]
    [InlineData("""{ "verdict": "reject", "findings": [{ "reason": "Wrong.", "severity": "fatal" }] }""")]
    [InlineData("""{ "verdict": "reject", "findings": [{ "reason": "Wrong.", "severity": "error", "path": 3 }] }""")]
    [InlineData("""{ "verdict": "reject", "findings": [{ "reason": "Wrong.", "severity": "error", "line": 3 }] }""")]
    [InlineData("""{ "verdict": "reject", "findings": ["Wrong."] }""")]
    public void FR350_an_answer_breaking_the_verdict_schema_reads_as_null(string answer) =>
        Assert.Null(ValidatorAgentRules.Read(Checker, answer));

    [Fact]
    public void FR350_the_stored_findings_keep_errors_and_warnings_with_their_agent()
    {
        var verdict = ValidatorAgentRules.Read(Checker, """
            { "verdict": "reject", "findings": [
              { "path": "/title", "reason": "Wrong year.", "severity": "error" },
              { "claim": "Fast", "reason": "Vague.", "severity": "warning" }
            ] }
            """)!;

        using var doc = JsonDocument.Parse(ValidatorAgentRules.StoredFindings(verdict.Findings));
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(("fact-checker", "/title", "Wrong year.", "error"), (
            items[0].GetProperty("agent").GetString(), items[0].GetProperty("path").GetString(),
            items[0].GetProperty("reason").GetString(), items[0].GetProperty("severity").GetString()));
        Assert.False(items[0].TryGetProperty("claim", out _));
        Assert.Equal(("Fast", "warning"), (items[1].GetProperty("claim").GetString(), items[1].GetProperty("severity").GetString()));
    }

    [Fact]
    public void FR350_the_validator_run_key_and_error_code()
    {
        Assert.Equal("validate-fact-checker", ValidatorAgentRules.RunKey(Checker));
        Assert.Equal("VALIDATOR_INVALID_OUTPUT", EngineErrorCodes.ValidatorInvalidOutput);
        Assert.NotEqual(AgentRunRules.RunId(Guid.Empty, ValidatorAgentRules.RunKey(Checker)), AgentRunRules.RunId(Guid.Empty, "veto-" + Checker));
    }
}
