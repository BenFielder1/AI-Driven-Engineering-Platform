using Tevrin.Api.Logging;
using Tevrin.Domain.Common;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Infrastructure.Bitbucket;
using Tevrin.Infrastructure.GitHub;
using Tevrin.Infrastructure.GitLab;
using Tevrin.Infrastructure.Persistence.Conversion;

namespace Tevrin.Api.Jobs;

public static class TevrinJobServiceCollectionExtensions
{
    /// <summary>
    /// The job queue (outbox enqueuer) and the background worker (§4.9) from the engine (FR-264), with Tevrin's settings,
    /// payload JSON, trace source and log scope keys, and the Git host rate-limit deferrals (D-0.7.0-20).
    /// </summary>
    public static IServiceCollection AddTevrinJobs(this IServiceCollection services)
    {
        services.AddOptions<JobOptions>()
            .BindConfiguration(JobOptions.SectionName)
            .Validate(o => o.Concurrency >= 1, "Jobs:Concurrency must be at least 1.")
            .Validate(o => o.PollInterval > TimeSpan.Zero, "Jobs:PollInterval must be positive.")
            .Validate(o => o.LeaseDuration > TimeSpan.Zero, "Jobs:LeaseDuration must be positive.")
            .ValidateOnStart();
        services.AddSingleton<IJobPayloadSerializer>(TevrinJobPayloadSerializer.Instance);
        services.AddSingleton(new JobTelemetry(TevrinTelemetry.Source)
        {
            JobIdTag = TevrinTelemetry.JobIdTag,
            JobTypeTag = TevrinTelemetry.JobTypeTag,
            JobAttemptTag = TevrinTelemetry.JobAttemptTag,
            ProjectIdTag = TevrinTelemetry.ProjectIdTag,
            JobOutcomeTag = TevrinTelemetry.JobOutcomeTag,
            ErrorCodeTag = TevrinTelemetry.ErrorCodeTag,
            ProjectIdScopeKey = ProjectLogScope.ProjectIdKey,
        });
        services.AddSingleton<IJobExceptionClassifier, GitHostRateLimitClassifier>();
        services.AddTevrinJobQueue();
        return services;
    }
}

/// <summary>Job payloads as <c>jsonb</c>, in the same JSON as Tevrin's typed columns (<see cref="TevrinJson"/>).</summary>
public sealed class TevrinJobPayloadSerializer : IJobPayloadSerializer
{
    public static TevrinJobPayloadSerializer Instance { get; } = new();

    public string Serialize(object payload) => TevrinJson.Serialize(payload);
}

/// <summary>
/// §4.14, FR-136, NFR-REL: a Git host's rate limit isn't a job failure. GitHub's exhausted limit, GitLab's rate limiting and
/// Bitbucket's 429 (or the connection's 900-an-hour budget) re-queue the job for the reset without counting the attempt.
/// </summary>
public sealed partial class GitHostRateLimitClassifier : IJobExceptionClassifier
{
    public JobDeferralHint? Classify(Exception exception) => exception switch
    {
        GitHubRateLimitedException ex => new JobDeferralHint(
            ex.ResetAt,
            ErrorCodes.GitHubRateLimited,
            until => $"{ErrorCodes.GitHubRateLimited}: GitHub's rate limit is exhausted; the job waits until {until:u}.",
            LogRateLimited),
        GitLabRateLimitedException ex => new JobDeferralHint(
            ex.ResetAt,
            ErrorCodes.GitLabRateLimited,
            until => $"{ErrorCodes.GitLabRateLimited}: GitLab is rate limiting requests; the job waits until {until:u}.",
            (logger, until) => LogHostRateLimited(logger, "GitLab", until)),
        BitbucketRateLimitedException ex => new JobDeferralHint(
            ex.ResetAt,
            ErrorCodes.BitbucketRateLimited,
            until => $"{ErrorCodes.BitbucketRateLimited}: Bitbucket is rate limiting requests; the job waits until {until:u}.",
            (logger, until) => LogHostRateLimited(logger, "Bitbucket", until)),
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Job stopped by GitHub's rate limit; deferred until {RunAfter} without counting the attempt")]
    private static partial void LogRateLimited(ILogger logger, DateTimeOffset runAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job stopped by {Host}'s rate limit; deferred until {RunAfter} without counting the attempt")]
    private static partial void LogHostRateLimited(ILogger logger, string host, DateTimeOffset runAfter);
}
