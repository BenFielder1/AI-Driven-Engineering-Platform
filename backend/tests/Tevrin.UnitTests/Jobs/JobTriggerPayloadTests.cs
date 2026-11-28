using Tevrin.Api.Jobs;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Infrastructure.Persistence.Conversion;

namespace Tevrin.UnitTests.Jobs;

/// <summary>
/// FR-273: agent job payloads write the generic triggers; a 0.6.1 payload that says <c>phase_entered</c> reads as
/// <c>stage_entered</c>, every other trigger is stored and read byte for byte; the API still shows the 0.6.1 values (FR-274).
/// </summary>
public class JobTriggerPayloadTests
{
    private static readonly Guid ProjectId = Guid.Parse("0199f2a0-7777-7000-8000-000000000001");

    [Fact]
    public void FR273_a_new_stage_entry_payload_writes_stage_entered()
    {
        var json = TevrinJobPayloadSerializer.Instance.Serialize(new LifecycleJobPayload(ProjectId, 3, LifecycleJobPayload.StageEntered));

        Assert.Contains("\"trigger\":\"stage_entered\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("phase_entered", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FR273_a_0_6_1_phase_entered_payload_reads_as_stage_entered()
    {
        var legacy = Json(LifecycleJobPayload.GateRejected).Replace("\"gate_rejected\"", "\"phase_entered\"", StringComparison.Ordinal);

        var payload = TevrinJson.Deserialize<LifecycleJobPayload>(legacy);

        Assert.Equal((LifecycleJobPayload.StageEntered, ProjectId, 3), (payload.Trigger, payload.ProjectId, payload.ProjectVersion));
        Assert.Equal(legacy.Replace("phase_entered", "stage_entered", StringComparison.Ordinal), TevrinJobPayloadSerializer.Instance.Serialize(payload));
    }

    [Fact]
    public void FR273_phase_entered_given_in_code_or_with_reads_as_stage_entered()
    {
        var payload = new LifecycleJobPayload(ProjectId, 1, JobTriggers.LegacyPhaseEntered);

        Assert.Equal(JobTriggers.StageEntered, payload.Trigger);
        Assert.Equal(JobTriggers.StageEntered, (payload with { Trigger = JobTriggers.UserRequested } with { Trigger = JobTriggers.LegacyPhaseEntered }).Trigger);
    }

    [Theory]
    [InlineData(JobTriggers.UserRequested)]
    [InlineData(JobTriggers.StageEntered)]
    [InlineData(JobTriggers.GateRejected)]
    [InlineData(JobTriggers.ArtifactRejected)]
    [InlineData(JobTriggers.ArtifactCorrupted)]
    [InlineData(JobTriggers.ValidationFailed)]
    [InlineData("retried")] // the release retry's own trigger (FR-43)
    public void FR273_every_other_trigger_round_trips_byte_identical(string trigger)
    {
        var json = Json(trigger);

        Assert.Equal(json, TevrinJobPayloadSerializer.Instance.Serialize(TevrinJson.Deserialize<LifecycleJobPayload>(json)));
    }

    [Theory]
    [InlineData(JobTriggers.StageEntered, "phase_entered")]
    [InlineData(JobTriggers.LegacyPhaseEntered, "phase_entered")]
    [InlineData(JobTriggers.UserRequested, "user_requested")]
    [InlineData(JobTriggers.GateRejected, "gate_rejected")]
    [InlineData(JobTriggers.ArtifactRejected, "artifact_rejected")]
    [InlineData(JobTriggers.ArtifactCorrupted, "artifact_corrupted")]
    [InlineData(JobTriggers.ValidationFailed, null)] // not in the 0.6.1 AgentJobTrigger enum
    [InlineData(null, null)]
    public void FR273_FR274_the_api_shows_the_0_6_1_trigger_values(string? stored, string? wire) =>
        Assert.Equal(wire, AgentJobTriggerWire.ToWireOrNull(stored));

    [Theory]
    [InlineData(JobTriggers.StageEntered, "phase_entered")]
    [InlineData(JobTriggers.LegacyPhaseEntered, "phase_entered")]
    [InlineData(JobTriggers.UserRequested, "user_requested")]
    [InlineData(JobTriggers.ArtifactRejected, "artifact_rejected")]
    [InlineData("retried", "retried")] // the release retry (RELEASE_STARTED)
    public void FR273_FR274_event_payloads_keep_the_0_6_1_trigger_values(string trigger, string recorded) =>
        // REVIEW_STARTED and RELEASE_STARTED record the trigger through ToWire: only job payloads say stage_entered.
        Assert.Equal(recorded, AgentJobTriggerWire.ToWire(trigger));

    [Fact]
    public void FR274_the_api_trigger_values_are_the_0_6_1_enum_in_order() =>
        Assert.Equal(["user_requested", "phase_entered", "gate_rejected", "artifact_rejected", "artifact_corrupted"], AgentJobTriggerWire.All);

    private static string Json(string trigger) =>
        TevrinJobPayloadSerializer.Instance.Serialize(new LifecycleJobPayload(ProjectId, 3, trigger, "feedback", Guid.Parse("0199f2a0-7777-7000-8000-000000000002")));
}
