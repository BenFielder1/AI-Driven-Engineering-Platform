using Aide.Api.Features.GitHub;
using Aide.Api.Features.Versions;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-5, FR-66, FR-73, FR-86: creates a project with everything it starts with, in one <c>SaveChanges</c> (one
/// transaction): the project (refinement / active / checkpoint 1) and its release versions, the Owner membership, Git
/// settings (token encrypted), LLM settings (system default provider if usable, else Fake), empty notification settings,
/// the creation state transition (none → refinement at checkpoint 1, the anchor for a rollback to checkpoint 1) and the
/// <c>PROJECT_CREATED</c> event.
/// <list type="bullet">
/// <item><b>Scratch</b> (FR-66): one <c>in_development</c> version, the initial version (default <c>0.1.0</c>). A picked
/// GitHub repository that already has code (FR-86) is treated as an import with "Never released": the version's base is
/// the default branch head and the Repository Analyst is queued.</item>
/// <item><b>Import</b> (FR-73): the repository's tags are read (GitHub App: through the user's connection; PAT: the
/// REST API with the token; Local: the repository copied under <c>Git:Local:ImportRoot</c>) and checked
/// (<c>TAG_NOT_FOUND</c>, <c>TAG_EXISTS</c>). An <c>imported</c> version for the released tag (its tag and commit, no
/// artifacts) unless "Never released", plus the <c>in_development</c> version based on that tag's commit (or the default
/// branch head); the idea is the version's request; the Repository Analyst is queued (FR-70).</item>
/// </list>
/// Used by <c>POST /api/projects</c> and the dev seed.
/// </summary>
public sealed partial class ProjectCreator(
    AideDbContext db,
    ISecretProtector secrets,
    ILlmProviderCatalog llm,
    IGitHubAppStore apps,
    GitHubRepoService gitHubRepos,
    GitHubApi gitHubApi,
    IWorkingCopies workingCopies,
    IWorkspaceStore workspaces,
    JobEnqueuer jobs,
    IOptions<LocalGitOptions> localGit,
    TimeProvider time,
    ILogger<ProjectCreator> logger)
{
    /// <summary>The <c>request</c> recorded on an imported baseline version (it was never developed in AIDE).</summary>
    public const string ImportedRequest = "Imported baseline.";

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
                : Result.Success(),
            ImportRules.ValidateSource(request.Source, request.Import is not null, request.InitialVersion is not null, git.LocalSourcePath is not null, gitProvider));
        if (validation.IsFailure)
        {
            return Result.Failure<Project>(validation.Error!);
        }

        var now = time.GetUtcNow();
        var project = new Project
        {
            Name = request.Name.Trim(),
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

        Result<RepositoryStart> start = request.Source == ProjectSource.Import
            ? await ImportStartAsync(request, project, gitProvider, ownerId, cancellationToken)
            : await ScratchStartAsync(request, gitProvider, ownerId, cancellationToken);
        if (start.IsFailure)
        {
            return Result.Failure<Project>(start.Error!);
        }

        try
        {
            return await SaveAsync(request, project, start.Value, gitProvider, ownerId, actor, now, cancellationToken);
        }
        catch
        {
            if (start.Value.LocalCopy)
            {
                // The Local import's working copy belongs to a project that was never created.
                workspaces.TryDelete(project.Id);
            }

            throw;
        }
    }

    /// <summary>What the repository gives the new project: its versions, base commit, default branch and the App repo.</summary>
    private sealed record RepositoryStart(
        SemVer DevelopmentVersion,
        string? ReleasedTag,
        SemVer? ReleasedVersion,
        string? ReleasedCommit,
        string? HeadCommit,
        string? DefaultBranch,
        VerifiedAppRepo? AppRepo,
        bool LocalCopy,
        bool Imported);

    /// <summary>FR-66, FR-82, FR-86: the initial version; a picked App repository is verified (and checked for code).</summary>
    private async Task<Result<RepositoryStart>> ScratchStartAsync(CreateProjectRequest request, GitProvider gitProvider, Guid ownerId, CancellationToken cancellationToken)
    {
        var git = request.Git;
        VerifiedAppRepo? appRepo = null;
        if (gitProvider == GitProvider.GithubApp)
        {
            var verified = await gitHubRepos.VerifyProjectRepoAsync(ownerId, git.InstallationId!.Value, git.RepoFullName!.Trim(), cancellationToken);
            if (verified.IsFailure)
            {
                return Result.Failure<RepositoryStart>(verified.Error!);
            }

            appRepo = verified.Value;
        }

        var initial = ReleaseVersionRules.Parse(request.InitialVersion ?? ReleaseVersionRules.DefaultInitialVersion, "initialVersion");
        if (initial.IsFailure)
        {
            return Result.Failure<RepositoryStart>(initial.Error!);
        }

        // FR-86: a repository that already has code is built on: "Never released", based on its default branch head.
        var head = appRepo is { HasCode: true } ? appRepo.HeadSha : null;
        return Result.Success(new RepositoryStart(initial.Value, null, null, null, head, appRepo?.DefaultBranch, appRepo, LocalCopy: false, Imported: false));
    }

    /// <summary>FR-73: the import's versions, checked against the repository's tags.</summary>
    private async Task<Result<RepositoryStart>> ImportStartAsync(CreateProjectRequest request, Project project, GitProvider gitProvider, Guid ownerId, CancellationToken cancellationToken)
    {
        var git = request.Git;
        var parsed = ImportRules.ParseVersions(request.Import!.ReleasedTag, request.Import.DevelopmentVersion, project.TagPrefix);
        if (parsed.IsFailure)
        {
            return Result.Failure<RepositoryStart>(parsed.Error!);
        }

        var versions = parsed.Value;

        // The tags the checks are about: the released one and the development version's (exact lookups on GitHub).
        string[] wanted = [.. new[] { versions.ReleasedTag, ReleaseVersionRules.Tag(project.TagPrefix, versions.DevelopmentVersion) }.OfType<string>()];
        var repository = gitProvider switch
        {
            GitProvider.GithubApp => await AppRepositoryAsync(git, ownerId, wanted, cancellationToken),
            GitProvider.GithubPat => await PatRepositoryAsync(git, wanted, cancellationToken),
            _ => await LocalRepositoryAsync(git, project.Id, cancellationToken),
        };
        if (repository.IsFailure)
        {
            return Result.Failure<RepositoryStart>(repository.Error!);
        }

        var found = repository.Value;
        var checkedTags = ImportRules.CheckAgainstRepository(versions, [.. found.Tags.Select(t => t.Tag)], project.TagPrefix);
        if (checkedTags.IsFailure)
        {
            if (found.LocalCopy)
            {
                workspaces.TryDelete(project.Id);
            }

            return Result.Failure<RepositoryStart>(checkedTags.Error!);
        }

        var releasedCommit = versions.ReleasedTag is { } tag ? found.Tags.First(t => t.Tag == tag).Commit : null;
        return Result.Success(new RepositoryStart(
            versions.DevelopmentVersion,
            versions.ReleasedTag,
            versions.ReleasedVersion,
            releasedCommit,
            found.HeadCommit,
            found.DefaultBranch,
            found.AppRepo,
            found.LocalCopy,
            Imported: true));
    }

    /// <summary>A repository to import: its tags (with their commits), default branch head and branch.</summary>
    private sealed record FoundRepository(IReadOnlyList<ReleaseTagRef> Tags, string? HeadCommit, string? DefaultBranch, VerifiedAppRepo? AppRepo, bool LocalCopy);

    /// <summary>FR-73 (GitHub App): the picked repository, verified for the caller (<c>GITHUB_APP_NOT_INSTALLED</c>), and which of <paramref name="wanted"/> exist.</summary>
    private async Task<Result<FoundRepository>> AppRepositoryAsync(CreateProjectGitRequest git, Guid ownerId, IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        var verified = await gitHubRepos.VerifyProjectRepoAsync(ownerId, git.InstallationId!.Value, git.RepoFullName!.Trim(), cancellationToken);
        if (verified.IsFailure)
        {
            return Result.Failure<FoundRepository>(verified.Error!);
        }

        var repo = verified.Value;
        var slash = repo.FullName.IndexOf('/', StringComparison.Ordinal);
        var tags = await gitHubRepos.FindTagsAsync(ownerId, repo.FullName[..slash], repo.FullName[(slash + 1)..], wanted, cancellationToken);
        if (tags.IsFailure)
        {
            return Result.Failure<FoundRepository>(tags.Error!);
        }

        return Result.Success(new FoundRepository(
            [.. tags.Value.Select(t => new ReleaseTagRef(t.Tag, t.Commit))],
            repo.HeadSha,
            repo.DefaultBranch,
            repo,
            LocalCopy: false));
    }

    /// <summary>FR-73 (PAT, FR-83): the repository's default branch, head and tags through the REST API with the token.</summary>
    private async Task<Result<FoundRepository>> PatRepositoryAsync(CreateProjectGitRequest git, IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        if (!ProjectSettingsRules.TryParseGitHubRepo(ProjectSettingsRules.NormalizeRepoUrl(GitProvider.GithubPat, git.RepoUrl), out var repo))
        {
            return Result.Failure<FoundRepository>(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = ["git.repoUrl: Use https://github.com/owner/repo."] });
        }

        var token = git.Token ?? string.Empty;
        try
        {
            var repository = await gitHubApi.FindRepositoryAsync(token, repo.Owner, repo.Name, cancellationToken);
            if (repository is null)
            {
                return Result.Failure<FoundRepository>(new DomainError(ErrorCodes.NotFound, "The repository was not found, or the token can't access it."));
            }

            var root = await gitHubApi.ReadBranchRootAsync(token, repository.Owner, repository.Name, repository.DefaultBranch, cancellationToken);
            var tags = new List<ReleaseTagRef>();
            foreach (var tag in root is null ? [] : wanted)
            {
                if (await gitHubApi.FindTagCommitAsync(token, repository.Owner, repository.Name, tag, cancellationToken) is { } sha)
                {
                    tags.Add(new ReleaseTagRef(tag, sha));
                }
            }

            return Result.Success(new FoundRepository(
                tags,
                root?.HeadSha,
                repository.DefaultBranch,
                null,
                LocalCopy: false));
        }
        catch (GitHubApiException ex)
        {
            LogImportLookupFailed(logger, ex.Status, ex.Kind);
            return Result.Failure<FoundRepository>(ex.Kind is GitHubFailureKind.Unauthorized or GitHubFailureKind.Forbidden
                ? new DomainError(ErrorCodes.Validation, "GitHub refused the token.") { Details = [$"git.token: {ex.Message}"] }
                : new DomainError(ErrorCodes.UpstreamError, $"GitHub failed or couldn't be reached. {ex.Message}"));
        }
    }

    /// <summary>
    /// FR-73, §8 Testing (Local): the directory under <c>Git:Local:ImportRoot</c> (not set → <c>VALIDATION</c>) is copied
    /// into the new project's working copy with its history and tags; its tags are then those of the copy.
    /// </summary>
    private async Task<Result<FoundRepository>> LocalRepositoryAsync(CreateProjectGitRequest git, Guid projectId, CancellationToken cancellationToken)
    {
        var source = ImportRules.ResolveLocalSource(localGit.Value.ImportRoot, git.LocalSourcePath);
        if (source.IsFailure)
        {
            return Result.Failure<FoundRepository>(source.Error!);
        }

        try
        {
            var imported = await workingCopies.ImportLocalAsync(projectId, source.Value, cancellationToken);
            return Result.Success(new FoundRepository(imported.Tags, imported.HeadSha, imported.DefaultBranch, null, LocalCopy: true));
        }
        catch (Exception ex) when (ex is WorkingCopyException or GitCommandException or IOException or UnauthorizedAccessException)
        {
            LogLocalImportFailed(logger, ex.GetType().Name);
            workspaces.TryDelete(projectId);
            const string message = "The directory isn't a Git repository with a commit on its default branch, or it couldn't be copied.";
            return Result.Failure<FoundRepository>(new DomainError(ErrorCodes.Validation, message) { Details = [$"git.localSourcePath: {message}"] });
        }
    }

    private async Task<Result<Project>> SaveAsync(
        CreateProjectRequest request, Project project, RepositoryStart start, GitProvider gitProvider, Guid ownerId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var git = request.Git;
        var appRepo = start.AppRepo;
        project.Slug = await UniqueSlugAsync(project.Name, project.Id, cancellationToken);

        var versions = new List<ReleaseVersion>();
        if (start.ReleasedVersion is { } released)
        {
            // FR-73: the baseline AIDE never developed: its tag and commit, no artifacts.
            versions.Add(new ReleaseVersion
            {
                ProjectId = project.Id,
                Version = released.ToString(),
                Status = ReleaseVersionStatus.Imported,
                Tag = start.ReleasedTag,
                TagPrefix = project.TagPrefix,
                MergeCommitSha = start.ReleasedCommit,
                Request = ImportedRequest,
                StartedBy = ownerId,
                CreatedAt = now.AddTicks(-10),
                ReleasedAt = now.AddTicks(-10),
            });
        }

        var version = new ReleaseVersion
        {
            ProjectId = project.Id,
            Version = start.DevelopmentVersion.ToString(),
            Status = ReleaseVersionStatus.InDevelopment,
            TagPrefix = project.TagPrefix,
            Request = project.Idea,
            StartedBy = ownerId,
            CreatedAt = now,

            // FR-73: an import builds on the released tag's commit (or, "Never released", the default branch head); FR-86:
            // so does a scratch project on a repository with code. The Repository Analyst reads the repo at this commit.
            BaseTag = start.ReleasedTag,
            BaseCommitSha = start.ReleasedCommit ?? start.HeadCommit,
        };
        versions.Add(version);
        project.CurrentReleaseVersionId = version.Id;
        ReleaseVersionScope.Refresh(project, versions);

        var provider = llm.DefaultForNewProject;
        if (provider != llm.ConfiguredDefault)
        {
            LogFakeDefault(logger, llm.ConfiguredDefault, provider);
        }

        db.Projects.Add(project);
        db.ReleaseVersions.AddRange(versions);
        db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = ownerId, Role = ProjectRole.Owner, CreatedAt = now });
        db.ProjectGitSettings.Add(new ProjectGitSettings
        {
            ProjectId = project.Id,
            Provider = gitProvider,
            RepoUrl = ProjectSettingsRules.NormalizeRepoUrl(gitProvider, git.RepoUrl),
            DefaultBranch = DefaultBranchFor(git.DefaultBranch!.Trim(), start.DefaultBranch), // validated above
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
            new
            {
                projectId = project.Id,
                name = project.Name,
                actor,
                source = request.Source,
                repoFullName = appRepo?.FullName,
                repoHasCode = appRepo?.HasCode,
                importedTag = start.Imported ? start.ReleasedTag : null,
                developmentVersion = version.Version,
            },
            actor,
            now);

        // FR-70: a version with existing code starts with the Repository Analyst.
        if (version.BaseCommitSha is not null)
        {
            await jobs.EnqueueAsync(JobTypes.RepositoryAnalysis, new RepositoryAnalysisJobPayload(project.Id, version.Id), project.Id, cancellationToken, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        LogCreated(logger, project.Id, provider, gitProvider, request.Source);
        return Result.Success(project);
    }

    /// <summary>
    /// The stored default branch: for a picked GitHub repository (or an imported one) the wizard's default (<c>main</c>)
    /// gives way to the repository's own default branch; an explicit other branch is kept.
    /// </summary>
    private static string DefaultBranchFor(string requested, string? repositoryDefault) =>
        repositoryDefault is not null && requested == ProjectSettingsRules.DefaultBranch ? repositoryDefault : requested;

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} created (LLM provider {LlmProvider}, Git provider {GitProvider}, source {Source})")]
    private static partial void LogCreated(ILogger logger, Guid projectId, LlmProvider llmProvider, GitProvider gitProvider, ProjectSource source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Default LLM provider {ConfiguredProvider} has no API key configured; the new project uses {Provider}")]
    private static partial void LogFakeDefault(ILogger logger, LlmProvider configuredProvider, LlmProvider provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the repository to import failed (HTTP {Status}, {Kind})")]
    private static partial void LogImportLookupFailed(ILogger logger, int? status, GitHubFailureKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The local repository could not be imported ({ExceptionType})")]
    private static partial void LogLocalImportFailed(ILogger logger, string exceptionType);
}
