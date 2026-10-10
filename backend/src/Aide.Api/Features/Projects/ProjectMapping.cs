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
    public static ProjectDto ToDto(Project p, ProjectRole? myRole) => new(
        p.Id,
        p.Name,
        p.Slug,
        p.Idea,
        p.CurrentPhase,
        p.Status,
        p.ProjectVersion,
        p.ApprovalMode,
        p.ComplianceThreshold,
        p.MaxFilesPerStep,
        p.MaxReviewCycles,
        p.TokenBudget,
        p.TokensUsed,
        p.ExecutionPaused,
        myRole,
        p.CreatedAt,
        p.UpdatedAt);

    public static GitSettingsDto ToDto(ProjectGitSettings s) =>
        new(s.Provider, s.RepoUrl, s.DefaultBranch, s.BranchName, s.TokenEncrypted is not null);

    public static LlmSettingsDto ToDto(ProjectLlmSettings s, ILlmProviderCatalog catalog)
    {
        var hasKey = s.ApiKeyEncrypted is not null;
        var providers = Enum.GetValues<LlmProvider>()
            .Select(p => new LlmProviderStatusDto(
                p,
                ProjectSettingsRules.IsConfigured(p, catalog.HasSystemKey(p), hasKey && s.Provider == p),
                catalog.DefaultModel(p)))
            .ToList();
        return new LlmSettingsDto(s.Provider, s.DefaultModel, ToAgentModels(s.ModelsByAgent), hasKey, providers);
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
