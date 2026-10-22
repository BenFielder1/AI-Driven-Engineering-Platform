using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Llm;

namespace Aide.Api.Features.Projects;

/// <summary>Entity → DTO mapping for the projects feature. Secrets only ever become set / not set flags.</summary>
internal static class ProjectMapping
{
    /// <param name="p">The project.</param>
    /// <param name="myRole">The caller's project role.</param>
    /// <param name="preset">The preset pinned on the current release version.</param>
    /// <param name="deploymentsEnabled">FR-88: the project has deployment settings.</param>
    public static ProjectDto ToDto(Project p, ProjectRole? myRole, Presets.ProjectPresetRef preset, bool deploymentsEnabled) => new(
        p.Id,
        p.Name,
        p.Slug,
        p.Idea,
        p.CurrentPhase,
        p.Status,
        p.Checkpoint,
        p.CurrentReleaseVersionId,
        p.ReleasedVersion,
        p.DevelopmentVersion,
        p.ApprovalMode,
        p.ComplianceThreshold,
        p.MaxFilesPerStep,
        p.MaxReviewCycles,
        p.TokenBudget,
        p.TokensUsed,
        p.ExecutionPaused,
        myRole,
        p.CreatedAt,
        p.UpdatedAt,
        preset.PresetId,
        preset.PresetName,
        preset.PresetVersion,
        deploymentsEnabled);

    /// <param name="s">The project's Git settings.</param>
    /// <param name="branchName">The working branch of the project's current release version (§4.10), if recorded.</param>
    /// <param name="appInstalledOnRepo">FR-83: whether the App can access a PAT project's repo (<c>null</c>: unknown).</param>
    /// <param name="canSwitchToApp">FR-83: the App can access the repo and the caller is an Owner.</param>
    /// <param name="mergeStrategy">FR-65: the project's merge strategy.</param>
    /// <param name="tagPrefix">§4.10: the project's tag prefix.</param>
    public static GitSettingsDto ToDto(
        ProjectGitSettings s, string? branchName, MergeStrategy mergeStrategy, string tagPrefix, bool? appInstalledOnRepo = null, bool canSwitchToApp = false) =>
        new(
            s.Provider,
            s.RepoUrl,
            s.DefaultBranch,
            branchName,
            s.TokenEncrypted is not null,
            s.Provider,
            s.InstallationId,
            s.RepoId,
            s.RepoFullName,
            appInstalledOnRepo,
            canSwitchToApp,
            mergeStrategy,
            tagPrefix);

    /// <param name="s">The project's LLM settings.</param>
    /// <param name="catalog">The system LLM configuration.</param>
    /// <param name="binding">The bound connection (FR-79), if any.</param>
    /// <param name="canBind">Whether the caller is a project Owner.</param>
    public static LlmSettingsDto ToDto(ProjectLlmSettings s, ILlmProviderCatalog catalog, LlmBindingDto? binding, bool canBind)
    {
        var bound = binding is not null && binding.Provider == s.Provider ? binding : null;
        var providers = Enum.GetValues<LlmProvider>()
            .Select(p => new LlmProviderStatusDto(
                p,
                ProjectSettingsRules.IsConfigured(p, catalog.HasSystemKey(p), bound is not null && s.Provider == p),
                catalog.DefaultModel(p)))
            .ToList();
        return new LlmSettingsDto(s.Provider, s.DefaultModel, ToAgentModels(s.ModelsByAgent), bound is not null, providers, bound, canBind);
    }

    public static NotificationSettingsDto ToDto(ProjectNotificationSettings s) =>
        new([.. s.Channels.Select(ToDto)]);

    /// <summary>Slack/Teams targets are already stored masked; masking again also covers rows saved before the URL
    /// became a secret.</summary>
    public static NotificationChannelDto ToDto(NotificationChannelConfig c) => new(
        c.Id,
        c.Type,
        ProjectSettingsRules.UrlIsSecret(c.Type) ? ProjectSettingsRules.MaskUrl(c.Target) : c.Target,
        c.SecretEncrypted is not null,
        [.. c.Events.Select(e => EnumStrings.TryParse<NotificationEvent>(e, out var value) ? (NotificationEvent?)value : null)
            .OfType<NotificationEvent>()]);

    /// <summary>Stored map (agent wire name → model) → DTO.</summary>
    public static AgentModelsDto ToAgentModels(IReadOnlyDictionary<string, string> models)
    {
        string? Get(AgentKind agent) => models.TryGetValue(agent.ToWire(), out var model) ? model : null;
        return new AgentModelsDto
        {
            IdeaRefinement = Get(AgentKind.IdeaRefinement),
            SpecGenerator = Get(AgentKind.SpecGenerator),
            PlanGenerator = Get(AgentKind.PlanGenerator),
            CodeGenerator = Get(AgentKind.CodeGenerator),
            CodeReview = Get(AgentKind.CodeReview),
        };
    }

    /// <summary>DTO → (agent, sent value) pairs, including blank values so they can be rejected.</summary>
    public static IReadOnlyList<(AgentKind Agent, string FieldName, string? Model)> FromAgentModels(AgentModelsDto dto) =>
    [
        (AgentKind.IdeaRefinement, "ideaRefinement", dto.IdeaRefinement),
        (AgentKind.SpecGenerator, "specGenerator", dto.SpecGenerator),
        (AgentKind.PlanGenerator, "planGenerator", dto.PlanGenerator),
        (AgentKind.CodeGenerator, "codeGenerator", dto.CodeGenerator),
        (AgentKind.CodeReview, "codeReview", dto.CodeReview),
    ];
}

/// <summary>Combines validation results into one <c>VALIDATION</c> error listing every problem.</summary>
internal static class Validation
{
    public static Result Combine(params Result[] results)
    {
        var failures = results.Where(r => r.IsFailure).Select(r => r.Error!).ToList();
        return failures.Count switch
        {
            0 => Result.Success(),
            1 => Result.Failure(failures[0]),
            _ => Result.Failure(new DomainError(ErrorCodes.Validation, "One or more fields are invalid.")
            {
                Details = [.. failures.SelectMany(f => f.Details)],
            }),
        };
    }
}
