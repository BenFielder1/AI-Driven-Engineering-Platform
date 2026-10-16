using Aide.Api.Features.GitHub;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Releases;
using Aide.Infrastructure.GitHub;
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
    IGitHubAppStore apps,
    GitHubRepoService gitHubRepos,
    TimeProvider time,
    ILogger<ProjectCreator> logger)
{
    public async Task<Result<Project>> CreateAsync(CreateProjectRequest request, Guid ownerId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var git = request.Git;

        // FR-82: github_app is the default mode once an App exists (the wizard sends a mode; v1 clients a provider).
        GitProvider? sentMode = git.ModeSent ? git.Mode : null;
        GitProvider? sentProvider = git.ProviderSent ? git.Provider : null;
        if (sentMode is null && sentProvider is null && await apps.GetAsync(cancellationToken) is not null)
        {
            sentMode = GitProvider.GithubApp;
        }

        var mode = ProjectSettingsRules.ResolveGitMode(sentMode, sentProvider, prefix: "git.");
        var gitProvider = mode.IsSuccess ? mode.Value : GitProvider.Local;
        var validation = Validation.Combine(
            ProjectRules.ValidateNew(request.Name, request.Idea, request.ComplianceThreshold),
            mode.IsFailure ? Result.Failure(mode.Error!) : Result.Success(),
            ProjectSettingsRules.ValidateGit(gitProvider, git.RepoUrl, git.DefaultBranch?.Trim(), git.Token, clearToken: false, prefix: "git."),
            gitProvider == GitProvider.GithubApp
                ? ProjectSettingsRules.ValidateAppRepo(git.InstallationId, git.RepoFullName, git.RepoUrl, git.Token, prefix: "git.")
                : Result.Success());
        if (validation.IsFailure)
        {
            return Result.Failure<Project>(validation.Error!);
        }

        // FR-82: the caller must reach the repository through the App installation they picked; FR-86: and whether it has code.
        VerifiedAppRepo? appRepo = null;
        if (gitProvider == GitProvider.GithubApp)
        {
            var verified = await gitHubRepos.VerifyProjectRepoAsync(ownerId, git.InstallationId!.Value, git.RepoFullName!.Trim(), cancellationToken);
            if (verified.IsFailure)
            {
                return Result.Failure<Project>(verified.Error!);
            }

            appRepo = verified.Value;
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

            // FR-86: a picked repository that already has code: the version builds on its default branch head, never
            // released (no base tag). The import flow (FR-73) reads this as "existing code".
            BaseCommitSha = appRepo is { HasCode: true } ? appRepo.HeadSha : null,
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
            Provider = gitProvider,
            RepoUrl = ProjectSettingsRules.NormalizeRepoUrl(gitProvider, git.RepoUrl),
            DefaultBranch = DefaultBranchFor(git.DefaultBranch!.Trim(), appRepo), // validated above
            TokenEncrypted = gitProvider == GitProvider.GithubApp || string.IsNullOrEmpty(git.Token) ? null : secrets.Protect(git.Token),
            InstallationId = appRepo?.InstallationId,
            RepoId = appRepo?.RepoId,
            RepoFullName = appRepo?.FullName,
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
        db.AddEvent(
            EventTypes.ProjectCreated,
            project.Id,
            appRepo is null
                ? new { projectId = project.Id, name = project.Name, actor }
                : new { projectId = project.Id, name = project.Name, actor, repoFullName = appRepo.FullName, repoHasCode = appRepo.HasCode },
            actor,
            now);

        await db.SaveChangesAsync(cancellationToken);
        LogCreated(logger, project.Id, provider, gitProvider);
        return Result.Success(project);
    }

    /// <summary>
    /// The stored default branch: for a picked GitHub repository the wizard's default (<c>main</c>) gives way to the
    /// repository's own default branch; an explicit other branch is kept.
    /// </summary>
    private static string DefaultBranchFor(string requested, VerifiedAppRepo? appRepo) =>
        appRepo is not null && requested == ProjectSettingsRules.DefaultBranch ? appRepo.DefaultBranch : requested;

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
