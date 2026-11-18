using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-290: the registry is built once at startup; errors are logged, and a failed build is reported by readiness.</summary>
public class PipelineRegistryHostTests
{
    private static ServiceProvider Build(CapturingLogger logger, params IPipelinePack[] packs)
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        foreach (var pack in packs)
        {
            services.AddPipelinePack(pack);
        }

        services.AddSingleton<ILogger<PipelineRegistryStartup>>(logger);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FR290_a_registry_that_fails_to_build_is_built_once_and_reported_unhealthy_with_the_reason()
    {
        var throwing = new ThrowingPack();
        using var provider = Build(new CapturingLogger(), throwing);
        var check = provider.GetRequiredService<PipelineDefinitionsHealthCheck>();

        var first = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        var second = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, first.Status);
        Assert.Contains("pack exploded", first.Description, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(first.Exception);
        Assert.Equal(first.Description, second.Description);
        Assert.Equal(1, throwing.Calls);
        var resolve = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PipelineRegistry>());
        Assert.Contains("pack exploded", resolve.Message, StringComparison.Ordinal);
        Assert.Equal(1, throwing.Calls);
    }

    [Fact]
    public async Task FR290_startup_logs_a_build_failure_at_error_without_stopping_the_app()
    {
        var logger = new CapturingLogger();
        using var provider = Build(logger, new ThrowingPack());
        var startup = Assert.IsType<PipelineRegistryStartup>(Assert.Single(provider.GetServices<IHostedService>()));

        await startup.StartAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("pack exploded", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task FR290_startup_logs_every_definition_error_at_error_with_the_pipeline_id_and_path()
    {
        var logger = new CapturingLogger();
        using var provider = Build(logger, new BrokenPack());
        var startup = Assert.IsType<PipelineRegistryStartup>(Assert.Single(provider.GetServices<IHostedService>()));

        await startup.StartAsync(TestContext.Current.CancellationToken);

        var entries = logger.Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(LogLevel.Error, e.Level));
        Assert.Contains(entries, e => e.Message.Contains("broken/broken.pipeline.yaml (pipeline broken)", StringComparison.Ordinal)
            && e.Message.Contains($"{DefinitionErrorCodes.UnknownGuard} at stages[0].requires[0].guard", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Message.Contains($"{DefinitionErrorCodes.UnknownGuard} at stages[0].requires[1].guard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR290_startup_logs_a_valid_registry_at_information()
    {
        var logger = new CapturingLogger();
        using var provider = Build(logger, new FixturePack());
        var startup = Assert.IsType<PipelineRegistryStartup>(Assert.Single(provider.GetServices<IHostedService>()));

        await startup.StartAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Same(provider.GetRequiredService<PipelineRegistryHost>().Registry, provider.GetRequiredService<PipelineRegistry>());
    }

    private sealed class ThrowingPack : IPipelinePack
    {
        public int Calls { get; private set; }

        public string Id => "throwing";

        public void Register(IPackRegistry registry)
        {
            Calls++;
            throw new InvalidOperationException("pack exploded");
        }
    }

    private sealed class BrokenPack : IPipelinePack
    {
        public string Id => "broken";

        public void Register(IPackRegistry registry) =>
            registry.AddDefinition(
                "broken.pipeline.yaml",
                "pipeline: broken\nversion: 1\ntitle: Broken\napproval_modes: [strict]\ndefault_approval_mode: strict\nstart: [one]\nstages:\n  - { id: one, title: One, status: active, requires: [{ guard: broken.a }, { guard: broken.b }] }\n");
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    internal sealed class CapturingLogger : ILogger<PipelineRegistryStartup>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
