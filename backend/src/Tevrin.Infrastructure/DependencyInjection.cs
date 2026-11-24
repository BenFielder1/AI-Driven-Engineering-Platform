using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tevrin.Domain.Entities;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Infrastructure.Agents;
using Tevrin.Infrastructure.Bitbucket;
using Tevrin.Infrastructure.Configuration;
using Tevrin.Infrastructure.Deployments;
using Tevrin.Infrastructure.Git;
using Tevrin.Infrastructure.GitHub;
using Tevrin.Infrastructure.GitLab;
using Tevrin.Infrastructure.Llm;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Infrastructure.Platforms;
using Tevrin.Infrastructure.RepoTools;
using Tevrin.Infrastructure.Stack;
using Tevrin.Infrastructure.Trackers;

namespace Tevrin.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Configuration key of the PostgreSQL connection string (<c>ConnectionStrings:Tevrin</c>,
    /// env var <c>ConnectionStrings__Tevrin</c>).</summary>
    public const string ConnectionStringName = "Tevrin";

    /// <summary>FR-325: the pre-0.8.0 name, read when <see cref="ConnectionStringName"/> is unset, until 0.9.0
    /// (<see cref="LegacyConfigNames"/>).</summary>
    public const string LegacyConnectionStringName = "Aide";

    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public static IServiceCollection AddTevrinInfrastructure(this IServiceCollection services)
    {
        // The connection string is read when the context is first resolved (not at registration),
        // so tests and build-time OpenAPI generation can supply or omit it.
        services.AddDbContext<TevrinDbContext>((sp, options) =>
        {
            // FR-325: ConnectionStrings:Tevrin, else the old ConnectionStrings:Aide (warned at startup).
            var connectionString = LegacyConfigNames.Get(sp.GetRequiredService<IConfiguration>(), LegacyConfigNames.ConnectionString);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                // Design-time tools (`migrations add`, `migrations bundle` in the image build) need no connection;
                // the bundle receives it via --connection. At runtime a missing connection string is a config error.
                if (!EF.IsDesignTime)
                {
                    throw new InvalidOperationException(
                        $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
                }

                options.UseNpgsql(npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable));
            }
            else
            {
                options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable));
            }

            options.UseSnakeCaseNamingConvention();

            // FR-46: committed project events are pushed live (the API replaces the no-op publisher).
            options.AddInterceptors(sp.GetRequiredService<ProjectEventPublishingInterceptor>());

            // FR-47 outbox: notification-worthy events queue their notification job in the same transaction.
            options.AddInterceptors(sp.GetRequiredService<NotificationOutboxInterceptor>());

            // §4.10: new projects get their first release version; version-scoped rows left unstamped get the current one.
            options.AddInterceptors(sp.GetRequiredService<ReleaseVersionStampingInterceptor>());

            // FR-292, FR-294: the project's pipeline run follows it, and new rows get their run (after the version stamping).
            options.AddInterceptors(sp.GetRequiredService<PipelineRunSyncInterceptor>());

            // §8: a span per stage execution once the save that closes it commits (after the run sync, which closes them).
            options.AddInterceptors(sp.GetRequiredService<StageExecutionTracingInterceptor>());
        });

        // D-0.7.0-20: the engine reaches its tables through the same scoped context (one unit of work).
        services.AddScoped<IEngineDbContext>(sp => sp.GetRequiredService<TevrinDbContext>());
        services.AddScoped<RunLogQueries>();
        services.TryAddSingleton<IProjectEventPublisher, NullProjectEventPublisher>();
        services.TryAddSingleton<ProjectEventPublishingInterceptor>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NotificationOutboxInterceptor>();
        services.TryAddSingleton<ReleaseVersionStampingInterceptor>();
        services.TryAddSingleton<StageExecutionTracingInterceptor>();
        services.TryAddSingleton(sp => new PipelineRunSyncInterceptor(sp.GetService<PipelineRegistryHost>(), sp.GetRequiredService<TimeProvider>()));

        // FR-79: fills last4 of LLM keys migrated from 0.1.0 projects (needs ISecretProtector from AddTevrinDataProtection).
        services.AddHostedService<LlmCredentialLast4Backfill>();

        // FR-321, D-0.7.0-41: runs pinned to the earlier 0.7.0 SDLC definition move to the current one, before the backfill
        // and the job worker (registered later).
        services.AddHostedService<SdlcRunRepinService>();

        // FR-294, D-0.7.0-28: SDLC pipeline runs for 0.6.1 data, at start-up before the job worker (registered later) starts.
        services.AddHostedService<PipelineRunBackfillService>();

        // LLM provider configuration (FR-22): defaults and which providers have a system key.
        services.AddOptions<LlmOptions>()
            .BindConfiguration(LlmOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
        services.AddSingleton<ILlmProviderCatalog, LlmProviderCatalog>();
        services.AddSingleton<ILlmCatalog>(sp => sp.GetRequiredService<ILlmProviderCatalog>());

        // The one LLM abstraction (FR-22, CLAUDE.md rule 11): typed HttpClients for the real providers, an offline Fake,
        // versioned prompt templates (FR-24) and the resolver that picks provider, model and key per project and agent.
        // No client retries: the job worker retries transient failures (FR-25).
        services.AddHttpClient<AnthropicLlmProvider>(ConfigureLlmClient);
        services.AddHttpClient<OpenAiLlmProvider>(ConfigureLlmClient);
        services.AddHttpClient<OllamaLlmProvider>(ConfigureLlmClient);
        services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<AnthropicLlmProvider>());
        services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<OpenAiLlmProvider>());
        services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<OllamaLlmProvider>());
        // Built-in Fake replies for every agent (FR-27–FR-31, FR-38): E2E and the dev seed run offline.
        services.AddSingleton<IFakeAgentResponder, FakeIdeaRefinementResponder>();
        services.AddSingleton<IFakeAgentResponder, FakeSpecGeneratorResponder>();
        services.AddSingleton<IFakeAgentResponder, FakePlanGeneratorResponder>();
        services.AddSingleton<IFakeAgentResponder, FakeCodeGeneratorResponder>();
        services.AddSingleton<IFakeAgentResponder, FakeRepositoryAnalystResponder>();
        services.AddSingleton<IFakeAgentResponder, FakeStackAnalystResponder>();
        services.AddSingleton<IFakeAgentResponder, FakeBuildFixResponder>();
        services.AddSingleton<IFakeAgentResponder>(_ => new FakeCodeReviewResponder(FakeCodeReviewScript.Default));
        // FR-71: the Fake provider's scripted repo tool calls (last registered per agent wins; tests override them).
        foreach (var script in FakeToolScripts.Defaults())
        {
            services.AddSingleton<IFakeToolScript>(script);
        }

        services.AddSingleton<FakeLlmProvider>();
        services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<FakeLlmProvider>());
        services.AddSingleton<IPromptLibrary, EmbeddedPromptLibrary>();

        // Stack catalog (FR-113, FR-122, §4.16): the shipped catalog (embedded), loaded once at startup; tests may replace the source.
        services.TryAddSingleton<IStackCatalogSource>(EmbeddedStackCatalogSource.Shipped);
        services.TryAddSingleton<IStackCatalog, StackCatalogProvider>();
        services.AddHostedService<StackCatalogStartup>();
        services.AddScoped<ILlmResolver, LlmResolver>();
        services.AddScoped<ILlmKeyValidator, LlmKeyValidator>();

        // FR-160: per-model output limits (catalog, Anthropic discovery cached 24 h, ceiling, fallback).
        services.AddHttpClient(IAnthropicModelInfo.HttpClientName, ConfigureLlmClient);
        services.AddSingleton<AnthropicModelInfoCache>();
        services.AddScoped<IAnthropicModelInfo, AnthropicModelInfo>();
        services.AddScoped<ILlmOutputLimits, LlmOutputLimitResolver>();

        // Git providers (FR-10, FR-35) and working copies.
        services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
        services.AddSingleton<IGitCli, GitCli>();
        services.AddSingleton<IWorkingCopies, WorkingCopies>();

        // FR-71, D-V2-9: credential-free, read-only snapshots of the working copy for the agents' repo tools.
        services.AddSingleton<IRepoSnapshotProvider, RepoSnapshotProvider>();
        services.AddOptions<LocalGitOptions>().BindConfiguration(LocalGitOptions.SectionName);
        services.AddOptions<GitHubGitOptions>()
            .BindConfiguration(GitHubGitOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GitHubGitOptions>, GitHubGitOptionsValidator>();
        services.AddHttpClient(GitHubGitProvider.HttpClientName, (sp, client) =>
        {
            var configured = sp.GetRequiredService<IConfiguration>()[GitHubGitProvider.ApiBaseUrlKey];
            var baseUrl = string.IsNullOrWhiteSpace(configured) ? GitHubGitProvider.DefaultApiBaseUrl : configured.Trim();
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", GitHubGitProvider.ApiVersion);
        });
        services.AddScoped<GitHubGitProvider>();
        services.AddScoped<IGitProvider>(sp => sp.GetRequiredService<GitHubGitProvider>());
        services.AddScoped<IGitProvider, LocalGitProvider>();

        // The Tevrin GitHub App (FR-80–FR-83, §4.14): configuration (GitHub:App:*) or the manifest-registered row, the REST
        // API on the fixed GitHub hosts, installation tokens (in-memory cache ≤ 55 min) and the github_app provider.
        services.AddOptions<GitHubAppOptions>()
            .BindConfiguration(GitHubAppOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GitHubAppOptions>, GitHubAppOptionsValidator>();
        services.AddHttpClient(GitHubApi.WebHttpClientName, client =>
        {
            client.BaseAddress = new Uri(GitHubApi.WebBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
        });
        services.AddTransient<GitHubApi>();
        services.AddScoped<IGitHubAppStore, GitHubAppStore>();
        services.AddSingleton<GitHubInstallationTokenCache>();
        services.AddScoped<IGitHubInstallationTokens, GitHubInstallationTokens>();
        services.AddSingleton<GitHubBotIdentities>();
        services.AddSingleton<GitHubAppInfoCache>();
        services.AddScoped<IGitProvider, GitHubAppGitProvider>();
        services.AddScoped<IGitProviderResolver, GitProviderResolver>();

        // GitLab instances, connections and API (FR-130–FR-132, §4.19, §8): one HttpClient per instance from the "gitlab"
        // handler, whose ConnectCallback is the SSRF guard (resolved addresses checked on every connection, no redirects,
        // no proxy). Tokens come from IGitConnectionTokens (refresh before expiry, rotated pair stored atomically).
        services.AddOptions<GitLabOptions>()
            .BindConfiguration(GitLabOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GitLabOptions>, GitLabOptionsValidator>();
        services.TryAddSingleton<IGitHostDns, SystemGitHostDns>();
        services.AddSingleton<GitHostConnector>();
        services.AddHttpClient(GitLabClients.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            })
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<GitHostConnector>().CreateHandler());
        services.AddSingleton<IGitLabClients, GitLabClients>();
        services.AddScoped<IGitLabInstances, GitLabInstances>();
        services.AddSingleton<IGitConnectionTokens, GitConnectionTokens>();

        // FR-136, FR-137: the gitlab provider (the binding Owner's connection, per-command extraHeader for git).
        services.AddScoped<IGitProvider, GitLabGitProvider>();

        // FR-136, FR-137: the bitbucket provider (the binding Owner's connection, its budget, per-command extraHeader for git).
        services.AddScoped<IGitProvider, Bitbucket.BitbucketGitProvider>();

        // Bitbucket Cloud (FR-133–FR-135, §4.19): fixed hosts (no SSRF surface), redirects never followed, the consumer row
        // (git_host_instances, kind bitbucket) with Bitbucket__OAuth__* on top, and the shared per-connection limiter
        // (900 requests an hour, in memory: one API replica).
        services.AddOptions<BitbucketOptions>()
            .BindConfiguration(BitbucketOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BitbucketOptions>, BitbucketOptionsValidator>();
        services.AddHttpClient(BitbucketClients.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddSingleton<BitbucketRateLimiter>();
        services.AddSingleton<IBitbucketClients, BitbucketClients>();
        services.AddScoped<IBitbucketConsumer, BitbucketConsumer>();
        services.AddSingleton<IBitbucketRepoInitializer, BitbucketRepoInitializer>();

        // Deployments (FR-87–FR-91, §4.15, D-V2-10): Vercel's REST API on the fixed host api.vercel.com (§8: never
        // configurable, so no SSRF), the deterministic Fake (Deployments:Provider=Fake) and the env write limiter.
        services.AddOptions<DeploymentOptions>().BindConfiguration(DeploymentOptions.SectionName);
        services.AddHttpClient(VercelApiClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(VercelApiClient.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
        });
        services.AddSingleton<VercelEnvWriteLimiter>();
        // §4.20: one provider per platform (all real since M30; every one Fake under Deployments:Provider=Fake).
        services.AddSingleton<VercelApiClient>();
        services.AddSingleton<FakeVercelClient>();
        services.AddSingleton<VercelDeploymentProvider>();
        services.AddSingleton<GitHubPagesDeploymentProvider>();
        services.AddSingleton<NetlifyDeployRateLimiter>();
        services.AddSingleton<IGitHostRepoLinks, GitHostRepoLinks>();
        services.AddSingleton<NetlifyDeploymentProvider>();
        services.AddSingleton<CloudflareDeploymentProvider>();
        services.AddSingleton<RenderLogRateLimiter>();
        services.AddSingleton<RenderDeploymentProvider>();
        services.AddSingleton<RailwayDeploymentProvider>();
        services.AddSingleton<IDeploymentProviderResolver, DeploymentProviderResolver>();

        // FR-152, FR-153, §4.21: managed database hosts (Fake under Databases:Provider=Fake) and the Supabase BaaS runner.
        services.AddOptions<Databases.DatabaseOptions>().BindConfiguration(Databases.DatabaseOptions.SectionName);
        services.AddSingleton<Databases.SupabaseDatabaseProvider>();
        services.AddSingleton<Databases.RenderPostgresProvider>();
        services.AddSingleton<Databases.RailwayPostgresProvider>();
        services.AddSingleton<Databases.ExternalDatabaseProvider>();
        services.AddSingleton<Databases.IDatabaseProviderResolver, Databases.DatabaseProviderResolver>();
        services.AddSingleton<Databases.NpgsqlMigrationSqlExecutor>();
        services.AddSingleton<Databases.FakeAwareMigrationSqlExecutor>();
        services.AddSingleton<Databases.IMigrationSqlExecutor>(sp => sp.GetRequiredService<Databases.FakeAwareMigrationSqlExecutor>());
        services.AddSingleton<Databases.IDatabaseSchemaReset>(sp => sp.GetRequiredService<Databases.FakeAwareMigrationSqlExecutor>());
        services.AddSingleton<Databases.SupabaseMigrationRunner>();

        // Platform connections (FR-142, §4.20, §4.21): each platform's REST or GraphQL API on its fixed host (§8: never
        // configurable, so no SSRF); the Fake for all but Vercel under Deployments:Provider=Fake.
        foreach (var (name, baseUrl) in PlatformHosts.All)
        {
            services.AddHttpClient(name, client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            });
        }

        services.AddSingleton<PlatformHttp>();
        services.AddSingleton<IPlatformConnectionValidator, VercelConnectionValidator>();
        services.AddSingleton<IPlatformConnectionValidator, NetlifyConnectionValidator>();
        services.AddSingleton<IPlatformConnectionValidator, CloudflareConnectionValidator>();
        services.AddSingleton<IPlatformConnectionValidator, RenderConnectionValidator>();
        services.AddSingleton<IPlatformConnectionValidator, RailwayConnectionValidator>();
        services.AddSingleton<IPlatformConnectionValidator, SupabaseConnectionValidator>();
        services.AddSingleton<IPlatformConnectionValidators, PlatformConnectionValidators>();

        // Issue trackers (FR-165–FR-173, §8): fixed hosts or Admin-registered GitLab instances, read-only, with every header value
        // redacted from HTTP logs (Jira's Authorization, the raw Linear key). Jira's site client follows no redirects.
        services.AddOptions<TrackerOptions>()
            .BindConfiguration(TrackerOptions.SectionName)
            .Validate(o => o.HasKnownProvider, "Trackers:Provider must be unset (the real trackers), 'Real' or 'Fake'.")
            .Validate(o => o.SearchTimeout > TimeSpan.Zero && o.SearchTimeout <= TrackerOptions.MaxSearchTimeout, "Trackers:SearchTimeout must be positive and at most 2 minutes.")
            .Validate(o => o.CheckTimeout > TimeSpan.Zero && o.CheckTimeout <= Tevrin.Domain.Trackers.SourceItemDriftRules.CheckTimeout, "Trackers:CheckTimeout must be positive and at most 10 seconds.")
            .ValidateOnStart();
        services.AddHttpClient(TrackerHosts.JiraGatewayClient, client =>
            {
                client.BaseAddress = new Uri(TrackerHosts.JiraGatewayBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            })
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient(TrackerHosts.JiraSiteClient, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            })
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient(TrackerHosts.LinearClient, client =>
            {
                client.BaseAddress = new Uri(TrackerHosts.LinearBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
            })
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<TrackerHttp>();
        services.AddSingleton<FakeTrackerScript>();
        services.AddSingleton<IIssueTrackerProvider, JiraIssueTrackerProvider>();
        services.AddSingleton<IIssueTrackerProvider, LinearIssueTrackerProvider>();
        services.AddSingleton<IIssueTrackerProvider, GitHubIssueTrackerProvider>();
        services.AddSingleton<IIssueTrackerProvider, GitLabIssueTrackerProvider>();
        services.AddSingleton<IIssueTrackers, IssueTrackers>();
        services.AddScoped<GitHubTrackerAccess>();
        services.AddSingleton<IPlatformConnectionValidator>(sp => new TrackerConnectionValidator(PlatformProvider.Jira, sp.GetRequiredService<IIssueTrackers>()));
        services.AddSingleton<IPlatformConnectionValidator>(sp => new TrackerConnectionValidator(PlatformProvider.Linear, sp.GetRequiredService<IIssueTrackers>()));

        return services;
    }

    /// <summary>
    /// FR-161: no client timeout. Streamed completions can run for many minutes and are bounded by
    /// <c>Llm:RequestTimeout</c> and <c>Llm:StreamIdleTimeout</c> per call (<c>LlmStream</c>); key checks, model lookups and
    /// Ollama <c>/api/show</c> set their own short timeouts.
    /// </summary>
    private static void ConfigureLlmClient(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("tevrin-platform");
    }
}
