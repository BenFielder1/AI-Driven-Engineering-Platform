using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Pack.Coach;

/// <summary>
/// FR-340: what <see cref="VelocityLoadRulesGuard"/> reads besides the stage output. A <c>validate:</c> guard receives
/// only the <c>plan_draft</c> being checked (<see cref="StageOutputFact"/>), so the host supplies the run's pinned
/// <c>athlete_profile</c> (FR-352) and the recent <c>session_log</c> versions from its artifact store.
/// </summary>
public interface IVelocityInputsSource
{
    /// <summary>The run's athlete profile and session logs, or <c>null</c> when the run has no athlete profile.</summary>
    Task<VelocityInputs?> ReadAsync(GuardContext context, CancellationToken cancellationToken);
}

/// <summary>The run's <c>athlete_profile</c> JSON and its <c>session_log</c> versions' JSON (the last 4 weeks of
/// actuals; any order, duplicate sessions count once).</summary>
public sealed record VelocityInputs(string AthleteProfile, IReadOnlyList<string> SessionLogs);

/// <summary>FR-340: <c>coach.velocity-load-rules</c>, the engine adapter of <see cref="VelocityRules"/>. It fails with one
/// reason per finding, which the engine feeds back to the programme planner's next attempt.</summary>
public sealed class VelocityLoadRulesGuard(IVelocityInputsSource inputs) : IGuard
{
    public async Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Facts.Get<StageOutputFact>() is not { } output)
        {
            return GuardResult.Fail("coach.velocity-load-rules: there is no plan_draft to check.");
        }

        var read = await inputs.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            return GuardResult.Fail("coach.velocity-load-rules: the run has no athlete_profile.");
        }

        IReadOnlyList<string> findings;
        try
        {
            findings = VelocityRules.Check(output.Content, read.AthleteProfile, read.SessionLogs);
        }
        catch (FormatException e)
        {
            return GuardResult.Fail("coach.velocity-load-rules: " + e.Message);
        }

        return findings.Count == 0 ? GuardResult.Pass : GuardResult.Fail([.. findings]);
    }
}
