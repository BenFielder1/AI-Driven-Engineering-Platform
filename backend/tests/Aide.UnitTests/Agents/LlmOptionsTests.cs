using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure;
using Aide.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aide.UnitTests.Agents;

/// <summary>FR-157, FR-158, FR-160: start-up validation of the LLM configuration, the catalog and the output limit resolver.</summary>
public sealed class LlmOptionsTests
{
    private static Dictionary<string, string?> Base() => new()
    {
        ["Llm:DefaultProvider"] = "anthropic",
        ["Llm:Providers:Anthropic:DefaultModel"] = "claude-opus-5-5",
        ["Llm:Providers:OpenAI:DefaultModel"] = "gpt-6-astra",
        ["Llm:Providers:Ollama:DefaultModel"] = "qwen2.5-coder",
        ["Llm:Providers:Fake:DefaultModel"] = "fake-1",
        ["Llm:Providers:Anthropic:Catalog:0:Id"] = "claude-opus-5-5",
        ["Llm:Providers:Anthropic:Catalog:0:Name"] = "Claude Opus 5.5",
        ["Llm:Providers:Anthropic:Catalog:0:MaxOutputTokens"] = "128000",
        ["Llm:Providers:Anthropic:Catalog:0:ContextTokens"] = "1000000",
        ["Llm:Providers:Anthropic:Catalog:0:Recommended"] = "true",
        ["Llm:Providers:Anthropic:Catalog:1:Id"] = "claude-haiku-4-5",
        ["Llm:Providers:Anthropic:Catalog:1:MaxOutputTokens"] = "64000",
        ["Llm:Providers:Anthropic:Catalog:1:ContextTokens"] = "200000",
    };

    private static ServiceProvider Services(Dictionary<string, string?> settings, IAnthropicModelInfo? anthropic = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAideInfrastructure();
        if (anthropic is not null)
        {
            services.AddScoped(_ => anthropic);
        }

        return services.BuildServiceProvider();
    }

