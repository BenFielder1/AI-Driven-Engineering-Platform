using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Git;
using Aide.Api.Features.GitHub;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-9, FR-10: persisted project settings (§6 <c>/api/projects/:id/settings</c>), Owner only (the LLM section alone is
/// readable by members, FR-79). Secrets (Git token, webhook signing keys and Slack/Teams webhook URLs) are encrypted at
/// rest with <see cref="ISecretProtector"/>, only reported as set / not set, and never written to logs or event payloads.
/// LLM keys live in the binding Owner's connections (FR-78, FR-79); a project only references one.
/// </summary>
public static partial class ProjectSettingsEndpoints
{
    public static IEndpointRouteBuilder MapProjectSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/settings")
            .WithTags("Project settings")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", GetProjectSettings)
            .WithName("GetProjectSettings")
            .WithSummary("Git, LLM and notification settings with secrets as set / not set flags (Owner).")
            .Produces<ProjectSettingsDto>();

        group.MapPatch("/git", UpdateGitSettings)
            .WithName("UpdateGitSettings")
            .WithSummary("Update the Git settings (Owner). A GitHub provider needs a https://github.com/<owner>/<repo> URL. Also sets the release settings: mergeStrategy (FR-65: aide_merges or manual) and tagPrefix (§4.10, FR-64: ^[A-Za-z0-9._/-]{0,20}$, else 400 VALIDATION; 409 CONFLICT while the version in development is awaiting_merge or a release attempt is in progress). FR-136: connectionId re-binds a GitLab project to the caller's own connection after AIDE checks Developer access on the repository; the response shows the host instance, the repo and the binding (\"connected by <name>\"). A project can't move onto gitlab or bitbucket here (400 VALIDATION).")
            .Produces<GitSettingsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status400BadRequest, "VALIDATION: invalid Git settings or tag prefix, a provider change onto gitlab/bitbucket, or connectionId on a project that isn't on GitLab or for another instance; GIT_HOST_NOT_ALLOWED."),
                (StatusCodes.Status403Forbidden, "FORBIDDEN (not an Owner, or the connection lacks Developer access to the repository); CREDENTIAL_NOT_OWNER (another user's connection)"),
                (StatusCodes.Status409Conflict, "CONFLICT: the tag prefix can't change while a release is in progress or awaiting its merge, or a concurrent change; GIT_NOT_CONNECTED (the connection is invalid or its instance disabled)."),
                (StatusCodes.Status429TooManyRequests, "UPSTREAM_RATE_LIMITED (GitLab; Retry-After when sent)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (GitLab failed or couldn't be reached)"));

        group.MapPost("/git/switch-to-app", SwitchGitToApp)
            .WithName("SwitchGitToApp")
            .WithSummary("FR-83: move a github_pat project onto the AIDE GitHub App (Owner): stores the installation and repository ids and drops the PAT. The caller's own GitHub connection must be able to push to the repo (403 FORBIDDEN otherwise; 409 GITHUB_NOT_CONNECTED without a connection). 409 GITHUB_APP_NOT_INSTALLED (extension installUrl) when the App can't access the repo.")
            .Produces<GitSettingsDto>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithRetryAfterOn429()
            .WithResponseDescriptions(
                (StatusCodes.Status403Forbidden, "FORBIDDEN (not an Owner, or the caller's GitHub account can't push to the repository)"),
                (StatusCodes.Status409Conflict, "GITHUB_APP_NOT_INSTALLED (extension installUrl), GITHUB_NOT_CONNECTED, or CONFLICT when the project isn't on a GitHub PAT"),
                (StatusCodes.Status429TooManyRequests, "GITHUB_RATE_LIMITED (Retry-After: seconds until GitHub's rate-limit reset)"),
                (StatusCodes.Status502BadGateway, "UPSTREAM_ERROR (GitHub failed or couldn't be reached)"));

        group.MapGet("/llm", GetLlmSettings)
            .WithName("GetLlmSettings")
            .WithSummary("The LLM settings and binding (\"connected by <name>\", no secret) for any member, with canBind for the caller (FR-79).")
            .Produces<LlmSettingsDto>();

        group.MapPatch("/llm", UpdateLlmSettings)
            .WithName("UpdateLlmSettings")
            .WithSummary("Update the LLM provider and models, and bind one of the caller's own connections (credentialId; null unbinds) (Owner). 403 CREDENTIAL_NOT_OWNER for another user's connection, 404 NOT_FOUND for an unknown one, 400 LLM_PROVIDER_UNAVAILABLE when the provider (sent or kept) is disabled on this server (FR-157).")
            .Produces<LlmSettingsDto>()
            .ProducesValidationProblem();

        group.MapPatch("/notifications", UpdateNotificationSettings)
            .WithName("UpdateNotificationSettings")
            .WithSummary("Replace the notification channels (Owner). Slack/Teams webhook URLs are secrets (sent in secret, returned masked); webhook channels need an HMAC signing secret. A channel sent with its id and no secret keeps its saved secret.")
            .Produces<NotificationSettingsDto>()
            .ProducesValidationProblem();

        group.MapPost("/git/test", TestGitConnection)
            .WithName("TestGitConnection")
            .WithSummary("Test the saved Git settings: repo reachable, writable, default branch exists (Owner). Never changes settings.")
            .Produces<GitTestResultDto>();

        return app;
    }

    private static async Task<Results<Ok<ProjectSettingsDto>, ProblemHttpResult>> GetProjectSettings(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        ILlmProviderCatalog catalog,
        IGitHubAppStore apps,
        GitHubRepoService gitHubRepos,
        IGitLabInstances gitLabInstances,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var git = await GitAsync(db, id, cancellationToken);
        var llm = await LlmAsync(db, id, catalog, cancellationToken);
        var notifications = await NotificationsAsync(db, id, cancellationToken);
        // FR-83: a PAT project whose repository the App is installed on can switch to the App (Owners; this page is Owner-only).
        bool? appInstalled = null;
        if (git.Provider == GitProvider.GithubPat && ProjectSettingsRules.TryParseGitHubRepo(git.RepoUrl, out var patRepo)
            && await apps.GetAsync(cancellationToken) is { } app)
        {
            appInstalled = await gitHubRepos.IsAppInstalledAsync(app, patRepo.Owner, patRepo.Name, cancellationToken);
        }

        return TypedResults.Ok(new ProjectSettingsDto(
            await GitDtoAsync(db, git, gitLabInstances, grant.Value.ProjectRole, appInstalled, appInstalled == true, cancellationToken),
            ProjectMapping.ToDto(llm, catalog, await LlmBindings.LoadAsync(db, llm, cancellationToken), LlmBindings.CanBind(grant.Value.ProjectRole)),
            ProjectMapping.ToDto(notifications)));
    }

    private static async Task<Results<Ok<LlmSettingsDto>, ProblemHttpResult>> GetLlmSettings(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        ILlmProviderCatalog catalog,
        CancellationToken cancellationToken)
    {
        // FR-79: every member sees which connection the project uses ("connected by <name>"), never the key.
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var llm = await db.ProjectLlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == id, cancellationToken)
            ?? DefaultLlm(id, catalog);
        return TypedResults.Ok(ProjectMapping.ToDto(llm, catalog, await LlmBindings.LoadAsync(db, llm, cancellationToken), LlmBindings.CanBind(grant.Value.ProjectRole)));
    }

    private static async Task<Results<Ok<GitSettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateGitSettings(
        Guid id,
        UpdateGitSettingsRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        ISecretProtector secrets,
        PullRequestJobs pullRequests,
        GitLab.GitLabProjectRepos gitLabRepos,
        IGitLabInstances gitLabInstances,
        HttpContext http,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var settings = await GitAsync(db, id, cancellationToken);
        var provider = request.Provider ?? settings.Provider;
        var repoUrl = request.RepoUrl ?? settings.RepoUrl;
        var branch = request.DefaultBranch?.Trim() ?? settings.DefaultBranch;
        var hosted = ProjectSettingsRules.IsGitHost(settings.Provider) && provider == settings.Provider;
        var validation = Validation.Combine(
            ProjectSettingsRules.ValidateProviderChange(settings.Provider, provider),
            ProjectSettingsRules.ValidateGit(provider, repoUrl, branch, request.Token, request.ClearToken),
            ProjectSettingsRules.ValidateTagPrefix(request.TagPrefix),
            ValidateHostedGit(request, settings, hosted));
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        // FR-136: re-bind to the caller's own connection, after AIDE checks it has Developer access to the repository.
        GitLab.VerifiedGitLabRepo? rebound = null;
        if (request.ConnectionId is { } connectionId)
        {
            var verified = await gitLabRepos.VerifyAsync(
                currentUser.RequiredUserId, grant.Value.ProjectRole, settings.InstanceId!.Value, connectionId, settings.ExternalRepoId!, string.Empty, cancellationToken);
            if (verified.IsFailure)
            {
                ResponseDocs.SetRetryAfter(http, verified.Error!.RetryAfter);
                return verified.Error.Code == ErrorCodes.Validation ? ApiProblems.Validation(verified.Error) : ApiProblems.FromError(verified.Error);
            }

            rebound = verified.Value;
        }

        var changes = new List<string>();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return ApiProblems.NotFound("The project was not found.");
        }

        // FR-65: the merge strategy and §4.10 tag prefix live on the project (xmin-guarded).
        if (request.MergeStrategy is { } mergeStrategy && mergeStrategy != project.MergeStrategy)
        {
            project.MergeStrategy = mergeStrategy;
            changes.Add("mergeStrategy");
        }

        if (request.TagPrefix is { } tagPrefix && tagPrefix != project.TagPrefix)
        {
            var development = await db.CurrentReleaseVersionAsync(id, cancellationToken);
            var developmentStatus = development?.Status is ReleaseVersionStatus.InDevelopment or ReleaseVersionStatus.AwaitingMerge
                ? development.Status
                : (ReleaseVersionStatus?)null;
            var releaseInProgress = await Release.ReleaseRecords.JobActiveAsync(db, id, cancellationToken)
                || await db.Releases.AnyAsync(r => r.ProjectId == id && r.Status == ReleaseStatus.InProgress, cancellationToken);
            var allowed = ProjectSettingsRules.CanChangeTagPrefix(developmentStatus, releaseInProgress);
            if (allowed.IsFailure)
            {
                return ApiProblems.FromError(allowed.Error!);
            }

            project.TagPrefix = tagPrefix;
            if (development is not null && developmentStatus == ReleaseVersionStatus.InDevelopment)
            {
                development.TagPrefix = tagPrefix;
            }

            changes.Add("tagPrefix");
        }
        var previousProvider = settings.Provider;
        // FR-136: a GitLab project's web URL is the one read from GitLab when it was bound; it isn't edited here.
        var normalizedUrl = hosted ? settings.RepoUrl : ProjectSettingsRules.NormalizeRepoUrl(provider, repoUrl);
        if (rebound is not null && (settings.GitConnectionId != rebound.ConnectionId || settings.BoundByUserId != rebound.OwnerUserId))
        {
            settings.GitConnectionId = rebound.ConnectionId;
            settings.BoundByUserId = rebound.OwnerUserId;
            changes.Add("gitConnectionId");
        }

        if (provider != settings.Provider)
        {
            if (settings.Provider == GitProvider.GithubApp)
            {
                // FR-82: the App's installation and repository belong to the github_app mode only.
                settings.InstallationId = null;
                settings.RepoId = null;
                settings.RepoFullName = null;
            }

            if (ProjectSettingsRules.IsGitHost(settings.Provider))
            {
                // FR-136: the instance, repo and binding belong to the gitlab/bitbucket mode only.
                settings.InstanceId = null;
                settings.GitConnectionId = null;
                settings.BoundByUserId = null;
                settings.ExternalRepoId = null;
                settings.RepoFullName = null;
            }

            settings.Provider = provider;
            changes.Add("provider");
        }

        if (normalizedUrl != settings.RepoUrl)
        {
            settings.RepoUrl = normalizedUrl;
            changes.Add("repoUrl");
        }

        if (branch != settings.DefaultBranch)
        {
            settings.DefaultBranch = branch;
            changes.Add("defaultBranch");
        }

        var now = time.GetUtcNow();
        var forget = changes.Contains("provider") || changes.Contains("repoUrl");
        if (forget)
        {
            // The recorded PR belongs to the old repository (FR-36): the PR summary, GET /git/pull-request and the pull
            // request job all honour PULL_REQUEST_FORGOTTEN, so a new PR is opened in the new repository.
            if (await db.CurrentReleaseVersionAsync(id, cancellationToken) is { } version)
            {
                version.PullRequestNumber = null;
                version.PullRequestUrl = null;
            }

            if (await PullRequestQueries.LatestOpenedAsync(db, id, cancellationToken) is not null)
            {
                db.AddEvent(EventTypes.PullRequestForgotten, id, new { projectId = id, changes = changes.ToArray() }, currentUser.Actor, now);
            }
        }

        if (request.Token is { } token)
        {
            settings.TokenEncrypted = secrets.Protect(token);
            changes.Add("token");
        }
        else if (!ProjectSettingsRules.KeepsSavedToken(previousProvider, provider, request.ClearToken) && settings.TokenEncrypted is not null)
        {
            settings.TokenEncrypted = null;
            changes.Add("token");
        }

        if (changes.Count > 0)
        {
            // FR-36: with no PR recorded for the current plan (e.g. the job failed for a bad token, or the repository just
            // changed), new settings queue the pull request job again; RequestAsync applies the same rule as the retry
            // endpoint and does nothing otherwise. The project is touched so its xmin guards the forget and the queueing.
            var requeued = await pullRequests.RequestAsync(project, PullRequestJobPayload.SettingsChanged, now, cancellationToken);
            if (forget || requeued.IsSuccess)
            {
                project.UpdatedAt = now;
            }
        }

        if (changes.Contains("mergeStrategy") || changes.Contains("tagPrefix"))
        {
            project.UpdatedAt = now;
        }

        // A concurrent change to the project (xmin) becomes 409 CONFLICT in the exception handler.
        await SaveChangesAsync(db, id, "git", changes, currentUser, time, loggers, cancellationToken);
        return TypedResults.Ok(await GitDtoAsync(db, settings, gitLabInstances, grant.Value.ProjectRole, null, false, cancellationToken));
    }

    /// <summary>
    /// FR-136: <c>connectionId</c> only re-binds a GitLab project (not Bitbucket yet, not GitHub or Local); a GitLab
    /// project's repository URL comes from GitLab and can't be changed here (sending the stored one is accepted).
    /// </summary>
    private static Result ValidateHostedGit(UpdateGitSettingsRequest request, ProjectGitSettings settings, bool hosted)
    {
        var problems = new List<string>();
        if (request.ConnectionId is not null && !(hosted && settings.Provider == GitProvider.Gitlab && settings.InstanceId is not null && settings.ExternalRepoId is not null))
        {
            problems.Add("connectionId: Only used to re-bind a GitLab project's connection.");
        }

        if (hosted && request.RepoUrl is { } sentUrl && !string.Equals(sentUrl.Trim(), settings.RepoUrl, StringComparison.Ordinal))
        {
            problems.Add("repoUrl: A GitLab project's repository can't be changed; create a new project for another repository.");
        }

        return problems.Count == 0
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = problems });
    }

    private static async Task<Results<Ok<LlmSettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateLlmSettings(
        Guid id,
        UpdateLlmSettingsRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        ILlmProviderCatalog catalog,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        // FR-79: the same project row lock as member role changes and removals, so a concurrent demotion either commits
        // first (and is seen below) or waits for this bind (and then clears it). Everything below is read under the lock.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, id, cancellationToken);
        var callerId = grant.Value.UserId;
        var callerRole = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == id && m.UserId == callerId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (grant.Value.SystemRole != SystemRole.Admin && callerRole != ProjectRole.Owner)
        {
            // Demoted or removed while this request waited for the lock.
            return callerRole is null
                ? ApiProblems.NotFound("The project was not found.")
                : ApiProblems.Forbidden("Your project role doesn't allow this action.");
        }

        var settings = await LlmAsync(db, id, catalog, cancellationToken);
        var provider = request.Provider ?? settings.Provider;
        var providerChanged = provider != settings.Provider;

        // FR-157: a provider this deployment disabled can't be chosen or kept (the Owner must choose another one).
        var enabled = LlmProviderRules.IsEnabled(provider, catalog.IsEnabled(provider));
        if (enabled.IsFailure)
        {
            return ApiProblems.FromError(enabled.Error!);
        }
        var current = settings.CredentialId is { } currentId
            ? await db.LlmCredentials.AsNoTracking().Where(c => c.Id == currentId).Select(c => new LlmConnectionRef(c.Id, c.UserId, c.Provider)).FirstOrDefaultAsync(cancellationToken)
            : null;

        // FR-79: the binding after the update. Sending a credentialId binds it (only the caller's own, for the provider);
        // null unbinds; omitting it keeps the binding unless the provider changes (a binding is for one provider).
        LlmConnectionRef? binding;
        if (request.CredentialIdSent && request.CredentialId is { } credentialId)
        {
            // Re-sending the current binding (e.g. the whole form) needs no ownership check: it changes nothing.
            var connection = credentialId == current?.Id
                ? current
                : await db.LlmCredentials.AsNoTracking()
                    .Where(c => c.Id == credentialId)
                    .Select(c => new LlmConnectionRef(c.Id, c.UserId, c.Provider))
                    .FirstOrDefaultAsync(cancellationToken);
            var allowed = connection == current && connection is not null
                ? LlmConnectionRules.CanBind(connection.UserId, ProjectRole.Owner, connection, provider)
                : LlmConnectionRules.CanBind(callerId, callerRole, connection, provider);
            if (allowed.IsFailure)
            {
                return allowed.Error!.Code == ErrorCodes.Validation ? ApiProblems.Validation(allowed.Error) : ApiProblems.FromError(allowed.Error);
            }

            binding = connection;
        }
        else if (request.CredentialIdSent)
        {
            binding = null;
        }
        else
        {
            binding = LlmConnectionRules.KeepsBinding(current?.Provider, provider) ? current : null;
        }

        // A new provider starts from its configured defaults unless the request sets models explicitly.
        var defaultModel = request.DefaultModel?.Trim() ?? (providerChanged ? catalog.DefaultModel(provider) : settings.DefaultModel);
        var sentModels = request.ModelsByAgent is { } dto ? ProjectMapping.FromAgentModels(dto) : null;
        var validation = ProjectSettingsRules.ValidateLlm(
            provider,
            defaultModel,
            sentModels?.ToDictionary(m => m.FieldName, m => m.Model),
            catalog.HasSystemKey(provider),
            hasBindingAfter: binding is not null,
            providerChanged);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var models = sentModels is not null
            ? sentModels.Where(m => m.Model is not null).ToDictionary(m => m.Agent.ToWire(), m => m.Model!.Trim(), StringComparer.Ordinal)
            : providerChanged
                ? new Dictionary<string, string>(catalog.DefaultAgentModels(provider), StringComparer.Ordinal)
                : null;

        var changes = new List<string>();
        if (providerChanged)
        {
            settings.Provider = provider;
            changes.Add("provider");
        }

        if (defaultModel != settings.DefaultModel)
        {
            settings.DefaultModel = defaultModel;
            changes.Add("defaultModel");
        }

        if (models is not null && !SameModels(models, settings.ModelsByAgent))
        {
            settings.ModelsByAgent = models;
            changes.Add("modelsByAgent");
        }

        if (binding?.Id != settings.CredentialId)
        {
            // The binding Owner is the connection's owner (D-V2-5: runs use it on their behalf).
            settings.CredentialId = binding?.Id;
            settings.BoundByUserId = binding?.UserId;
            changes.Add("credentialId");
        }

        try
        {
            await SaveChangesAsync(db, id, "llm", changes, currentUser, time, loggers, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // The connection was deleted between the check and the save (its deletion doesn't take the project lock).
            return ApiProblems.NotFound("The connection was not found.");
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(ProjectMapping.ToDto(settings, catalog, await LlmBindings.LoadAsync(db, settings, cancellationToken), LlmBindings.CanBind(callerRole)));
    }

    private static async Task<Results<Ok<NotificationSettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateNotificationSettings(
        Guid id,
        UpdateNotificationSettingsRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        ISecretProtector secrets,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var settings = await NotificationsAsync(db, id, cancellationToken);
        var existing = settings.Channels.ToDictionary(c => c.Id);
        var inputs = request.Channels ?? [];

        var problems = new List<string>();
        var seen = new HashSet<Guid>();
        var candidates = new List<ChannelCandidate>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            NotificationChannelConfig? previous = null;
            if (input.Id is { } channelId && (!existing.TryGetValue(channelId, out previous) || !seen.Add(channelId)))
            {
                problems.Add($"channels[{i}].id: Unknown or repeated channel id.");
            }

            candidates.Add(new ChannelCandidate(
                input.Type,
                input.Target,
                input.Secret,
                input.ClearSecret,
                previous is null ? null : new SavedChannel(previous.Type, previous.Target, previous.SecretEncrypted is not null)));
        }

        var validation = Validation.Combine(
            problems.Count == 0
                ? Result.Success()
                : Result.Failure(new DomainError(ErrorCodes.Validation, "The notification settings are invalid.") { Details = problems }),
            ProjectSettingsRules.ValidateChannels(candidates));
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var channels = inputs.Select((input, i) =>
        {
            var previous = input.Id is { } channelId ? existing[channelId] : null;
            var plan = ProjectSettingsRules.PlanChannel(candidates[i]);
            var secret = plan.Secret switch
            {
                ChannelSecretChange.Set => secrets.Protect(plan.NewSecret!),
                ChannelSecretChange.Keep => previous?.SecretEncrypted,
                _ => null,
            };
            return new NotificationChannelConfig(
                previous?.Id ?? Guid.CreateVersion7(),
                input.Type,
                plan.StoredTarget,
                secret,
                [.. (input.Events ?? []).Distinct().Select(e => e.ToWire())]);
        }).ToList();

        var unchanged = SameChannels(settings.Channels, channels);
        settings.Channels = channels;
        await SaveChangesAsync(db, id, "notifications", unchanged ? [] : ["channels"], currentUser, time, loggers, cancellationToken);
        return TypedResults.Ok(ProjectMapping.ToDto(settings));
    }

    /// <summary>The working branch recorded on the project's current release version (§4.10).</summary>
    /// <summary>The Git settings DTO with the project branch and the project's release settings (FR-65, §4.10).</summary>
    private static async Task<GitSettingsDto> GitDtoAsync(
        AideDbContext db,
        ProjectGitSettings settings,
        IGitLabInstances gitLabInstances,
        ProjectRole? callerRole,
        bool? appInstalledOnRepo,
        bool canSwitchToApp,
        CancellationToken cancellationToken)
    {
        var release = await db.Projects.AsNoTracking()
            .Where(p => p.Id == settings.ProjectId)
            .Select(p => new { p.MergeStrategy, p.TagPrefix })
            .FirstOrDefaultAsync(cancellationToken);
        var tracked = db.ChangeTracker.Entries<Project>().FirstOrDefault(e => e.Entity.Id == settings.ProjectId)?.Entity;
        var dto = ProjectMapping.ToDto(
            settings,
            await CurrentBranchAsync(db, settings.ProjectId, cancellationToken),
            tracked?.MergeStrategy ?? release?.MergeStrategy ?? MergeStrategy.AideMerges,
            tracked?.TagPrefix ?? release?.TagPrefix ?? ReleaseVersion.DefaultTagPrefix,
            appInstalledOnRepo,
            canSwitchToApp);
        if (!ProjectSettingsRules.IsGitHost(settings.Provider))
        {
            return dto;
        }

        // FR-136: the host instance, the repo and the binding ("connected by <name>"), never a token.
        var instance = settings.InstanceId is { } instanceId && settings.Provider == GitProvider.Gitlab
            ? await gitLabInstances.GetAsync(instanceId, cancellationToken)
            : null;
        GitBindingDto? binding = null;
        if (settings.GitConnectionId is { } connectionId)
        {
            binding = await (
                    from c in db.GitConnections.AsNoTracking()
                    join u in db.Users.AsNoTracking() on c.UserId equals u.Id
                    where c.Id == connectionId
                    select new GitBindingDto(c.Id, c.Login, c.Status, new GitBindingUserDto(u.Id, u.Name)))
                .FirstOrDefaultAsync(cancellationToken);
        }

        return dto with
        {
            Instance = instance is null ? null : new GitHostInstanceRefDto(instance.Id, instance.Name, instance.BaseUrl.AbsoluteUri.TrimEnd('/'), instance.Enabled),
            ExternalRepoId = settings.ExternalRepoId,
            Binding = binding,
            CanRebind = callerRole == ProjectRole.Owner && settings.Provider == GitProvider.Gitlab,
        };
    }

    private static async Task<string?> CurrentBranchAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is { } versionId
            ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.BranchName).FirstOrDefaultAsync(cancellationToken)
            : null;

    private static async Task<Results<Ok<GitSettingsDto>, ProblemHttpResult>> SwitchGitToApp(
        Guid id,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        IGitHubAppStore apps,
        GitHubRepoService gitHubRepos,
        IGitLabInstances gitLabInstances,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var settings = await GitAsync(db, id, cancellationToken);
        if (settings.Provider != GitProvider.GithubPat || !ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return ApiProblems.Of(ErrorCodes.Conflict, "Only a project on a GitHub personal access token can switch to the AIDE GitHub App.");
        }

        if (await apps.GetAsync(cancellationToken) is null)
        {
            return ApiProblems.GitHubAppNotInstalled(null, "No AIDE GitHub App is registered. An Admin registers it under Admin → Integrations → GitHub.");
        }

        // FR-83: the caller must be able to write to the repository with their own GitHub account.
        var verified = await gitHubRepos.VerifySwitchAsync(currentUser.RequiredUserId, repo.Owner, repo.Name, cancellationToken);
        if (verified.IsFailure)
        {
            return await GitHubResults.ProblemAsync(verified.Error!, apps, cancellationToken);
        }

        var found = verified.Value;
        // FR-83: the project now uses installation tokens; the PAT ciphertext is deleted.
        settings.Provider = GitProvider.GithubApp;
        settings.InstallationId = found.InstallationId;
        settings.RepoId = found.RepoId;
        settings.RepoFullName = found.FullName;
        settings.RepoUrl = null;
        settings.TokenEncrypted = null;
        await SaveChangesAsync(db, id, "git", ["provider", "installationId", "repoId", "repoFullName", "token"], currentUser, time, loggers, cancellationToken);
        return TypedResults.Ok(await GitDtoAsync(db, settings, gitLabInstances, grant.Value.ProjectRole, null, false, cancellationToken));
    }

    private static async Task<Results<Ok<GitTestResultDto>, ProblemHttpResult>> TestGitConnection(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        ISecretProtector secrets,
        IGitProviderResolver providers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ChangeSettings, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        // Read-only: nothing here is saved, whatever the outcome (FR-10).
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == id, cancellationToken)
            ?? DefaultGit(id);
        string? token = null;
        if (settings.TokenEncrypted is { } encrypted && !secrets.TryUnprotect(encrypted, out token))
        {
            return TypedResults.Ok(new GitTestResultDto(false, false, false, false, "The saved token can't be decrypted. Save it again."));
        }

        var result = await providers.Resolve(settings.Provider)
            .TestConnectionAsync(GitConnectionSettings.From(settings), token, cancellationToken);
        return TypedResults.Ok(new GitTestResultDto(result.Ok, result.Reachable, result.Writable, result.DefaultBranchExists, result.Reason));
    }

    private static async Task SaveChangesAsync(
        AideDbContext db,
        Guid projectId,
        string section,
        List<string> changes,
        ICurrentUser currentUser,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        // Field names only: secret values never reach events or logs.
        db.AddEvent(EventTypes.SettingsUpdated, projectId, new { projectId, section, changes }, currentUser.Actor, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        var logger = loggers.CreateLogger(typeof(ProjectSettingsEndpoints));
        LogSettingsUpdated(logger, projectId, section, changes);
    }

    // Every project gets its three settings rows at creation; these fallbacks only cover rows that are missing anyway.
    private static async Task<ProjectGitSettings> GitAsync(AideDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectGitSettings.FirstOrDefaultAsync(s => s.ProjectId == id, cancellationToken);
        if (settings is null)
        {
            settings = DefaultGit(id);
            db.ProjectGitSettings.Add(settings);
        }

        return settings;
    }

    private static ProjectGitSettings DefaultGit(Guid id) =>
        new() { ProjectId = id, Provider = GitProvider.Local, DefaultBranch = ProjectSettingsRules.DefaultBranch };

    private static async Task<ProjectLlmSettings> LlmAsync(AideDbContext db, Guid id, ILlmProviderCatalog catalog, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectLlmSettings.FirstOrDefaultAsync(s => s.ProjectId == id, cancellationToken);
        if (settings is null)
        {
            settings = DefaultLlm(id, catalog);
            db.ProjectLlmSettings.Add(settings);
        }

        return settings;
    }

    private static ProjectLlmSettings DefaultLlm(Guid id, ILlmProviderCatalog catalog)
    {
        var provider = catalog.FallbackProvider;
        return new ProjectLlmSettings
        {
            ProjectId = id,
            Provider = provider,
            DefaultModel = catalog.DefaultModel(provider),
            ModelsByAgent = new Dictionary<string, string>(catalog.DefaultAgentModels(provider)),
        };
    }

    private static async Task<ProjectNotificationSettings> NotificationsAsync(AideDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectNotificationSettings.FirstOrDefaultAsync(s => s.ProjectId == id, cancellationToken);
        if (settings is null)
        {
            settings = new ProjectNotificationSettings { ProjectId = id, Channels = [] };
            db.ProjectNotificationSettings.Add(settings);
        }

        return settings;
    }

    private static bool SameModels(Dictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool SameChannels(IReadOnlyList<NotificationChannelConfig> a, List<NotificationChannelConfig> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id
            && p.First.Type == p.Second.Type
            && p.First.Target == p.Second.Target
            && p.First.SecretEncrypted == p.Second.SecretEncrypted
            && p.First.Events.SequenceEqual(p.Second.Events));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} {Section} settings updated ({Changes})")]
    private static partial void LogSettingsUpdated(ILogger logger, Guid projectId, string section, List<string> changes);
}
