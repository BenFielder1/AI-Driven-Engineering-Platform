using Aide.Domain.Stack;
using Aide.Infrastructure.Migrations;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-129, §4.23: the compositions the <c>RetirePresets</c> migration writes for the retired v2 presets are exactly what
/// <see cref="StackCompositionJson"/> stores for the equivalent choices validated against the shipped catalog (with the
/// catalog hash left NULL for the app to re-pin), and the packs it writes are <see cref="StackPinning.PackIds"/>.
/// </summary>
public sealed class PresetMigrationCompositionTests
{
    public static TheoryData<string, bool, string, string> Documents => new()
    {
        { "nextjs-supabase", true, RetirePresets.SupabaseVercelJson, RetirePresets.SupabaseVercelPacks },
        { "nextjs-supabase", false, RetirePresets.SupabaseJson, RetirePresets.SupabasePacks },
        { "nextjs-frontend", true, RetirePresets.FrontendVercelJson, RetirePresets.FrontendVercelPacks },
        { "nextjs-frontend", false, RetirePresets.FrontendJson, RetirePresets.FrontendPacks },
    };

    /// <summary>§4.23: fe-nextjs at ".", no backend, Supabase BaaS (or no data), GitHub, Vercel if set up, Supabase database host, no dev loop, same origin.</summary>
    private static StackSelection Equivalent(string presetId, bool vercel)
    {
        var supabase = presetId == "nextjs-supabase";
        return new StackSelection
        {
            Frontend = new StackComponentPick("fe-nextjs", "."),
            Backend = new StackComponentPick(StackIds.None),
            Data = new StackDataPick(supabase ? "data-supabase" : StackIds.None),
            Vcs = new StackVcsPick(StackPinning.GitHubVcs),
            Deploy = new StackDeployPick(vercel ? "deploy-vercel" : StackIds.None, StackIds.None, supabase ? "supabase" : StackIds.None, StackIds.None),
            OriginModel = "same-origin",
        };
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void FR129_The_migrated_composition_is_the_validated_equivalent_of_the_preset(string presetId, bool vercel, string json, string packs)
    {
        var validated = Valid(Equivalent(presetId, vercel));

        Assert.Equal(StackCompositionJson.Serialize(validated with { CatalogHash = null! }), json);
        Assert.Equal(RetirePresets.CatalogVersion, validated.CatalogVersion);
        Assert.Equal($"{{{string.Join(",", StackPinning.PackIds(validated, Catalog))}}}", packs);
    }

    public static TheoryData<string> Json => new()
    {
        RetirePresets.SupabaseVercelJson,
        RetirePresets.SupabaseJson,
        RetirePresets.FrontendVercelJson,
        RetirePresets.FrontendJson,
    };

    [Theory]
    [MemberData(nameof(Json))]
    public void FR129_The_migrated_composition_round_trips_and_revalidates_against_the_shipped_catalog(string json)
    {
        var stored = StackCompositionJson.Deserialize(json);

        Assert.NotNull(stored);
        Assert.Null(stored.CatalogHash);
        Assert.Equal(json, StackCompositionJson.Serialize(stored));
        var revalidated = StackCompatibility.Validate(StackPinning.ToSelection(stored), Catalog);
        Assert.True(revalidated.IsValid, string.Join(" | ", revalidated.Violations.Select(v => $"{v.Layer} {v.Option} {v.Rule}")));
        Assert.Equal(json, StackCompositionJson.Serialize(revalidated.Composition! with { CatalogHash = null! }));
    }
}
