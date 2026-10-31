using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Domain.Review;

namespace Aide.Domain.Deployments;

/// <summary>A preview component's outcome as review sees it.</summary>
public sealed record RolloutComponentOutcome(
    DeploymentComponent Component, DeploymentProvider Platform, DeploymentStatus Status, string? ErrorCode, string? ErrorMessage, string? BuildLogTail, string? Url);

/// <summary>The preview migration run as review sees it (status, error, advisor findings).</summary>
public sealed record RolloutMigrationOutcome(MigrationRunStatus Status, string? ErrorCode, string? ErrorMessage, IReadOnlyList<AdvisorFinding> Advisors);

/// <summary>
/// FR-147, FR-153: the deterministic review findings of a preview rollout, pure.
/// <list type="bullet">
/// <item>A failed build is a critical <c>architecture</c> finding, "&lt;Host&gt; preview build failed" (v2's "Vercel preview build
/// failed"), with the log tail as evidence.</item>
/// <item>A failed pre-deploy (the platform-run migration, FR-154) is a critical <c>architecture</c> finding, "Backend preview
/// pre-deploy migration failed", with the pre-deploy log.</item>
/// <item>A failed Supabase BaaS preview migration is a critical <c>data_model</c> finding with the runner's error.</item>
/// <item>Supabase security advisors: <c>ERROR</c> → major <c>security</c>, <c>WARN</c> → minor <c>security</c>; <c>INFO</c>
/// is not a finding.</item>
/// </list>
/// </summary>
public static class RolloutFindings
{
    public const string PreDeployFailedMessage = "Backend preview pre-deploy migration failed";

    public const string MigrationFailedMessage = "Preview database migration failed";

    public const string DivergedMessage = "Preview database has migrations this branch no longer has";

    public static string BuildFailedMessage(DeploymentProvider platform) => $"{DeploymentPlatforms.DisplayName(platform)} preview build failed";

    public static IReadOnlyList<ReviewFinding> For(IReadOnlyList<RolloutComponentOutcome> components, RolloutMigrationOutcome? migration)
    {
        ArgumentNullException.ThrowIfNull(components);
        List<ReviewFinding> findings = [];
        if (migration is { Status: MigrationRunStatus.Failed })
        {
            var diverged = migration.ErrorCode is ErrorCodes.MigrationHistoryDiverged or ErrorCodes.PreviewDatabaseDiverged;
            findings.Add(new ReviewFinding(
                ReviewCategory.DataModel,
                Severity.Critical,
                diverged ? DivergedMessage : MigrationFailedMessage,
                Source: FindingSource.Deterministic,
                Evidence: Cut(migration.ErrorMessage ?? migration.ErrorCode ?? "The migration run failed.")));
        }

        foreach (var advisor in migration?.Advisors ?? [])
        {
            if (AdvisorSeverity(advisor.Level) is { } severity)
            {
                findings.Add(new ReviewFinding(
                    ReviewCategory.Security,
                    severity,
                    $"Supabase advisor: {advisor.Title}",
                    Source: FindingSource.Deterministic,
                    Evidence: Cut(string.Join("\n", new[] { advisor.Detail, advisor.Remediation }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } text ? text : advisor.Name)));
            }
        }

        foreach (var component in components.Where(c => c.Status == DeploymentStatus.Error))
        {
            var preDeploy = component.Component == DeploymentComponent.Backend
                && (component.ErrorCode == ErrorCodes.MigrationFailed || RolloutRules.IsPreDeployFailure(component.ErrorMessage));
            var evidence = string.IsNullOrWhiteSpace(component.BuildLogTail)
                ? component.ErrorMessage ?? component.Url ?? "The preview deployment failed without a build log."
                : component.BuildLogTail;
            findings.Add(new ReviewFinding(
                ReviewCategory.Architecture,
                Severity.Critical,
                preDeploy ? PreDeployFailedMessage : BuildFailedMessage(component.Platform),
                Source: FindingSource.Deterministic,
                Evidence: Cut(evidence)));
        }

        return findings;
    }

    /// <summary>A Supabase advisor level as a finding severity; <c>null</c> (no finding) for <c>INFO</c> and unknown levels.</summary>
    public static Severity? AdvisorSeverity(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "ERROR" => Severity.Major,
        "WARN" or "WARNING" => Severity.Minor,
        _ => null,
    };

    private static string Cut(string text) =>
        text.Length <= PreviewReviewRules.EvidenceMaxLength ? text : text[^PreviewReviewRules.EvidenceMaxLength..];
}
