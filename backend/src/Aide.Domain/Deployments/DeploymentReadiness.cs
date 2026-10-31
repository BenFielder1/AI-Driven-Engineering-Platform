using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>State of a readiness item (FR-144). Wire values: <c>ok</c>, <c>warning</c>, <c>error</c>, <c>na</c>.</summary>
public enum ReadinessState
{
    Ok = 0,
    Warning = 1,
    Error = 2,
    Na = 3,
}

/// <summary>One readiness checklist item: a stable <see cref="Id"/>, its state, a message and an optional fix link.</summary>
public sealed record ReadinessItem(string Id, ReadinessState State, string Message, string? FixUrl = null);

/// <summary>The bound connection as readiness sees it.</summary>
public enum ReadinessConnection
{
    Valid = 0,
    Invalid = 1,
    InsufficientScope = 2,
    Missing = 3,
    Unchecked = 4,
}

/// <summary>Whether AIDE's branches are kept from deploying on their own (§4.18 <c>deploy_branch_control</c>).</summary>
public enum BranchControlState
{
    Ok = 0,
    Missing = 1,
    Unknown = 2,
    PlatformManaged = 3,
    NotApplicable = 4,
}

/// <summary>
/// The facts of a readiness check. <see cref="Checked"/> is false when only AIDE's own data was read (Settings → Deployment
/// GET); then the platform-dependent items say "verify to check". <see cref="GitAccess"/>: <c>null</c> when unknown.
/// </summary>
public sealed record ReadinessInput(
    DeploymentProvider Provider,
    bool Checked,
    ReadinessConnection Connection,
    string? ConnectionFixUrl,
    bool? GitAccess,
    string? GitFixUrl,
    string? GitMessage,
    IReadOnlyList<string> MissingEnv,
    BranchControlState BranchControl,
    string? BranchControlPath,
    bool? PreviewReady);

/// <summary>
/// FR-144 readiness checklist, pure: <c>connection</c>, <c>git_access</c>, <c>required_env</c>, <c>branch_control</c>,
/// <c>preview_environment</c> (<c>na</c> where the provider has no previews) and <c>plan_notes</c> (§4.20, not enforced).
/// </summary>
public static class ReadinessRules
{
    public const string Connection = "connection";
    public const string GitAccess = "git_access";
    public const string RequiredEnv = "required_env";
    public const string BranchControl = "branch_control";
    public const string PreviewEnvironment = "preview_environment";
    public const string PlanNotes = "plan_notes";

    private const string VerifyToCheck = "Not checked yet: run Verify.";

