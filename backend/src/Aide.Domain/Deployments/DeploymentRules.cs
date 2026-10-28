using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;

namespace Aide.Domain.Deployments;

/// <summary>FR-87: rules for personal Vercel connections (access tokens). Tokens are checked with Vercel before saving.</summary>
public static class VercelConnectionRules
{
    public const int MaxLabelLength = 100;

    /// <summary>Longest accepted token (Vercel tokens are a few dozen characters).</summary>
    public const int MaxTokenLength = 1000;

    /// <summary>FR-87: a new connection: a label (1–100 characters after trimming) and a well-formed token (not blank, no
    /// whitespace or control characters).</summary>
    public static Result ValidateNew(string? label, string? token)
    {
        var problems = new List<string>();
        AddLabelProblems(label, problems);
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add("token: The token is required.");
        }
        else if (token.Length > MaxTokenLength)
        {
            problems.Add($"token: The token can't be longer than {MaxTokenLength} characters.");
        }
        else if (!ProjectSettingsRules.IsWellFormedApiKey(token))
        {
            problems.Add("token: The token can't contain spaces, line breaks or other control characters.");
        }

        return ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    /// <summary>FR-87: renaming a connection.</summary>
    public static Result ValidateLabel(string? label)
    {
        var problems = new List<string>();
        AddLabelProblems(label, problems);
        return ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    private static void AddLabelProblems(string? label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            problems.Add("label: The label is required.");
        }
        else if (label.Trim().Length > MaxLabelLength)
        {
            problems.Add($"label: The label can't be longer than {MaxLabelLength} characters.");
        }
    }
}

/// <summary>
/// FR-88: the Deployment settings of a project. Deployment needs a GitHub-backed project (Local: <c>VALIDATION</c>); an
/// Owner binds one of their own Vercel connections and links an existing Vercel project (<c>vercelProjectId</c>) or
/// creates one (<c>create {name, rootDirectory}</c>), never both.
/// </summary>
public static partial class DeploymentSettingsRules
{
    public const int VercelProjectNameMaxLength = 100;

    public const int VercelProjectIdMaxLength = 100;

    public const int RootDirectoryMaxLength = 256;

    /// <summary>
    /// FR-88: a deployment choice, from Settings → Deployment (<paramref name="fieldPrefix"/> empty) or the wizard
    /// (<c>deployment.</c>). <paramref name="provider"/> <c>null</c> turns deployment off (no other field is allowed);
    /// <c>fake</c> only where the server allows it (<paramref name="allowFake"/>: <c>Deployments:Provider=Fake</c>, which also
    /// lets Local Git projects deploy, for E2E). <paramref name="requireConnection"/>: the wizard must name a connection;
    /// settings may keep the bound one.
    /// </summary>
    public static Result Validate(
        DeploymentProvider? provider,
        Guid? connectionId,
        string? vercelProjectId,
        bool create,
        string? createName,
        string? createRootDirectory,
        GitProvider gitProvider,
        bool allowFake,
        bool requireConnection,
        string fieldPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(fieldPrefix);
        var problems = new List<string>();
        if (provider is null)
        {
            if (connectionId is not null || vercelProjectId is not null || create)
            {
                problems.Add($"{fieldPrefix}provider: Choose a provider to bind a connection or link a Vercel project.");
            }

            return ProjectRules.ToResult(problems, "The deployment settings are invalid.");
        }

        if (provider == DeploymentProvider.Fake && !allowFake)
        {
            problems.Add($"{fieldPrefix}provider: The fake provider is only available in test environments.");
        }

        // §8 E2E (Fake LLM + Local Git + Fake deployments): with the Fake provider switched on, Local projects may deploy.
        if (gitProvider == GitProvider.Local && !allowFake)
        {
            problems.Add($"{fieldPrefix}provider: Deployment needs a GitHub repository; this project uses Local Git.");
        }

        if (requireConnection && connectionId is null)
        {
            problems.Add($"{fieldPrefix}connectionId: Choose one of your Vercel connections.");
        }

        if (vercelProjectId is not null && create)
        {
            problems.Add($"{fieldPrefix}create: Link an existing Vercel project or create one, not both.");
        }

        if (vercelProjectId is not null && !IsVercelProjectId(vercelProjectId))
        {
            problems.Add($"{fieldPrefix}vercelProjectId: Not a Vercel project id or name.");
        }

        if (create)
        {
            if (!IsVercelProjectName(createName))
            {
                problems.Add($"{fieldPrefix}create.name: Up to {VercelProjectNameMaxLength} lowercase letters, digits, '.', '_' or '-', without '---'.");
            }

            if (createRootDirectory is not null && !IsRootDirectory(createRootDirectory))
            {
                problems.Add($"{fieldPrefix}create.rootDirectory: A relative path inside the repository (no '..', no leading '/').");
            }
        }

        return ProjectRules.ToResult(problems, "The deployment settings are invalid.");
    }

