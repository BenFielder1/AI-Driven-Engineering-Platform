using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tevrin.Api.Auth;
using Tevrin.Api.Logging;
using Tevrin.Infrastructure;
using Tevrin.Infrastructure.Configuration;
using Tevrin.Infrastructure.Persistence;

namespace Tevrin.UnitTests.Common;

/// <summary>
/// FR-325 (§4.77, D-0.8.0-7): config names become Tevrin; the old names are read as a fallback until 0.9.0, with one
/// startup warning per old name used. When both are set the new name wins and the old one gets an "ignored" warning.
/// </summary>
public sealed class Fr325LegacyConfigNamesTests
{
    private const string NewKey = "ConnectionStrings:Tevrin";
    private const string OldKey = "ConnectionStrings:Aide";

    [Fact]
    public void FR325_The_connection_string_is_ConnectionStrings_Tevrin_with_ConnectionStrings_Aide_as_the_fallback()
    {
        Assert.Equal("Tevrin", DependencyInjection.ConnectionStringName);
        Assert.Equal(NewKey, LegacyConfigNames.ConnectionString.NewKey);
        Assert.Equal(OldKey, LegacyConfigNames.ConnectionString.OldKey);
        Assert.Equal("ConnectionStrings__Tevrin", LegacyConfigNames.ConnectionString.NewEnvName);
        Assert.Equal("ConnectionStrings__Aide", LegacyConfigNames.ConnectionString.OldEnvName);
        Assert.Contains(LegacyConfigNames.ConnectionString, LegacyConfigNames.All);
        Assert.Equal("0.9.0", LegacyConfigNames.RemovedIn);
    }

    [Fact]
    public void FR325_Only_the_new_name_set_is_used_without_a_warning()
    {
        var config = Config((NewKey, "Host=new"));
        var logger = new ListLogger();

        Assert.Equal("Host=new", LegacyConfigNames.Get(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(LegacyConfigUse.None, LegacyConfigNames.Classify(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(0, LegacyConfigNames.LogWarnings(config, logger));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FR325_Only_the_old_name_set_is_used_and_warned_about_once()
    {
        var config = Config((OldKey, "Host=old"));
        var logger = new ListLogger();

        Assert.Equal("Host=old", LegacyConfigNames.Get(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(LegacyConfigUse.OldUsed, LegacyConfigNames.Classify(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(1, LegacyConfigNames.LogWarnings(config, logger));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ConnectionStrings:Aide", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:Tevrin", entry.Message, StringComparison.Ordinal);
        Assert.Contains("deprecated", entry.Message, StringComparison.Ordinal);
        Assert.Contains("0.9.0", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=old", entry.Message, StringComparison.Ordinal); // never the value (a secret)
    }

    [Fact]
    public void FR325_Both_names_set_the_new_one_wins_and_the_old_one_is_warned_as_ignored()
    {
        var config = Config((NewKey, "Host=new"), (OldKey, "Host=old"));
        var logger = new ListLogger();

        Assert.Equal("Host=new", LegacyConfigNames.Get(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(LegacyConfigUse.OldIgnored, LegacyConfigNames.Classify(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(1, LegacyConfigNames.LogWarnings(config, logger));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ignored", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR325_Neither_name_set_reads_null_without_a_warning()
    {
        var config = Config();
        var logger = new ListLogger();

        Assert.Null(LegacyConfigNames.Get(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(LegacyConfigUse.None, LegacyConfigNames.Classify(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(0, LegacyConfigNames.LogWarnings(config, logger));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void FR325_An_empty_new_name_counts_as_unset()
    {
        var config = Config((NewKey, " "), (OldKey, "Host=old"));

        Assert.Equal("Host=old", LegacyConfigNames.Get(config, LegacyConfigNames.ConnectionString));
        Assert.Equal(LegacyConfigUse.OldUsed, LegacyConfigNames.Classify(config, LegacyConfigNames.ConnectionString));
    }

    [Fact]
    public void FR325_The_old_env_var_ConnectionStrings__Aide_alone_is_read()
    {
        // A prefixed copy of the real variable, so parallel tests never see it in the process environment.
        var prefix = $"FR325_{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "ConnectionStrings__Aide", "Host=from-env");
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var logger = new ListLogger();

            Assert.Equal("Host=from-env", LegacyConfigNames.Get(config, LegacyConfigNames.ConnectionString));
            Assert.Equal(1, LegacyConfigNames.LogWarnings(config, logger));
            Assert.Contains("ConnectionStrings__Aide", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "ConnectionStrings__Aide", null);
        }
    }

    [Fact]
    public void FR325_The_database_context_connects_with_only_the_old_connection_string()
    {
        const string Old = "Host=old-host;Port=5432;Database=aide;Username=aide";
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Config((OldKey, Old)));
        services.AddLogging();
        services.AddTevrinInfrastructure();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<TevrinDbContext>();

        Assert.Equal(Old, db.Database.GetConnectionString());
    }

    [Fact]
    public void FR325_The_auth_scheme_is_Tevrin() => Assert.Equal("Tevrin", AuthConstants.SmartScheme);

    [Fact]
    public void FR325_The_activity_source_is_Tevrin_and_its_tags_are_tevrin_names()
    {
        Assert.Equal("Tevrin", TevrinTelemetry.SourceName);
        Assert.Equal("Tevrin", TevrinTelemetry.Source.Name);
        var tags = typeof(TevrinTelemetry).GetFields()
            .Where(f => f.IsLiteral && f.Name.EndsWith("Tag", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(tags);
        Assert.All(tags, tag => Assert.StartsWith("tevrin.", tag, StringComparison.Ordinal));
        Assert.Contains("tevrin.run_id", tags);
        Assert.Contains("tevrin.project_id", tags);
        Assert.Contains("tevrin.stage", tags);
        Assert.Contains("tevrin.run_status", tags);
        Assert.Contains("tevrin.user_id", tags);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class ListLogger : ILogger
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