    public static IReadOnlyList<ReadinessItem> Checklist(ReadinessInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var platform = DeploymentPlatforms.DisplayName(input.Provider);
        var caps = DeploymentPlatforms.Capabilities(input.Provider);
        var items = new List<ReadinessItem>
        {
            input.Connection switch
            {
                ReadinessConnection.Valid => new(Connection, ReadinessState.Ok, DeploymentPlatforms.NeedsConnection(input.Provider)
                    ? $"The {platform} connection is valid."
                    : "The project's GitHub App installation or token is used.", null),
                ReadinessConnection.Invalid => new(Connection, ReadinessState.Error, $"{platform} rejected the connection's token. Add a new token and bind it.", input.ConnectionFixUrl),
                ReadinessConnection.InsufficientScope => new(Connection, ReadinessState.Error, $"The {platform} token is missing permissions.", input.ConnectionFixUrl),
                ReadinessConnection.Missing => new(Connection, ReadinessState.Error, $"Bind one of your {platform} connections.", input.ConnectionFixUrl),
                _ => new(Connection, ReadinessState.Warning, $"The {platform} connection hasn't been checked yet.", input.ConnectionFixUrl),
            },
            !input.Checked && input.GitAccess is null
                ? new(GitAccess, ReadinessState.Warning, VerifyToCheck)
                : input.GitAccess switch
                {
                    true => new(GitAccess, ReadinessState.Ok, $"{platform} can access the repository."),
                    false => new(GitAccess, ReadinessState.Error, input.GitMessage ?? $"{platform} can't see this repository. Connect it in {platform}, then verify again.", input.GitFixUrl),
                    _ => new(GitAccess, ReadinessState.Warning, input.GitMessage ?? $"AIDE couldn't check {platform}'s access to the repository.", input.GitFixUrl),
                },
            input.MissingEnv.Count == 0
                ? new(RequiredEnv, ReadinessState.Ok, "Every required environment variable has a value.")
                : new(RequiredEnv, ReadinessState.Error, $"Missing: {string.Join(", ", input.MissingEnv)}."),
            input.BranchControl switch
            {
                BranchControlState.Ok => new(BranchControl, ReadinessState.Ok, $"Automatic deploys of aide/** are disabled ({input.BranchControlPath})."),
                BranchControlState.Missing => new(BranchControl, ReadinessState.Error,
                    $"{input.BranchControlPath} doesn't stop automatic deploys of aide/**; the deploy step adds it, or add it yourself."),
                BranchControlState.PlatformManaged => new(BranchControl, ReadinessState.Ok, $"{platform} builds only the production branch; AIDE deploys aide/** explicitly."),
                BranchControlState.NotApplicable => new(BranchControl, ReadinessState.Na, $"{platform} only deploys the default branch."),
                _ => new(BranchControl, ReadinessState.Warning, input.Checked ? $"AIDE couldn't read {input.BranchControlPath} yet (the repository isn't checked out)." : VerifyToCheck),
            },
            !caps.Previews
                ? new(PreviewEnvironment, ReadinessState.Na, $"{platform} has no preview deployments; review doesn't wait for one.")
                : input.PreviewReady switch
                {
                    true => new(PreviewEnvironment, ReadinessState.Ok, "The preview environment is ready."),
                    false => new(PreviewEnvironment, ReadinessState.Error, "The preview environment isn't set up."),
                    _ => new(PreviewEnvironment, ReadinessState.Warning, VerifyToCheck),
                },
        };
        var notes = DeploymentPlatforms.PlanNotes(input.Provider);
        items.Add(notes.Count == 0
            ? new(PlanNotes, ReadinessState.Na, "No plan notes.")
            : new(PlanNotes, ReadinessState.Ok, string.Join(" ", notes)));
        return items;
    }

    /// <summary>
    /// After a verify: a <c>ready</c> target with an error item needs <c>attention</c> (reason: the first error's id); an
    /// <c>attention</c> target from an earlier verify is <c>ready</c> again when nothing is an error.
    /// </summary>
    public static (DeploymentTargetStatus Status, string? Reason) StatusAfterVerify(DeploymentTargetStatus status, string? reason, IReadOnlyList<ReadinessItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var error = items.FirstOrDefault(i => i.State == ReadinessState.Error);
        if (status == DeploymentTargetStatus.Ready && error is not null)
        {
            return (DeploymentTargetStatus.Attention, error.Id);
        }

        if (status == DeploymentTargetStatus.Attention && error is null && reason != DeploymentTargetRules.ConnectionMissing)
        {
            return (DeploymentTargetStatus.Ready, null);
        }

        return (status, reason);
    }
}

/// <summary>Why a deployment target failed (<c>DEPLOYMENT_TARGET_FAILED.reason</c>, FR-155). Wire values are snake_case.</summary>
public static class DeploymentTargetFailureReasons
{
    /// <summary>The platform can no longer reach the repository (a verify or a deployment found out).</summary>
    public const string GitAccessLost = "git_access_lost";

    /// <summary>The platform rejected the bound connection's token.</summary>
    public const string TokenInvalid = "token_invalid";

    /// <summary>The target's setup failed for another reason (plan limit, missing permission, retries used up).</summary>
    public const string SetupFailed = "setup_failed";

    /// <summary>A verify found another readiness error (required env vars, branch control).</summary>
    public const string ReadinessFailed = "readiness_failed";

    /// <summary>FR-155: "deployment target needs attention" is only for lost platform Git access and an invalid token.</summary>
    public static bool NeedsOwner(string? reason) => reason is GitAccessLost or TokenInvalid;
}
