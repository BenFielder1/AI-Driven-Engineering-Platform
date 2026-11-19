using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Tests.Jobs;

/// <summary>FR-273: generic trigger strings replace <c>AgentJobTrigger</c>; <c>phase_entered</c> reads as <c>stage_entered</c>.</summary>
public class JobTriggersTests
{
    [Fact]
    public void FR273_the_generic_triggers_keep_the_0_6_1_values_except_stage_entered()
    {
        Assert.Equal(
            ["user_requested", "stage_entered", "gate_rejected", "artifact_rejected", "artifact_corrupted", "validation_failed"],
            JobTriggers.All);
        Assert.Equal("phase_entered", JobTriggers.LegacyPhaseEntered);
    }

    [Fact]
    public void FR273_phase_entered_reads_as_stage_entered() =>
        Assert.Equal(JobTriggers.StageEntered, JobTriggers.Read("phase_entered"));

    [Theory]
    [InlineData("user_requested")]
    [InlineData("stage_entered")]
    [InlineData("gate_rejected")]
    [InlineData("artifact_rejected")]
    [InlineData("artifact_corrupted")]
    [InlineData("validation_failed")]
    [InlineData("retried")] // a pack's own trigger (the SDLC release retry)
    [InlineData("Phase_Entered")] // ordinal: only the exact 0.6.1 value is an alias
    [InlineData("")]
    public void FR273_every_other_trigger_reads_back_unchanged(string trigger) =>
        Assert.Equal(trigger, JobTriggers.Read(trigger));

    [Fact]
    public void FR273_a_missing_trigger_stays_missing() => Assert.Null(JobTriggers.Read(null));
}
