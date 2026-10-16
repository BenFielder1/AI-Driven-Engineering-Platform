using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Projects;

/// <summary>Where the key of an agent run comes from (FR-79, D-V2-5).</summary>
public enum LlmKeySource
{
    /// <summary>Ollama and Fake need no key.</summary>
    NotNeeded = 0,

    /// <summary>The connection an Owner bound to the project, whoever triggered the run.</summary>
    Connection = 1,

    /// <summary>The system key from configuration: only with <c>Llm:AllowSystemKeys=true</c> and no binding.</summary>
    System = 2,

    /// <summary>No usable key: the run fails with <c>PROVIDER_NOT_CONFIGURED</c> without calling the provider.</summary>
    None = 3,
}

/// <summary>Why a project's LLM binding was cleared (FR-79). Wire values are snake_case.</summary>
public enum LlmBindingClearedReason
{
    /// <summary>The binding Owner's role changed to Contributor or Viewer.</summary>
    OwnerRoleLost = 0,

    /// <summary>The binding Owner left or was removed from the project.</summary>
    MemberRemoved = 1,

    /// <summary>The binding Owner deleted the connection.</summary>
    ConnectionDeleted = 2,
}

/// <summary>A user's LLM connection as the binding rules see it (no secret).</summary>
public sealed record LlmConnectionRef(Guid Id, Guid UserId, LlmProvider Provider);

/// <summary>
/// FR-78, FR-79, D-V2-5: rules for personal LLM connections and their binding to projects. Connections hold Anthropic or
/// OpenAI keys; Ollama and Fake need none. An Owner binds one of their own connections for the project's provider, and
/// every run of the project uses it. System keys apply only when allowed and nothing is bound.
/// </summary>
public static class LlmConnectionRules
{
    public const int MaxLabelLength = 100;

    /// <summary>Longest accepted API key (keys are a few hundred characters at most).</summary>
    public const int MaxApiKeyLength = 1000;

    /// <summary>Providers that are always available without a key (FR-78), listed next to the user's connections.</summary>
    public static IReadOnlyList<LlmProvider> KeylessProviders { get; } =
        [.. Enum.GetValues<LlmProvider>().Where(p => !ProjectSettingsRules.RequiresApiKey(p))];

    /// <summary>FR-78: a new connection: a provider that uses a key, a label (1–100 characters after trimming) and a
    /// well-formed key (not blank, no whitespace or control characters).</summary>
    public static Result ValidateNew(LlmProvider provider, string? label, string? apiKey)
    {
        var problems = new List<string>();
        if (!ProjectSettingsRules.RequiresApiKey(provider))
        {
            problems.Add($"provider: {provider.ToWire()} needs no key; it is always available.");
        }

        AddLabelProblems(label, problems);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            problems.Add("apiKey: The API key is required.");
        }
        else if (apiKey.Length > MaxApiKeyLength)
        {
            problems.Add($"apiKey: The API key can't be longer than {MaxApiKeyLength} characters.");
        }
        else if (!ProjectSettingsRules.IsWellFormedApiKey(apiKey))
        {
            problems.Add("apiKey: The API key can't contain spaces, line breaks or other control characters.");
        }

        return ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    /// <summary>FR-78: renaming a connection.</summary>
    public static Result ValidateLabel(string? label)
    {
        var problems = new List<string>();
        AddLabelProblems(label, problems);
        return ProjectRules.ToResult(problems, "The connection is invalid.");
    }

    /// <summary>
    /// FR-79: whether <paramref name="callerId"/> may bind <paramref name="connection"/> for <paramref name="provider"/>:
    /// only a project Owner (<c>FORBIDDEN</c>), only an existing connection (<c>NOT_FOUND</c>), only their own
    /// (<c>CREDENTIAL_NOT_OWNER</c>), and only for the project's provider (<c>VALIDATION</c> on <c>credentialId</c>).
    /// </summary>
    public static Result CanBind(Guid callerId, ProjectRole? callerRole, LlmConnectionRef? connection, LlmProvider provider)
    {
        if (callerRole != ProjectRole.Owner)
        {
            return Result.Failure(new DomainError(ErrorCodes.Forbidden, "Only a project Owner can bind an LLM connection."));
        }

        if (connection is null)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "The connection was not found."));
        }

        if (connection.UserId != callerId)
        {
            return Result.Failure(new DomainError(ErrorCodes.CredentialNotOwner, "You can only bind your own connections."));
        }

        return connection.Provider == provider
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The LLM settings are invalid.")
            {
                Details = [$"credentialId: The connection is for {connection.Provider.ToWire()}, not {provider.ToWire()}."],
            });
    }

    /// <summary>FR-79: a binding is for one provider, so changing the project's provider drops a binding of another.</summary>
    public static bool KeepsBinding(LlmProvider? boundProvider, LlmProvider newProvider) =>
        boundProvider is { } bound && bound == newProvider;

    /// <summary>
    /// D-V2-5, FR-79: the key a run of <paramref name="provider"/> uses: none for Ollama/Fake; the bound connection when it
    /// is for that provider; else the system key, only when <paramref name="allowSystemKeys"/> and one is configured.
    /// </summary>
    public static LlmKeySource ChooseKey(LlmProvider provider, LlmProvider? boundProvider, bool allowSystemKeys, bool hasSystemKey)
    {
        if (!ProjectSettingsRules.RequiresApiKey(provider))
        {
            return LlmKeySource.NotNeeded;
        }

        if (boundProvider == provider)
        {
            return LlmKeySource.Connection;
        }

        return allowSystemKeys && hasSystemKey ? LlmKeySource.System : LlmKeySource.None;
    }

    /// <summary>FR-79: a role change clears the binding when the binding Owner stops being an Owner.</summary>
    public static bool RoleChangeClearsBinding(Guid? boundByUserId, Guid memberUserId, ProjectRole newRole) =>
        boundByUserId == memberUserId && newRole != ProjectRole.Owner;

    /// <summary>FR-79: removing (or leaving) clears the binding when the member is the binding Owner.</summary>
    public static bool RemovalClearsBinding(Guid? boundByUserId, Guid memberUserId) => boundByUserId == memberUserId;

    /// <summary>FR-78: a provider rejecting the key during a run (HTTP 401) marks the connection <c>invalid</c>; other
    /// failures leave its status alone.</summary>
    public static bool RunFailureInvalidatesKey(int? providerStatus) => providerStatus == 401;

    /// <summary>How a connection is shown: <c>Label (••••a1b2)</c>.</summary>
    public static string DisplayName(string label, string last4) => $"{label} (••••{last4})";

    private static void AddLabelProblems(string? label, List<string> problems)
    {
        var trimmed = label?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problems.Add("label: A label is required.");
        }
        else if (trimmed.Length > MaxLabelLength)
        {
            problems.Add($"label: The label can't be longer than {MaxLabelLength} characters.");
        }
        else if (trimmed.Any(char.IsControl))
        {
            problems.Add("label: The label can't contain control characters.");
        }
    }
}
