using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Releases;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-5, FR-66: creates a project with everything it starts with, in one <c>SaveChanges</c> (one transaction): the project
/// (refinement / active / checkpoint 1) and its first release version (<c>in_development</c>, the initial version, default
/// <c>0.1.0</c>; nothing released yet), the Owner membership, Git settings (token encrypted), LLM settings (system
/// default provider if usable, else Fake), empty notification settings, the creation state transition (none →
/// refinement at checkpoint 1, the anchor for a rollback to checkpoint 1) and the <c>PROJECT_CREATED</c> event.
/// Used by <c>POST /api/projects</c> and the dev seed.
/// </summary>
public sealed partial class ProjectCreator(
    AideDbContext db,
    ISecretProtector secrets,
    ILlmProviderCatalog llm,
    TimeProvider time,
    ILogger<ProjectCreator> logger)
{
    public async Task<Result<Project>> CreateAsync(CreateProjectRequest request, Guid ownerId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var git = request.Git;
        var validation = Validation.Combine(
            ProjectRules.ValidateNew(request.Name, request.Idea, request.ComplianceThreshold),
            ProjectSettingsRules.ValidateGit(git.Provider, git.RepoUrl, git.DefaultBranch?.Trim(), git.Token, clearToken: false, prefix: "git."));
        if (validation.IsFailure)
        {
            return Result.Failure<Project>(validation.Error!);
        }

        var initial = ReleaseVersionRules.Parse(request.InitialVersion ?? ReleaseVersionRules.DefaultInitialVersion, "initialVersion");
        if (initial.IsFailure)
        {
            return Result.Failure<Project>(initial.Error!);
        }

        var now = time.GetUtcNow();
        var name = request.Name.Trim();
        var project = new Project
        {
            Name = name,
            Idea = request.Idea.Trim(),
            CurrentPhase = LifecycleState.Initial.Phase,
            Status = LifecycleState.Initial.Status,
            Checkpoint = LifecycleState.Initial.Version,
            ApprovalMode = request.ApprovalMode,
            ComplianceThreshold = request.ComplianceThreshold,
            MaxFilesPerStep = request.MaxFilesPerStep,
            MaxReviewCycles = request.MaxReviewCycles,
            TokenBudget = request.TokenBudget,
            CreatedAt = now,
            UpdatedAt = now,
        };
        project.Slug = await UniqueSlugAsync(name, project.Id, cancellationToken);
        var version = new ReleaseVersion
        {
            ProjectId = project.Id,
            Version = initial.Value.ToString(),
            Status = ReleaseVersionStatus.InDevelopment,
            TagPrefix = project.TagPrefix,
            Request = project.Idea,
            StartedBy = ownerId,
            CreatedAt = now,
        };
        project.CurrentReleaseVersionId = version.Id;
        project.DevelopmentVersion = version.Version;
        project.ReleasedVersion = null;

        var provider = llm.DefaultForNewProject;
        if (provider != llm.ConfiguredDefault)
        {
            LogFakeDefault(logger, llm.ConfiguredDefault, provider);
        }

        db.Projects.Add(project);
        db.ReleaseVersions.Add(version);
        db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = ownerId, Role = ProjectRole.Owner, CreatedAt = now });
        db.ProjectGitSettings.Add(new ProjectGitSettings
        {
            ProjectId = project.Id,
            Provider = git.Provider,
            RepoUrl = ProjectSettingsRules.NormalizeRepoUrl(git.Provider, git.RepoUrl),
            DefaultBranch = git.DefaultBranch!.Trim(), // validated above
            TokenEncrypted = string.IsNullOrEmpty(git.Token) ? null : secrets.Protect(git.Token),
        });
        db.ProjectLlmSettings.Add(new ProjectLlmSettings
        {
            ProjectId = project.Id,
            Provider = provider,
            DefaultModel = llm.DefaultModel(provider),
            ModelsByAgent = new Dictionary<string, string>(llm.DefaultAgentModels(provider)),
        });
        db.ProjectNotificationSettings.Add(new ProjectNotificationSettings { ProjectId = project.Id, Channels = [] });
        db.StateTransitions.Add(new StateTransition
        {
            ProjectId = project.Id,
            ReleaseVersionId = version.Id,
            FromPhase = null,
            ToPhase = project.CurrentPhase,
            Actor = actor,
            Reason = ProjectRules.CreatedReason,
            Checkpoint = project.Checkpoint,
            CreatedAt = now,
        });
        db.AddEvent(EventTypes.ProjectCreated, project.Id, new { projectId = project.Id, name = project.Name, actor }, actor, now);

        await db.SaveChangesAsync(cancellationToken);
        LogCreated(logger, project.Id, provider, git.Provider);
        return Result.Success(project);
    }

    /// <summary>Slugs carry 6 hex characters of the (random part of the) id; on the rare collision, 12.</summary>
    private async Task<string> UniqueSlugAsync(string name, Guid id, CancellationToken cancellationToken)
    {
        var slug = ProjectRules.Slug(name, id);
        if (await db.Projects.AnyAsync(p => p.Slug == slug, cancellationToken))
        {
            slug = ProjectRules.Slug(name, id, ProjectRules.SlugLongSuffixLength);
        }

        return slug;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} created (LLM provider {LlmProvider}, Git provider {GitProvider})")]
    private static partial void LogCreated(ILogger logger, Guid projectId, LlmProvider llmProvider, GitProvider gitProvider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Default LLM provider {ConfiguredProvider} has no API key configured; the new project uses {Provider}")]
    private static partial void LogFakeDefault(ILogger logger, LlmProvider configuredProvider, LlmProvider provider);
}
