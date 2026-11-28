using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Api.Jobs;

/// <summary>
/// FR-273, FR-274, D-0.7.0-24: an agent job's trigger as the API shows it: the 0.6.1 values of the <c>AgentJobTrigger</c>
/// component of <c>openapi/api.json</c>, so the contract, the generated client and the frontend's copy are unchanged.
/// Payloads store the generic <see cref="JobTriggers"/>; <see cref="JobTriggers.StageEntered"/> is shown as
/// <c>phase_entered</c>, every other value as stored.
/// </summary>
public static class AgentJobTriggerWire
{
    /// <summary>The 0.6.1 wire value of <see cref="JobTriggers.StageEntered"/>.</summary>
    public const string PhaseEntered = JobTriggers.LegacyPhaseEntered;

    /// <summary>The documented values, in the order of the 0.6.1 enum.</summary>
    public static IReadOnlyList<string> All { get; } =
        [JobTriggers.UserRequested, PhaseEntered, JobTriggers.GateRejected, JobTriggers.ArtifactRejected, JobTriggers.ArtifactCorrupted];

    /// <summary>The wire value of a stored trigger: <c>stage_entered</c> (or <c>phase_entered</c>) is <c>phase_entered</c>;
    /// anything else is unchanged.</summary>
    public static string ToWire(string trigger) =>
        JobTriggers.Read(trigger) == JobTriggers.StageEntered ? PhaseEntered : trigger;

    /// <summary>As <see cref="ToWire"/>, but <c>null</c> for a missing trigger or one outside <see cref="All"/> (as the
    /// 0.6.1 enum read it).</summary>
    public static string? ToWireOrNull(string? trigger) =>
        trigger is not null && ToWire(trigger) is var wire && All.Contains(wire, StringComparer.Ordinal) ? wire : null;
}
