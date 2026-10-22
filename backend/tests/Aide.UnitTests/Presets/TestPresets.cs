using Aide.Domain.Presets;
using Aide.Infrastructure.Presets;

namespace Aide.UnitTests.Presets;

/// <summary>The test-only fixture presets (<c>tests/Fixtures/Presets/</c>, embedded in this assembly).</summary>
public static class TestPresets
{
    public const string Prefix = "Aide.Tests.Presets/";

    public const string FixtureId = "test-stack";

    public static EmbeddedPresetSource Source { get; } = new(typeof(TestPresets).Assembly, Prefix);

    /// <summary>The fixture's files (relative path → text).</summary>
    public static IReadOnlyDictionary<string, string> FixtureFiles =>
        Source.ReadFolders().Single(f => f.Id == FixtureId).Files;

    /// <summary>The loaded fixture preset (throws when it doesn't load).</summary>
    public static Preset Fixture { get; } = Load(FixtureFiles);

    /// <summary>Loads <paramref name="files"/> as the fixture folder, throwing with every reason on failure.</summary>
    public static Preset Load(IReadOnlyDictionary<string, string> files)
    {
        var result = PresetLoader.Load(FixtureId, files);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException(string.Join(" | ", result.Error!.Details));
    }

    /// <summary>The fixture's files with <paramref name="change"/> applied (to break one thing).</summary>
    public static Dictionary<string, string> With(Action<Dictionary<string, string>> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var files = new Dictionary<string, string>(FixtureFiles, StringComparer.Ordinal);
        change(files);
        return files;
    }

    /// <summary>The fixture's files with <c>preset.json</c> text replaced via <paramref name="edit"/>.</summary>
    public static Dictionary<string, string> WithManifest(Func<string, string> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return With(f => f[PresetLoader.ManifestFile] = edit(f[PresetLoader.ManifestFile]));
    }
}