    /// <summary>
    /// FR-88 (as FR-79 for LLM connections): only a project Owner (<c>FORBIDDEN</c>) may bind, only an existing connection
    /// (<c>NOT_FOUND</c>), and only their own (<c>CREDENTIAL_NOT_OWNER → 403</c>).
    /// </summary>
    public static Result CanBind(Guid callerId, ProjectRole? callerRole, Guid? connectionOwnerId)
    {
        if (callerRole != ProjectRole.Owner)
        {
            return Result.Failure(new DomainError(ErrorCodes.Forbidden, "Only a project Owner can bind a Vercel connection."));
        }

        if (connectionOwnerId is not { } ownerId)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "The connection was not found."));
        }

        return ownerId == callerId
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.CredentialNotOwner, "You can only bind your own connections."));
    }

    /// <summary>
    /// FR-88, §4.22: deployment is offered when the version's stack lists <c>vercel</c> among its deploy providers, or an Owner opted in
    /// by setting a provider.
    /// </summary>
    public static bool Supported(IReadOnlyCollection<string>? deployProviders, bool providerSet) =>
        providerSet || (deployProviders?.Contains("vercel", StringComparer.OrdinalIgnoreCase) ?? false);

    /// <summary>FR-88: a binding Owner who stops being an Owner takes their connection with them (as FR-79).</summary>
    public static bool RoleChangeClearsBinding(Guid? boundByUserId, Guid memberUserId, ProjectRole newRole) =>
        boundByUserId == memberUserId && newRole != ProjectRole.Owner;

    /// <summary>FR-88: removing (or leaving) clears the binding when the member is the binding Owner.</summary>
    public static bool RemovalClearsBinding(Guid? boundByUserId, Guid memberUserId) => boundByUserId == memberUserId;

    /// <summary>
    /// FR-88: the repository a Vercel project linked to (<paramref name="linkedRepo"/>, <c>owner/name</c>; <c>null</c>:
    /// not linked) fits the project's repository: unlinked, or the same repository (case-insensitive, as GitHub).
    /// </summary>
    public static bool LinkMatches(string? linkedRepo, string? projectRepo) =>
        string.IsNullOrEmpty(linkedRepo) || string.IsNullOrEmpty(projectRepo)
        || string.Equals(linkedRepo, projectRepo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Vercel project names: lowercase letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, up to 100, without <c>---</c>, not only
    /// dots (<c>.</c>, <c>..</c> would be path segments in Vercel's routes), no path separators.
    /// </summary>
    public static bool IsVercelProjectName(string? name) =>
        name is not null && name.Length <= VercelProjectNameMaxLength && ProjectNamePattern().IsMatch(name)
        && !name.Contains("---", StringComparison.Ordinal) && !OnlyDots(name);

    /// <summary>A Vercel project id (<c>prj_…</c>) or name as Vercel accepts in its project routes (not only dots, no separators).</summary>
    public static bool IsVercelProjectId(string? id) =>
        id is not null && id.Length <= VercelProjectIdMaxLength && ProjectIdPattern().IsMatch(id) && !OnlyDots(id);

    /// <summary>
    /// FR-88, §2 (connections are personal): changing what the project deploys to (the provider, or linking or creating a
    /// Vercel project) uses a Vercel token, so it needs the caller's own: the caller sends their own <paramref name="requestConnectionId"/>
    /// (checked by <see cref="CanBind"/>, rebinding to the caller) or is the Owner who bound the current one
    /// (<paramref name="boundByUserId"/>); otherwise <c>CREDENTIAL_NOT_OWNER → 403</c>. Turning deployment off uses no token.
    /// </summary>
    public static Result CanUseBinding(Guid callerId, Guid? boundByUserId, Guid? requestConnectionId, bool turningOff)
    {
        if (turningOff || requestConnectionId is not null || boundByUserId is null || boundByUserId == callerId)
        {
            return Result.Success();
        }

        return Result.Failure(new DomainError(
            ErrorCodes.CredentialNotOwner,
            "The project's Vercel connection belongs to another Owner. Bind one of your own connections to change the deployment."));
    }

    private static bool OnlyDots(string value) => value.All(c => c == '.');

    /// <summary>A relative directory inside the repository: <c>.</c> or segments of <c>[A-Za-z0-9._-]</c>, no <c>..</c>.</summary>
    public static bool IsRootDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > RootDirectoryMaxLength)
        {
            return false;
        }

        if (path == ProjectDeploymentSettings.DefaultRootDirectory)
        {
            return true;
        }

        var segments = path.TrimEnd('/').Split('/');
        return segments.All(s => s.Length > 0 && s != "." && s != ".." && SegmentPattern().IsMatch(s));
    }

    [GeneratedRegex("^[a-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNamePattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}

/// <summary>FR-88, §4.15 env sync: project environment variables pushed to Vercel. Values are never returned or logged.</summary>
public static partial class DeploymentEnvRules
{
    /// <summary>The largest value accepted, in UTF-8 bytes (64 KB, Vercel's limit for one variable).</summary>
    public const int ValueMaxBytes = 64 * 1024;

    /// <summary>FR-88: a key (<c>^[A-Za-z_][A-Za-z0-9_]*$</c>, up to 256), a non-empty value up to 64 KB and at least one
    /// target, each once.</summary>
    public static Result Validate(string? key, string? value, IReadOnlyCollection<DeploymentTarget>? targets)
    {
        var problems = new List<string>();
        if (!IsKey(key))
        {
            problems.Add($"key: Letters, digits and '_', not starting with a digit, up to {DeploymentEnvVar.KeyMaxLength} characters.");
        }

        if (string.IsNullOrEmpty(value))
        {
            problems.Add("value: The value is required.");
        }
        else if (Encoding.UTF8.GetByteCount(value) > ValueMaxBytes)
        {
            problems.Add("value: The value can't be larger than 64 KB.");
        }

        if (targets is null || targets.Count == 0)
        {
            problems.Add("targets: Choose preview, production or both.");
        }
        else if (targets.Distinct().Count() != targets.Count)
        {
            problems.Add("targets: Each target can be listed once.");
        }

        return ProjectRules.ToResult(problems, "The environment variable is invalid.");
    }

    /// <summary>
    /// FR-88: whether a required variable is present: the project has the key, for every target the stack requires it in
    /// (<paramref name="requiredTargets"/>; none listed: any target).
    /// </summary>
    public static bool IsPresent(
        string key, IReadOnlyCollection<DeploymentTarget> requiredTargets, IReadOnlyDictionary<string, IReadOnlyList<DeploymentTarget>> projectVars)
    {
        ArgumentNullException.ThrowIfNull(requiredTargets);
        ArgumentNullException.ThrowIfNull(projectVars);
        return projectVars.TryGetValue(key, out var targets) && requiredTargets.All(targets.Contains);
    }

    /// <summary>FR-88: an environment variable key.</summary>
    public static bool IsKey(string? key) => key is not null && key.Length <= DeploymentEnvVar.KeyMaxLength && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>FR-90, FR-91: who may act on an existing deployment, and which deployments can be promoted.</summary>
public static class DeploymentActionRules
{
    /// <summary>
    /// FR-90 ("a Contributor can redeploy" the preview) and FR-91 / §6 (production redeploy is Owner-only): redeploying a
    /// preview is a writer action, redeploying production an Owner action.
    /// </summary>
    public static ProjectAction RedeployAction(DeploymentTarget target) =>
        target == DeploymentTarget.Production ? ProjectAction.ManageDeployments : ProjectAction.TriggerDeployment;

    /// <summary>FR-91: only a <c>ready</c> production deployment can be promoted (<c>INVALID_DEPLOYMENT → 409</c>).</summary>
    public static Result CanPromote(DeploymentTarget target, DeploymentStatus status)
    {
        if (target != DeploymentTarget.Production)
        {
            return Result.Failure(new DomainError(ErrorCodes.InvalidDeployment, "Only a production deployment can be promoted."));
        }

        return status == DeploymentStatus.Ready
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.InvalidDeployment, "Only a ready deployment can be promoted."));
    }
}
