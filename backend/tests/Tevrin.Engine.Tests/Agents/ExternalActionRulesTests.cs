using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>FR-360 (M71d, D-0.8.0-31): the pure rules of the gate before an external action.</summary>
public class ExternalActionRulesTests
{
    [Fact]
    public void FR360_external_action_is_engine_raised_and_decided_mid_stage_like_tool_approval()
    {
        Assert.Equal("external_action", EngineGates.ExternalAction);
        Assert.True(EngineGates.IsEngineRaised(EngineGates.ExternalAction));
        Assert.True(EngineGates.IsMidStage(EngineGates.ExternalAction));
        Assert.True(EngineGates.IsMidStage(EngineGates.ToolApproval));
        Assert.False(EngineGates.IsMidStage(EngineGates.Escalation));
        Assert.False(EngineGates.IsMidStage(EngineGates.VariantSelect));
    }

    [Theory]
    [InlineData(ToolTier.Read, false, null)]
    [InlineData(ToolTier.Write, false, null)]
    [InlineData(ToolTier.Destructive, false, EngineGates.ToolApproval)]
    [InlineData(ToolTier.Read, true, EngineGates.ExternalAction)]
    [InlineData(ToolTier.Write, true, EngineGates.ExternalAction)]
    [InlineData(ToolTier.Destructive, true, EngineGates.ExternalAction)]
    public void FR360_an_external_tool_waits_on_one_external_action_gate_whatever_its_tier(ToolTier tier, bool external, string? gate) =>
        Assert.Equal(gate, ExternalActionRules.ApprovalGateOf(tier, external));

    [Theory]
    [InlineData("""{"to":"jobs@example.test","cv":"Ada Lovelace, +44 20 7946 0000"}""")]
    [InlineData("Work authorization: US citizen")]
    [InlineData("Authorization = granted by the board")]
    [InlineData("I was the bearer of this letter of recommendation.")]
    [InlineData("Bearer bonds, 2019-2021")]
    [InlineData("Skills: sk-learn, scikit, risk-taking; api_key design for public APIs")]
    [InlineData("Access token rotation and OAuth flows (access_token: short-lived)")]
    public void FR360_ordinary_cv_and_application_text_goes_on_the_gate_as_is(string payload) =>
        Assert.Null(ExternalActionRules.PayloadProblem(payload));

    // Fake credential-shaped vectors, assembled at run time so secret scanners don't read the source as real secrets.
    public static TheoryData<string> CredentialShapedPayloads() => new()
    {
        "{\"key\":\"" + "sk-ant-" + "api03-AbCdEfGhIjKlMnOpQrStUvWx" + "\"}",
        "{\"key\":\"" + "sk-proj-" + "0123456789abcdefghijKLMN" + "\"}",
        "token " + "ghp_" + "0123456789abcdefghijABCDEFGHIJ012345",
        "github" + "_pat_" + "11ABCDEFG0123456789_abcdefghijklmnop",
        "glpat" + "-" + "AbCdEfGhIjKlMnOpQrSt",
        "xoxb" + "-" + "1234567890-abcdefghij",
        "AKIA" + "IOSFODNN7EXAMPLE",
        "AIza" + "SyA0123456789abcdefghijklmnopqrstuv",
        string.Join('.', "eyJhbGciOiJIUzI1NiJ9", "eyJzdWIiOiJmYWtlIn0", "c2lnbmF0dXJlLW5vdC1yZWFsLWZvci10ZXN0cw"),
        string.Join('\n', "-----BEGIN RSA " + "PRIVATE KEY-----", "MIIEow==", "-----END RSA " + "PRIVATE KEY-----"),
        "-----BEGIN OPENSSH " + "PRIVATE KEY-----",
        "{\"headers\":{\"Authorization\":\"" + "Bearer " + "0123456789abcdefghijKLMNOP" + "\"}}",
    };