    private static string[] Failures(Dictionary<string, string?> settings)
    {
        using var services = Services(settings);
        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<LlmOptions>>().Value);
        return [.. ex.Failures];
    }

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] overrides)
    {
        var settings = Base();
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return settings;
    }

    // ------------------------------------------------------------------ validation (FR-157, FR-158, FR-160)

    [Fact]
    public void FR157_The_shipped_shape_with_every_provider_enabled_is_valid()
    {
        using var services = Services(Base());

        var options = services.GetRequiredService<IOptions<LlmOptions>>().Value;

        Assert.True(options.Providers.Fake.Enabled);
        Assert.Null(options.MaxOutputTokensCeiling);
    }

    [Fact]
    public void FR157_Startup_fails_when_no_provider_is_enabled()
    {
        var failures = Failures(With(
            ("Llm:Providers:Anthropic:Enabled", "false"),
            ("Llm:Providers:OpenAI:Enabled", "false"),
            ("Llm:Providers:Ollama:Enabled", "false"),
            ("Llm:Providers:Fake:Enabled", "false")));

        Assert.Contains(failures, f => f.Contains("At least one LLM provider must be enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void FR157_Startup_fails_when_the_default_provider_is_disabled()
    {
        var failures = Failures(With(("Llm:DefaultProvider", "fake"), ("Llm:Providers:Fake:Enabled", "false")));

        Assert.Contains(failures, f => f.Contains("Llm:DefaultProvider (fake) must be an enabled provider", StringComparison.Ordinal));
    }

    [Fact]
    public void FR158_Catalog_ids_must_be_unique_per_provider()
    {
        var failures = Failures(With(("Llm:Providers:Anthropic:Catalog:1:Id", "Claude-Opus-5-5")));

        Assert.Contains(failures, f => f.Contains("Llm:Providers:Anthropic:Catalog lists the model id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void FR160_Catalog_MaxOutputTokens_must_be_at_least_1(string value)
    {
        var failures = Failures(With(("Llm:Providers:Anthropic:Catalog:1:MaxOutputTokens", value)));

        Assert.Contains("Llm:Providers:Anthropic:Catalog:1:MaxOutputTokens must be at least 1.", failures);
    }

    [Fact]
    public void FR160_The_ceiling_must_be_at_least_1_when_set() =>
        Assert.Contains("Llm:MaxOutputTokensCeiling must be at least 1 when set.", Failures(With(("Llm:MaxOutputTokensCeiling", "0"))));

    [Fact]
    public void FR161_The_request_timeout_defaults_to_30_minutes_and_the_stream_idle_timeout_to_2_minutes()
    {
        using var services = Services(Base());

        var options = services.GetRequiredService<IOptions<LlmOptions>>().Value;

        Assert.Equal((TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2)), (options.RequestTimeout, options.StreamIdleTimeout));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void FR161_The_stream_idle_timeout_must_be_positive(string value) =>
        Assert.Contains("Llm:StreamIdleTimeout must be positive.", Failures(With(("Llm:StreamIdleTimeout", value))));

    [Fact]
    public void FR161_The_LLM_HttpClients_have_no_client_timeout_so_long_streams_are_not_cut()
    {
        using var services = Services(Base());
        var factory = services.GetRequiredService<IHttpClientFactory>();

        using var modelInfo = factory.CreateClient(IAnthropicModelInfo.HttpClientName);
        using var anthropic = factory.CreateClient(nameof(Aide.Infrastructure.Agents.AnthropicLlmProvider));

        Assert.Equal(Timeout.InfiniteTimeSpan, modelInfo.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, anthropic.Timeout);
    }

    // ------------------------------------------------------------------ catalog (FR-157, FR-158)

    [Fact]
    public void FR157_Railway_config_lists_only_Anthropic_and_OpenAI()
    {
        using var services = Services(With(("Llm:Providers:Ollama:Enabled", "false"), ("Llm:Providers:Fake:Enabled", "false")));
        var catalog = services.GetRequiredService<ILlmProviderCatalog>();

        Assert.Equal([LlmProvider.Anthropic, LlmProvider.Openai], catalog.EnabledProviders);
        Assert.Null(catalog.Availability(LlmProvider.Fake, 0));
        Assert.Equal(new LlmProviderAvailability(false, LlmProviderReason.NeedsConnection), catalog.Availability(LlmProvider.Anthropic, 0));
        Assert.Equal(Aide.Domain.Common.ErrorCodes.LlmProviderUnavailable, catalog.DefaultForNewProject([]).Error?.Code);
        Assert.Equal(LlmProvider.Anthropic, catalog.DefaultForNewProject([LlmProvider.Anthropic]).Value);
    }

    [Fact]
    public void FR158_The_catalog_is_read_from_configuration_and_names_default_to_the_id()
    {
        using var services = Services(Base());
        var catalog = services.GetRequiredService<ILlmProviderCatalog>().Catalog(LlmProvider.Anthropic);

        Assert.Equal(
            [
                new Aide.Domain.Agents.LlmCatalogModel("claude-opus-5-5", "Claude Opus 5.5", 128_000, 1_000_000, true),
                new Aide.Domain.Agents.LlmCatalogModel("claude-haiku-4-5", "claude-haiku-4-5", 64_000, 200_000, false),
            ],
            catalog);
    }

    // ------------------------------------------------------------------ output limit resolver (FR-160)

    [Fact]
    public async Task FR160_A_listed_model_uses_the_catalog_without_a_lookup()
    {
        var anthropic = new FakeAnthropicModelInfo(96_000);
        await using var services = Services(Base(), anthropic);
        await using var scope = services.CreateAsyncScope();

        var limit = await scope.ServiceProvider.GetRequiredService<ILlmOutputLimits>().ResolveAsync(LlmProvider.Anthropic, "claude-opus-5-5", "sk-key", TestContext.Current.CancellationToken);

        Assert.Equal((128_000, 0), (limit, anthropic.Calls));
    }

    [Fact]
    public async Task FR160_An_unlisted_Anthropic_model_uses_the_discovered_limit_capped_by_the_ceiling()
    {
        var anthropic = new FakeAnthropicModelInfo(96_000);
        await using var services = Services(With(("Llm:MaxOutputTokensCeiling", "50000")), anthropic);
        await using var scope = services.CreateAsyncScope();

        var limit = await scope.ServiceProvider.GetRequiredService<ILlmOutputLimits>().ResolveAsync(LlmProvider.Anthropic, "claude-new-x", "sk-key", TestContext.Current.CancellationToken);

        Assert.Equal((50_000, 1), (limit, anthropic.Calls));
    }

    [Fact]
    public async Task FR160_Without_a_key_no_lookup_is_made_and_the_fallback_applies()
    {
        var anthropic = new FakeAnthropicModelInfo(96_000);
        await using var services = Services(With(("Llm:MaxOutputTokens", "12000")), anthropic);
        await using var scope = services.CreateAsyncScope();

        var limit = await scope.ServiceProvider.GetRequiredService<ILlmOutputLimits>().ResolveAsync(LlmProvider.Anthropic, "claude-new-x", apiKey: null, TestContext.Current.CancellationToken);

        Assert.Equal((12_000, 0), (limit, anthropic.Calls));
    }

    private sealed class FakeAnthropicModelInfo(int? maxTokens) : IAnthropicModelInfo
    {
        public int Calls { get; private set; }

        public Task<int?> MaxOutputTokensAsync(string model, string apiKey, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(maxTokens);
        }
    }
}
