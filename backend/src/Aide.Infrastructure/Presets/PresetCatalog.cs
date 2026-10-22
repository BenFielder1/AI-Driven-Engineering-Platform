using System.Reflection;
using Aide.Domain.Presets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Presets;

/// <summary>The files of one preset folder: the folder name and its files (relative, <c>/</c>-separated path → text).</summary>
public sealed record PresetFolder(string Id, IReadOnlyDictionary<string, string> Files);

/// <summary>A preset folder that failed to load (§4.11): the folder and every reason.</summary>
public sealed record PresetLoadError(string Id, IReadOnlyList<string> Reasons);

/// <summary>Where preset folders come from (embedded resources; tests add fixtures).</summary>
public interface IPresetSource
{
    IReadOnlyList<PresetFolder> ReadFolders();
}

/// <summary>
/// FR-74, D-V2-12: the loaded presets, read once. A folder that fails to load is left out and listed in
/// <see cref="Errors"/> (logged at startup, and the readiness health check is unhealthy while any exist).
/// </summary>
public interface IPresetCatalog
{
    /// <summary>Every preset that loaded, ordered by id.</summary>
    IReadOnlyList<Preset> All { get; }

    /// <summary>The folders that failed to load.</summary>
    IReadOnlyList<PresetLoadError> Errors { get; }

    /// <summary>The preset with <paramref name="id"/> (ordinal), or <c>null</c>.</summary>
    Preset? Find(string? id);
}

/// <summary>
/// The shipped presets: embedded resources of this assembly under <c>Agents/Presets/&lt;id&gt;/**</c> (logical names
/// <c>Aide.Infrastructure.Agents.Presets/&lt;id&gt;/&lt;path&gt;</c>, see the csproj). Other assemblies (test fixtures) use
/// the same layout with their own prefix.
/// </summary>
public sealed class EmbeddedPresetSource(Assembly assembly, string prefix) : IPresetSource
{
    /// <summary>The logical-name prefix of the shipped presets.</summary>
    public const string ShippedPrefix = "Aide.Infrastructure.Agents.Presets/";

    /// <summary>The presets shipped in <c>Aide.Infrastructure</c>.</summary>
    public static EmbeddedPresetSource Shipped { get; } = new(typeof(EmbeddedPresetSource).Assembly, ShippedPrefix);

    public IReadOnlyList<PresetFolder> ReadFolders()
    {
        var folders = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            // %(RecursiveDir) uses the OS separator at build time; normalise to '/'.
            var relative = name[prefix.Length..].Replace('\\', '/');
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                continue; // a file directly under Presets/ (e.g. a README) belongs to no preset
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var id = relative[..slash];
            if (!folders.TryGetValue(id, out var files))
            {
                files = new Dictionary<string, string>(StringComparer.Ordinal);
                folders[id] = files;
            }

            files[relative[(slash + 1)..]] = reader.ReadToEnd();
        }

        return [.. folders.Select(f => new PresetFolder(f.Key, f.Value))];
    }
}

public sealed partial class PresetCatalog : IPresetCatalog
{
    private readonly Dictionary<string, Preset> _byId;

    public PresetCatalog(IEnumerable<IPresetSource> sources, ILogger<PresetCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);
        var loaded = Load(sources.SelectMany(s => s.ReadFolders()));
        _byId = loaded.Presets.ToDictionary(p => p.Id, StringComparer.Ordinal);
        All = [.. loaded.Presets.OrderBy(p => p.Id, StringComparer.Ordinal)];
        Errors = loaded.Errors;
        foreach (var error in Errors)
        {
            var reasons = string.Join(" | ", error.Reasons);
            LogInvalid(logger, error.Id, reasons);
        }

        var labels = string.Join(", ", All.Select(p => p.RunLabel));
        LogLoaded(logger, All.Count, labels);
    }

    public IReadOnlyList<Preset> All { get; }

    public IReadOnlyList<PresetLoadError> Errors { get; }

    public Preset? Find(string? id) => id is not null && _byId.TryGetValue(id, out var preset) ? preset : null;

    /// <summary>Loads every folder (pure apart from the reading done by the sources); duplicate ids are errors.</summary>
    public static (IReadOnlyList<Preset> Presets, IReadOnlyList<PresetLoadError> Errors) Load(IEnumerable<PresetFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var presets = new List<Preset>();
        var errors = new List<PresetLoadError>();
        foreach (var folder in folders)
        {
            var result = PresetLoader.Load(folder.Id, folder.Files);
            if (result.IsFailure)
            {
                errors.Add(new PresetLoadError(folder.Id, result.Error!.Details.Count > 0 ? result.Error.Details : [result.Error.Message]));
            }
            else if (presets.Any(p => p.Id == result.Value.Id))
            {
                errors.Add(new PresetLoadError(folder.Id, [$"Another source already has a preset '{folder.Id}'."]));
            }
            else
            {
                presets.Add(result.Value);
            }
        }

        return (presets, errors);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Preset {PresetId} failed to load: {Reasons}")]
    private static partial void LogInvalid(ILogger logger, string presetId, string reasons);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} preset(s): {Presets}")]
    private static partial void LogLoaded(ILogger logger, int count, string presets);
}

/// <summary>§4.11: loads the presets at startup, so a broken preset is logged before the first request.</summary>
internal sealed class PresetCatalogStartup(IPresetCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalog.All;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>FR-74, FR-76: the preset helpers the agent pipeline uses.</summary>
public static class PresetCatalogExtensions
{
    /// <summary>
    /// The preset pinned on <paramref name="version"/> (by id), as shipped now; <c>null</c> without a preset or when the
    /// pinned preset is no longer shipped. Its <see cref="Preset.Version"/> may differ from the pinned
    /// <c>preset_version</c> after an upgrade; runs record the preset they actually used (<see cref="StampRun"/>).
    /// </summary>
    public static Preset? ForVersion(this IPresetCatalog catalog, Domain.Entities.ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(version);
        return catalog.Find(version.PresetId);
    }

    /// <summary>
    /// FR-74, FR-76: records on <paramref name="run"/> the preset it ran with (<c>preset_id</c>, <c>preset_version</c>; together
    /// <see cref="Preset.RunLabel"/>, <c>&lt;id&gt;@&lt;version&gt;</c>) and the specialist <paramref name="agentKey"/> (plan steps only).
    /// Without a preset both preset fields are cleared.
    /// </summary>
    public static void StampRun(Domain.Entities.AgentRun run, Preset? preset, string? agentKey = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.PresetId = preset?.Id;
        run.PresetVersion = preset?.Version;
        if (agentKey is not null)
        {
            run.AgentKey = agentKey;
        }
    }
}
