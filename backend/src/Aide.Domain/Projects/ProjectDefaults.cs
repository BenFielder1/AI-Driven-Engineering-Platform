using Aide.Domain.Gates;

namespace Aide.Domain.Projects;

/// <summary>New-project defaults (FR-5).</summary>
public static class ProjectDefaults
{
    public const ApprovalMode Mode = ApprovalMode.Advisory;
    public const decimal ComplianceThreshold = 0.90m;
    public const int MaxFilesPerStep = 20;
    public const int MaxReviewCycles = 3;
    public const int InitialVersion = 1;
}