    [Theory]
    [MemberData(nameof(CredentialShapedPayloads))]
    public void FR360_a_payload_carrying_a_credential_is_refused_and_the_reason_never_quotes_it(string payload)
    {
        var problem = ExternalActionRules.PayloadProblem(payload);

        Assert.NotNull(problem);
        Assert.Contains("credential", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FR360_a_payload_naming_the_runs_own_secret_is_refused_but_short_secrets_never_match_words()
    {
        Assert.NotNull(ExternalActionRules.PayloadProblem("""{"token":"hunter2-secret"}""", "hunter2-secret"));
        Assert.Null(ExternalActionRules.PayloadProblem("a senior engineer", "senior"));
    }

    [Theory]
    [InlineData("a", 0x0000, "b")]
    [InlineData("lone ", 0xD800, " high")]
    [InlineData("lone ", 0xDC00, " low")]
    [InlineData("end ", 0xD800, "")]
    public void FR360_a_payload_jsonb_cannot_store_is_refused(string before, int code, string after) =>
        Assert.Contains("NUL", ExternalActionRules.PayloadProblem(before + (char)code + after), StringComparison.Ordinal);

    [Fact]
    public void FR360_a_paired_surrogate_is_fine() =>
        Assert.Null(ExternalActionRules.PayloadProblem("Ship it \ud83d\ude80"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FR360_an_empty_payload_is_refused(string? payload) =>
        Assert.NotNull(ExternalActionRules.PayloadProblem(payload));

    [Fact]
    public void FR360_a_payload_over_1_MB_is_refused()
    {
        Assert.Null(ExternalActionRules.PayloadProblem(new string('a', ExternalActionRules.MaxPayloadBytes)));
        Assert.Contains("1 MB", ExternalActionRules.PayloadProblem(new string('a', ExternalActionRules.MaxPayloadBytes + 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void FR360_the_gate_payload_round_trips_and_records_the_claim()
    {
        var execution = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        var json = ExternalActionRules.GatePayload("fixture-cv.submit-application", "submit", execution, job, """{"to":"x"}""");

        Assert.Equal(
            new ExternalActionGatePayload("fixture-cv.submit-application", "submit", execution, job, """{"to":"x"}""", null),
            ExternalActionRules.ReadGatePayload(json));

        var ranAt = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
        var claimed = ExternalActionRules.ReadGatePayload(ToolLoopStates.WithRanAt(json, ranAt))!;
        Assert.Equal((ranAt, (DateTimeOffset?)null), (claimed.RanAt, claimed.SentAt));
        Assert.True(ToolLoopStates.HasRanAt(ToolLoopStates.WithRanAt(json, ranAt)));

        var sentAt = ranAt.AddSeconds(2);
        Assert.Equal(sentAt, ExternalActionRules.ReadGatePayload(ExternalActionRules.WithSentAt(ToolLoopStates.WithRanAt(json, ranAt), sentAt))!.SentAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"tool":"tiers.send","arguments":"{}","agentRunId":"0195c8a4-63b1-7000-8000-000000000001"}""")]
    [InlineData("""{"handler":"a.b","stage":"s","payload":"x","stageExecutionId":"00000000-0000-0000-0000-000000000000","jobId":"0195c8a4-63b1-7000-8000-000000000001"}""")]
    public void FR360_a_payload_that_is_not_an_external_handlers_reads_as_none(string? json) =>
        Assert.Null(ExternalActionRules.ReadGatePayload(json));

    [Fact]
    public void FR360_the_rejection_names_the_reviewer_or_the_timer()
    {
        var owner = Guid.CreateVersion7();

        Assert.Equal($"The external action of submit was not sent: denied by {owner}: wrong job.", ExternalActionRules.RejectedMessage("submit", owner, "wrong job"));
        Assert.Equal(
            "The external action of submit was not sent: denied by system: no decision within 24 hours.",
            ExternalActionRules.RejectedMessage("submit", null, ToolApprovalRules.TimeoutComment));
    }

    [Theory]
    [InlineData(GateStatus.Pending, 23, ToolApprovalStep.Wait)]
    [InlineData(GateStatus.Pending, 24, ToolApprovalStep.TimeOut)]
    [InlineData(GateStatus.Approved, 1, ToolApprovalStep.Run)]
    [InlineData(GateStatus.Rejected, 1, ToolApprovalStep.Deny)]
    [InlineData(GateStatus.Cancelled, 1, ToolApprovalStep.Closed)]
    public void FR360_a_raised_gate_follows_the_FR359_steps_and_24_hour_timer(GateStatus status, int hours, ToolApprovalStep step)
    {
        var raised = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(step, ToolApprovalRules.Next(ToolApprovalPhase.PendingApproval, gateRaised: true, status, raised, raised.AddHours(hours)));
        Assert.Equal(ToolApprovalStep.AlreadyClaimed, ToolApprovalRules.Next(ToolApprovalPhase.Approved, gateRaised: true, status, raised, raised.AddHours(hours)));
    }
}
