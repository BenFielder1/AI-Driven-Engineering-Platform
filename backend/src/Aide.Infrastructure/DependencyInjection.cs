using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Presets;
using Aide.Infrastructure.RepoTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Configuration key of the PostgreSQL connection string (<c>ConnectionStrings:Aide</c>,
    /// env var <c>ConnectionStrings__Aide</c>).</summary>
    public const string ConnectionStringName = "Aide";

    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public static IServiceCollection AddAideInfrastructure(this IServiceCollection services)
    {
        // The connection string is read when the context is first resolved (not at registration),
        // so tests and build-time OpenAPI generation can supply or omit it.
        services.AddDbContext<AideDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
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
        });
        services.TryAddSingleton<IProjectEventPublisher, NullProjectEventPublisher>();
        services.TryAddSingleton<ProjectEventPublishingInterceptor>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NotificationOutboxInterceptor>();
        services.TryAddSingleton<ReleaseVersionStampingInterceptor>();

        // FR-79: fills last4 of LLM keys migrated from v1 projects (needs ISecretProtector from AddAideDataProtection).
        services.AddHostedService<LlmCredentialLast4Backfill>();

        // LLM provider configuration (FR-22): defaults and which providers have a system key.
        services.AddOptions<LlmOptions>()
            .BindConfiguration(LlmOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
        services.AddSingleton<ILlmProviderCatalog, LlmProviderCatalog>();

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
        services.AddSingleton<IFakeAgentResponder>(_ => new FakeCodeReviewResponder(FakeCodeReviewScript.Default));
        // FR-71: the Fake provider's scripted repo tool calls (last registered per agent wins; tests override them).
        foreach (var script in FakeToolScripts.Defaults())
        {
            services.AddSingleton<IFakeToolScript>(script);
        }

        services.AddSingleton<FakeLlmProvider>();
        services.AddTransient<ILlmProvider>(sp => sp.GetRequiredService<FakeLlmProvider>());
        services.AddSingleton<IPromptLibrary, EmbeddedPromptLibrary>();

        // Tech stack presets (FR-74, §4.11): the shipped presets (embedded), loaded once at startup; tests add sources.
        services.AddSingleton<IPresetSource>(EmbeddedPresetSource.Shipped);
        services.TryAddSingleton<IPresetCatalog, PresetCatalog>();
        services.AddHostedService<PresetCatalogStartup>();
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("aide-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", GitHubGitProvider.ApiVersion);
        });
        services.AddScoped<GitHubGitProvider>();
        services.AddScoped<IGitProvider>(sp => sp.GetRequiredService<GitHubGitProvider>());
        services.AddScoped<IGitProvider, LocalGitProvider>();

        // The AIDE GitHub App (FR-80–FR-83, §4.14): configuration (GitHub:App:*) or the manifest-registered row, the REST
        // API on the fixed GitHub hosts, installation tokens (in-memory cache ≤ 55 min) and the github_app provider.
        services.AddOptions<GitHubAppOptions>()
            .BindConfiguration(GitHubAppOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GitHubAppOptions>, GitHubAppOptionsValidator>();
        services.AddHttpClient(GitHubApi.WebHttpClientName, client =>
        {
            client.BaseAddress = new Uri(GitHubApi.WebBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("aide-platform");
        });
        services.AddTransient<GitHubApi>();
        services.AddScoped<IGitHubAppStore, GitHubAppStore>();
        services.AddSingleton<GitHubInstallationTokenCache>();
        services.AddScoped<IGitHubInstallationTokens, GitHubInstallationTokens>();
        services.AddSingleton<GitHubBotIdentities>();
        services.AddSingleton<GitHubAppInfoCache>();
        services.AddScoped<IGitProvider, GitHubAppGitProvider>();
        services.AddScoped<IGitProviderResolver, GitProviderResolver>();

        // Deployments (FR-87–FR-91, §4.15, D-V2-10): Vercel's REST API on the fixed host api.vercel.com (§8: never
        // configurable, so no SSRF), the deterministic Fake (Deployments:Provider=Fake) and the env write limiter.
        services.AddOptions<DeploymentOptions>().BindConfiguration(DeploymentOptions.SectionName);
        services.AddHttpClient(VercelDeploymentProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(VercelDeploymentProvider.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("aide-platform");
        });
        services.AddSingleton<VercelEnvWriteLimiter>();
        services.AddSingleton<VercelDeploymentProvider>();
        services.AddSingleton<FakeDeploymentProvider>();
        services.AddSingleton<IDeploymentProviderResolver, DeploymentProviderResolver>();

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("aide-platform");
    }
}
