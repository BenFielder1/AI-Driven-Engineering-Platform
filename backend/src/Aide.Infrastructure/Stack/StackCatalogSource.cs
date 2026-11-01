using System.Reflection;
using Aide.Domain.Stack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Stack;

/// <summary>Where the catalog files come from (embedded resources; tests may substitute fixtures).</summary>
public interface IStackCatalogSource
{
    /// <summary>Every file of the catalog folder: relative, <c>/</c>-separated path → text.</summary>
    IReadOnlyDictionary<string, string> ReadFiles();
}

/// <summary>
/// The shipped catalog (§4.16): embedded resources of this assembly under <c>Agents/Stack/**</c> (logical names
/// <c>Aide.Infrastructure.Agents.Stack/&lt;path&gt;</c>, see the csproj).
/// </summary>
public sealed class EmbeddedStackCatalogSource(Assembly assembly, string prefix) : IStackCatalogSource
{
    /// <summary>The logical-name prefix of the shipped catalog.</summary>
    public const string ShippedPrefix = "Aide.Infrastructure.Agents.Stack/";

    /// <summary>The catalog shipped in <c>Aide.Infrastructure</c>.</summary>
    public static EmbeddedStackCatalogSource Shipped { get; } = new(typeof(EmbeddedStackCatalogSource).Assembly, ShippedPrefix);

    public IReadOnlyDictionary<string, string> ReadFiles()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);

            // %(RecursiveDir) uses the OS separator at build time; normalise to '/'.
            files[name[prefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }

        return files;
    }
}

/// <summary>
/// FR-113, FR-122: the stack catalog, loaded once at startup. When loading fails, <see cref="Catalog"/> is <c>null</c>,
/// <see cref="Errors"/> says why (each error is logged), and the <c>stack-catalog</c> readiness check is unhealthy.
/// <see cref="Version"/> and <see cref="ContentHash"/> are what release versions pin and agent runs record (M25/M26).
/// </summary>
public interface IStackCatalog
{
    /// <summary>The loaded catalog, or <c>null</c> when it failed to load.</summary>
    StackCatalog? Catalog { get; }

    /// <summary>The loaded catalog; throws when it failed to load (the readiness check is unhealthy then).</summary>
    StackCatalog Current { get; }

    /// <summary>catalog.json's version, when it could be read.</summary>
    string? Version { get; }

    /// <summary>The SHA-256 over every catalog file (§4.16).</summary>
    string ContentHash { get; }

    /// <summary><c>stack@&lt;version&gt;</c>, as recorded on agent runs.</summary>
    string Label { get; }

    IReadOnlyList<StackLoadError> Errors { get; }

    /// <summary>The load status of every pack folder.</summary>
    IReadOnlyList<StackPackStatus> Packs { get; }
}

public sealed partial class StackCatalogProvider : IStackCatalog
{
    public StackCatalogProvider(IStackCatalogSource source, ILogger<StackCatalogProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        // Loaded once per source per process: each host (and each test host) would otherwise re-parse and validate
        // every pack, which is seconds of CPU per host.
        var result = Loads.GetValue(source, s => StackCatalogLoader.Load(s.ReadFiles()));
        Catalog = result.Catalog;
        Version = result.Version;
        ContentHash = result.ContentHash;
        Errors = result.Errors;
        Packs = result.Packs;
        foreach (var error in Errors)
        {
            LogInvalid(logger, error.Pack ?? "catalog", error.Message);
        }

        if (Catalog is not null)
        {
            LogLoaded(logger, Label, ContentHash, Catalog.Packs.Count);
        }
        else
        {
            LogFailed(logger, Errors.Count);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStackCatalogSource, StackCatalogLoadResult> Loads = new();

    public StackCatalog? Catalog { get; }

    public StackCatalog Current => Catalog ?? throw new InvalidOperationException($"The stack catalog failed to load: {string.Join(" | ", Errors)}");

    public string? Version { get; }

    public string ContentHash { get; }

    public string Label => $"stack@{Version ?? "unknown"}";

    public IReadOnlyList<StackLoadError> Errors { get; }

    public IReadOnlyList<StackPackStatus> Packs { get; }

    [LoggerMessage(Level = LogLevel.Error, Message = "Stack catalog: {Source}: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string source, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The stack catalog failed to load ({Count} error(s)); the stack-catalog readiness check is unhealthy")]
    private static partial void LogFailed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Catalog} (sha256 {Hash}) with {Packs} pack(s)")]
    private static partial void LogLoaded(ILogger logger, string catalog, string hash, int packs);
}

/// <summary>FR-122: loads the catalog at startup, so a broken catalog is logged before the first request.</summary>
internal sealed class StackCatalogStartup(IStackCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalog.ContentHash;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
