using Aide.Domain.Entities;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence.Conversion;

namespace Aide.UnitTests.Review;

/// <summary>FR-38: the compliance report jsonb shapes round-trip, and rows written before the evidence/source details
/// existed stay readable with defaults.</summary>
public sealed class ComplianceReportJsonTests
{
    [Fact]
    public void FR38_category_score_round_trips_with_agent_score_penalty_and_evidence()
    {
        IReadOnlyList<CategoryScoreEntry> scores =
            [new CategoryScoreEntry(ReviewCategory.Security, 0.7m, 0.9m, 0.2m, [new ScoreEvidence("src/a.cs", "hard-coded key"), new ScoreEvidence(null, "no auth on admin")])];

        var json = AideJson.Serialize(scores);
        var back = AideJson.Deserialize<IReadOnlyList<CategoryScoreEntry>>(json);

        Assert.Contains("\"category\":\"security\"", json, StringComparison.Ordinal);
        Assert.Contains("\"evidence\":[{\"file\":\"src/a.cs\"", json, StringComparison.Ordinal);
        var entry = Assert.Single(back);
        Assert.Equal((0.7m, 0.9m, 0.2m), (entry.Score, entry.AgentScore, entry.Penalty));
        Assert.Equal(scores[0].Evidence, entry.Evidence);
    }

    [Fact]
    public void FR38_legacy_category_score_reads_with_defaults_and_string_evidence_as_one_reason()
    {
        var legacy = """[{"category":"api_contracts","score":0.8,"evidence":"src/api.cs: missing route"},{"category":"testing","score":1}]""";

        var back = AideJson.Deserialize<IReadOnlyList<CategoryScoreEntry>>(legacy);

        Assert.Equal((0m, 0m), (back[0].AgentScore, back[0].Penalty));
        Assert.Equal([new ScoreEvidence(null, "src/api.cs: missing route")], back[0].Evidence);
        Assert.Empty(back[1].Evidence);
    }

    [Fact]
    public void FR38_finding_source_is_stored_as_a_string_and_legacy_findings_read_as_deterministic()
    {
        IReadOnlyList<ReviewFinding> findings = [new ReviewFinding(ReviewCategory.DataModel, Severity.Major, "No FK", "db.sql", FindingSource.Agent, "orders table")];

        var json = AideJson.Serialize(findings);
        var legacy = AideJson.Deserialize<IReadOnlyList<ReviewFinding>>("""[{"category":"security","severity":"critical","message":"Secret","path":"x.cs"}]""");

        Assert.Contains("\"source\":\"agent\"", json, StringComparison.Ordinal);
        Assert.Equal(findings, AideJson.Deserialize<IReadOnlyList<ReviewFinding>>(json));
        var old = Assert.Single(legacy);
        Assert.Equal(FindingSource.Deterministic, old.Source);
        Assert.Null(old.Evidence);
    }
}
