using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Git;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-9, FR-10: persisted project settings (§6 <c>/api/projects/:id/settings</c>), Owner only. Secrets (Git token,
/// LLM API key, webhook signing keys and Slack/Teams webhook URLs) are encrypted at rest with <see cref="ISecretProtector"/>, only reported as
/// set / not set, and never written to logs or event payloads.
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
            .WithSummary("Update the Git settings (Owner). A GitHub provider needs a https://github.com/<owner>/<repo> URL.")
            .Produces<GitSettingsDto>()
            .ProducesValidationProblem();

        group.MapPatch("/llm", UpdateLlmSettings)
            .WithName("UpdateLlmSettings")
            .WithSummary("Update the LLM provider, models and optional project API key (Owner).")
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
        return TypedResults.Ok(new ProjectSettingsDto(
            ProjectMapping.ToDto(git),
            ProjectMapping.ToDto(llm, catalog),
            ProjectMapping.ToDto(notifications)));
    }

    private static async Task<Results<Ok<GitSettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateGitSettings(
        Guid id,
        UpdateGitSettingsRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        ISecretProtector secrets,
        PullRequestJobs pullRequests,
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
        var validation = ProjectSettingsRules.ValidateGit(provider, repoUrl, branch, request.Token, request.ClearToken);
        if (validation.IsFailure)
        {
            return ApiProblems.Validation(validation.Error!);
        }

        var changes = new List<string>();
        var previousProvider = settings.Provider;
        var normalizedUrl = ProjectSettingsRules.NormalizeRepoUrl(provider, repoUrl);
        if (provider != settings.Provider)
        {
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
            settings.PullRequestNumber = null;
            settings.PullRequestUrl = null;
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

        if (changes.Count > 0 && await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) is { } project)
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

        await SaveChangesAsync(db, id, "git", changes, currentUser, time, loggers, cancellationToken);
        return TypedResults.Ok(ProjectMapping.ToDto(settings));
    }

    private static async Task<Results<Ok<LlmSettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateLlmSettings(
        Guid id,
        UpdateLlmSettingsRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        ISecretProtector secrets,
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

        var settings = await LlmAsync(db, id, catalog, cancellationToken);
        var provider = request.Provider ?? settings.Provider;
        var providerChanged = provider != settings.Provider;

        // A new provider starts from its configured defaults unless the request sets models explicitly, and a project
        // key belongs to the provider it was saved for, so it is dropped unless a new one is sent.
        var defaultModel = request.DefaultModel?.Trim() ?? (providerChanged ? catalog.DefaultModel(provider) : settings.DefaultModel);
        var sentModels = request.ModelsByAgent is { } dto ? ProjectMapping.FromAgentModels(dto) : null;
        var apiKeySent = request.ApiKey is not null;
        var apiKeyValid = apiKeySent && !string.IsNullOrWhiteSpace(request.ApiKey) && !request.ClearApiKey;
        var keepsKey = !request.ClearApiKey && !providerChanged && settings.ApiKeyEncrypted is not null;
        var validation = ProjectSettingsRules.ValidateLlm(
            provider,
            defaultModel,
            sentModels?.ToDictionary(m => m.FieldName, m => m.Model),
            apiKeySent,
            apiKeySent && string.IsNullOrWhiteSpace(request.ApiKey),
            request.ClearApiKey,
            catalog.HasSystemKey(provider),
            hasProjectKeyAfter: apiKeyValid || keepsKey,
            apiKey: request.ApiKey);
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

        if (apiKeyValid)
        {
            settings.ApiKeyEncrypted = secrets.Protect(request.ApiKey!);
            changes.Add("apiKey");
        }
        else if (!keepsKey && settings.ApiKeyEncrypted is not null)
        {
            settings.ApiKeyEncrypted = null;
            changes.Add("apiKey");
        }

        await SaveChangesAsync(db, id, "llm", changes, currentUser, time, loggers, cancellationToken);
        return TypedResults.Ok(ProjectMapping.ToDto(settings, catalog));
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
            .TestConnectionAsync(new GitConnectionSettings(id, settings.RepoUrl, settings.DefaultBranch), token, cancellationToken);
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
            var provider = catalog.DefaultForNewProject;
            settings = new ProjectLlmSettings
            {
                ProjectId = id,
                Provider = provider,
                DefaultModel = catalog.DefaultModel(provider),
                ModelsByAgent = new Dictionary<string, string>(catalog.DefaultAgentModels(provider)),
            };
            db.ProjectLlmSettings.Add(settings);
        }

        return settings;
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
