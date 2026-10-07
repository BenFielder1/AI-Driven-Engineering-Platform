namespace Aide.Domain.Review;

/// <summary>A deterministic-check finding that penalises a category (§4.7).</summary>
public sealed record ReviewFinding(ReviewCategory Category, Severity Severity, string Message, string? Path = null);

/// <summary>Category scores after penalties, and the weighted overall score.</summary>
public sealed record ComplianceScore(IReadOnlyDictionary<ReviewCategory, decimal> CategoryScores, decimal Overall);

/// <summary>§4.7 scoring: weights, fixed penalties (floored at 0), overall rounded to 3 dp.</summary>
public static class ComplianceScoring
{
    public static IReadOnlyDictionary<ReviewCategory, decimal> Weights { get; } = new Dictionary<ReviewCategory, decimal>
    {
        [ReviewCategory.FeatureCoverage] = 0.30m,
        [ReviewCategory.ApiContracts] = 0.20m,
        [ReviewCategory.DataModel] = 0.15m,
        [ReviewCategory.Architecture] = 0.15m,
        [ReviewCategory.Security] = 0.10m,
        [ReviewCategory.Testing] = 0.10m,
    };

    public static decimal Penalty(Severity severity) => severity switch
    {
        Severity.Critical => 0.20m,
        Severity.Major => 0.10m,
        Severity.Minor => 0.03m,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    /// <summary>
    /// Applies deterministic penalties to the agent's category scores (each clamped to 0–1) and computes the
    /// overall score. The agent must score every category.
    /// </summary>
    public static ComplianceScore Score(IReadOnlyDictionary<ReviewCategory, decimal> agentScores, IEnumerable<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(agentScores);
        ArgumentNullException.ThrowIfNull(findings);

        var missing = Weights.Keys.Where(c => !agentScores.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Missing category scores: {string.Join(", ", missing)}.", nameof(agentScores));
        }

        var penalties = findings
            .GroupBy(f => f.Category)
            .ToDictionary(g => g.Key, g => g.Sum(f => Penalty(f.Severity)));

        var categories = Weights.Keys.ToDictionary(
            c => c,
            c => Math.Max(0m, Math.Clamp(agentScores[c], 0m, 1m) - penalties.GetValueOrDefault(c)));

        var overall = Math.Round(categories.Sum(kv => kv.Value * Weights[kv.Key]), 3, MidpointRounding.AwayFromZero);
        return new ComplianceScore(categories, overall);
    }
}

/// <summary>§4.7 drift: areas weighted critical 3, major 2, minor 1; 0 none, 1–3 low, 4–8 moderate, ≥ 9 severe.</summary>
public static class DriftRules
{
    public static int Weight(Severity severity) => severity switch
    {
        Severity.Critical => 3,
        Severity.Major => 2,
        Severity.Minor => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    public static DriftLevel Level(IEnumerable<Severity> driftAreas)
    {
        ArgumentNullException.ThrowIfNull(driftAreas);
        return driftAreas.Sum(Weight) switch
        {
            0 => DriftLevel.None,
            <= 3 => DriftLevel.Low,
            <= 8 => DriftLevel.Moderate,
            _ => DriftLevel.Severe,
        };
    }
}
