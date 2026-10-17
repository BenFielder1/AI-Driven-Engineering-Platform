using System.Globalization;
using Aide.Domain.Gates;

namespace Aide.Domain.Notifications;

/// <summary>
/// What a notification is about, read from its gate or event. Every field but the type and project name is optional:
/// older events may lack one, and the message then leaves it out.
/// </summary>
public sealed record NotificationFacts(NotificationType Type, string ProjectName)
{
    public GateType? GateType { get; init; }

    /// <summary>The artifact's type (wire value), for gates and drafts.</summary>
    public string? ArtifactType { get; init; }

    public int? ArtifactVersion { get; init; }

    public int? StepNumber { get; init; }

    public int? Attempts { get; init; }

    public string? Agent { get; init; }

    public string? ErrorCode { get; init; }

    public int? Cycle { get; init; }

    public string? Score { get; init; }

    public string? Decision { get; init; }

    public string? Tag { get; init; }

    public int? FromVersion { get; init; }

    public int? TargetVersion { get; init; }

    public string? Reason { get; init; }

    /// <summary>FR-65: a <c>release_failed</c> notification raised by <c>RELEASE_AWAITING_MERGE</c>: the release waits for a merge on GitHub.</summary>
    public bool AwaitingMerge { get; init; }

    /// <summary>FR-65: the version's PR (awaiting merge).</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>FR-65, FR-84: the merge-readiness reasons (messages) why AIDE couldn't merge.</summary>
    public IReadOnlyList<string> MergeReasons { get; init; } = [];
}

/// <summary>A composed notification: a short title and a one-paragraph body (plain text, no secrets, no tokens).</summary>
public sealed record NotificationText(string Title, string Body);

/// <summary>FR-47: the wording of every notification type, used for the in-app centre and every channel.</summary>
public static class NotificationMessages
{
    public const int TitleMaxLength = 300;
    public const int BodyMaxLength = 2000;

    public static NotificationText Compose(NotificationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var project = OneLine(facts.ProjectName, 120);
        var gate = facts.GateType is { } g ? GateName(g) : "An approval";
        var artifact = facts.ArtifactType is { } a ? Humanize(a) : "An artifact";
        var version = facts.ArtifactVersion is { } v ? $" v{N(v)}" : string.Empty;
        var reason = string.IsNullOrWhiteSpace(facts.Reason) ? string.Empty : $" Reason: {OneLine(facts.Reason, 300)}";

        var (title, body) = facts.Type switch
        {
            NotificationType.GateRequired => (
                $"{project}: {gate} needs a decision",
                $"{gate} is waiting for an Owner's decision on {project}.{Subject(facts)}"),
            NotificationType.GateReminder => (
                $"Reminder — {project}: {gate} is still waiting",
                $"{gate} on {project} has been pending for more than 24 hours.{Subject(facts)}"),
            NotificationType.GateOverdue => (
                $"Overdue — {project}: {gate} is overdue",
                $"{gate} on {project} has been pending for more than 72 hours. Nothing is decided automatically.{Subject(facts)}"),
            NotificationType.ArtifactReady => (
                $"{project}: {artifact}{version} is ready for approval",
                $"A new draft of {artifact}{version} on {project} is waiting for an Owner's approval."),
            NotificationType.StepBlocked => (
                $"{project}: step {Opt(facts.StepNumber)} is blocked",
                $"Execution step {Opt(facts.StepNumber)} on {project} was rejected {Opt(facts.Attempts)} times and is blocked. An escalation needs a decision."),
            NotificationType.RunFailed => (
                $"{project}: {Agent(facts)} run failed",
                $"The {Agent(facts)} run on {project} failed{(facts.ErrorCode is { } code ? $" ({OneLine(code, 60)})" : string.Empty)}."),
            NotificationType.BudgetExceeded => (
                $"{project}: token budget used up",
                $"The {Agent(facts)} run on {project} was refused: the project's token budget is used up. Raise the budget in the project settings to continue."),
            NotificationType.ReviewCompleted => (
                $"{project}: review cycle {Opt(facts.Cycle)} completed",
                $"Review cycle {Opt(facts.Cycle)} on {project} completed with score {facts.Score ?? "?"}: {Humanize(facts.Decision ?? "unknown")}."),
            NotificationType.ReleaseCompleted => (
                $"{project}: released {facts.Tag ?? string.Empty}".TrimEnd(),
                $"{project} was released{(facts.Tag is { } tag ? $" as {OneLine(tag, 100)}" : string.Empty)}."),
            NotificationType.ReleaseFailed when facts.AwaitingMerge => (
                $"{project}: release {facts.Tag ?? string.Empty} awaits a merge on GitHub".Replace("  ", " ", StringComparison.Ordinal),
                $"AIDE can't merge the pull request of {project}{(facts.PullRequestUrl is { } url ? $" ({OneLine(url, 300)})" : string.Empty)} itself. Merge it on GitHub; AIDE then tags the merge commit and completes the release.{MergeReasonsText(facts.MergeReasons)}"),
            NotificationType.ReleaseFailed => (
                $"{project}: release failed",
                $"Releasing {project} failed{(facts.ErrorCode is { } code ? $" ({OneLine(code, 60)})" : string.Empty)}. An Owner can retry the release from the project page."),
            NotificationType.Rollback => (
                $"{project}: rolled back to checkpoint {Opt(facts.TargetVersion)}",
                $"{project} was rolled back from checkpoint {Opt(facts.FromVersion)} to checkpoint {Opt(facts.TargetVersion)}.{reason}"),
            _ => ($"{project}: update", $"There is an update on {project}."),
        };

        return new NotificationText(Truncate(title, TitleMaxLength), Truncate(body, BodyMaxLength));
    }

    public static string GateName(GateType type) => type switch
    {
        GateType.SpecApproval => "The specification approval",
        GateType.PlanApproval => "The plan approval",
        GateType.PreRelease => "The pre-release approval",
        GateType.Escalation => "An escalation",
        _ => "An approval",
    };

    private static string MergeReasonsText(IReadOnlyList<string> reasons) =>
        reasons.Count == 0 ? string.Empty : $" Reasons: {OneLine(string.Join(" ", reasons), 600)}";

    private static string Subject(NotificationFacts facts) =>
        facts.ArtifactType is { } a
            ? $" It approves {Humanize(a)}{(facts.ArtifactVersion is { } v ? $" v{N(v)}" : string.Empty)}."
            : string.Empty;

    private static string Agent(NotificationFacts facts) => facts.Agent is { } a ? Humanize(a) : "agent";

    private static string Opt(int? value) => value is { } v ? N(v) : "?";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Humanize(string wire) => OneLine(wire.Replace('_', ' '), 80);

    private static string OneLine(string value, int max) =>
        Truncate(string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim(), max);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
