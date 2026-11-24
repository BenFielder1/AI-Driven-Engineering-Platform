using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Pack.Coach.Tests;

/// <summary>FR-340: the <c>coach.velocity-load-rules</c> engine adapter: the plan arrives as the stage output, the profile and
/// session logs from the host's <see cref="IVelocityInputsSource"/>.</summary>
public class VelocityLoadRulesGuardTests
{
    private const string Profile = """{ "name": "Sam", "baselineWeeklyKm": 50 }""";

    private const string Log = """{ "sessions": [ { "date": "2026-11-22", "type": "long run", "distanceKm": 50 } ] }""";

    private const string PassingPlan = """
        { "weeks": [ { "week": 1, "sessions": [
          { "date": "2026-11-23", "type": "easy run", "intensity": "easy", "distanceKm": 25 },
          { "date": "2026-11-25", "type": "long run", "intensity": "easy", "distanceKm": 25 } ] } ] }
        """;

    private const string FailingPlan = """
        { "weeks": [ { "week": 1, "sessions": [
          { "date": "2026-11-23", "type": "intervals", "intensity": "hard", "distanceKm": 30 },
          { "date": "2026-11-24", "type": "tempo", "intensity": "hard", "distanceKm": 31 } ] } ] }
        """;

    private sealed class Source(VelocityInputs? inputs) : IVelocityInputsSource
    {
        public GuardContext? Seen { get; private set; }

        public Task<VelocityInputs?> ReadAsync(GuardContext context, CancellationToken cancellationToken)
        {
            Seen = context;
            return Task.FromResult(inputs);
        }
    }

    private static GuardContext Context(string? plan) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "plan", ApprovalMode.Strict,
            plan is null ? FactBag.Empty : FactBag.Empty.With(new StageOutputFact(CoachArtifacts.PlanDraft, ArtifactFormat.Json, plan)));

    [Fact]
    public async Task FR340_guard_passes_a_plan_within_the_rules()
    {
        var source = new Source(new VelocityInputs(Profile, [Log]));
        var context = Context(PassingPlan);

        var result = await new VelocityLoadRulesGuard(source).EvaluateAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Same(context, source.Seen);
    }

    [Fact]
    public async Task FR340_guard_fails_with_one_reason_per_finding()
    {
        var result = await new VelocityLoadRulesGuard(new Source(new VelocityInputs(Profile, [Log])))
            .EvaluateAsync(Context(FailingPlan), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(
            [
                "R1: week 1 volume 61.0 km is +22% over the last actual week's 50.0 km (max +10%)",
                "R2: hard sessions on 2026-11-23 and 2026-11-24 are 24 h apart (min 48 h)",
            ],
            result.Reasons);
    }

    [Fact]
    public async Task FR340_guard_fails_without_a_plan_a_profile_or_with_malformed_json()
    {
        var ct = TestContext.Current.CancellationToken;
        var withInputs = new VelocityLoadRulesGuard(new Source(new VelocityInputs(Profile, [])));

        Assert.False((await withInputs.EvaluateAsync(Context(null), ct)).Ok);
        Assert.False((await new VelocityLoadRulesGuard(new Source(null)).EvaluateAsync(Context(PassingPlan), ct)).Ok);
        var malformed = await withInputs.EvaluateAsync(Context("not json"), ct);
        Assert.False(malformed.Ok);
        Assert.StartsWith("coach.velocity-load-rules: plan_draft is not valid JSON", Assert.Single(malformed.Reasons), StringComparison.Ordinal);
    }
}
