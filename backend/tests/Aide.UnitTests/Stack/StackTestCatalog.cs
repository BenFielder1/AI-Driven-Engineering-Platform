using System.Text.Json.Nodes;
using Aide.Domain.Stack;
using Aide.Infrastructure.Stack;

namespace Aide.UnitTests.Stack;

/// <summary>The shipped stack catalog (loaded once), its files for mutation tests, and composition helpers.</summary>
internal static class StackTestCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Files = new(() => EmbeddedStackCatalogSource.Shipped.ReadFiles());

    private static readonly Lazy<StackCatalog> Loaded = new(() =>
    {
        var result = StackCatalogLoader.Load(Files.Value);
        return result.Catalog ?? throw new InvalidOperationException($"The shipped catalog failed to load: {string.Join(" | ", result.Errors)}");
    });

    /// <summary>The shipped catalog.</summary>
    public static StackCatalog Catalog => Loaded.Value;

    /// <summary>A mutable copy of the shipped catalog's files.</summary>
    public static Dictionary<string, string> ShippedFiles() => new(Files.Value, StringComparer.Ordinal);

    /// <summary>Applies <paramref name="change"/> to the JSON file at <paramref name="path"/> in <paramref name="files"/>.</summary>
    public static void EditJson(Dictionary<string, string> files, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(files[path])!.AsObject();
        change(node);
        files[path] = node.ToJsonString();
    }

    /// <summary>A selection; <c>null</c> leaves a layer out (not chosen for Evaluate, None or default for Validate).</summary>
    public static StackSelection Sel(
        string? frontend = null,
        string? backend = null,
        string? data = null,
        string? vcs = null,
        string? frontendHost = null,
        string? backendHost = null,
        string? databaseHost = null,
        string? local = null,
        string? origin = null,
        bool selfManaged = false) => new()
        {
            Frontend = frontend is null ? null : new StackComponentPick(frontend),
            Backend = backend is null ? null : new StackComponentPick(backend),
            Data = data is null ? null : new StackDataPick(data),
            Vcs = vcs is null ? null : new StackVcsPick(vcs, SelfManaged: selfManaged),
            Deploy = frontendHost is null && backendHost is null && databaseHost is null && local is null
                ? null
                : new StackDeployPick(frontendHost, backendHost, databaseHost, local),
            OriginModel = origin,
        };

    public static StackEvaluation Evaluate(StackSelection selection)
    {
        var result = StackCompatibility.Evaluate(selection, Catalog);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    public static StackOptionEvaluation Option(StackEvaluation evaluation, string choice, string option) =>
        evaluation.Choice(choice).Options.Single(o => o.Option == option);

    /// <summary>The option ids of <paramref name="choice"/> that can be picked (allowed or caveat).</summary>
    public static IReadOnlyList<string> Selectable(StackEvaluation evaluation, string choice) =>
        [.. evaluation.Choice(choice).Options.Where(o => o.State is StackOptionState.Allowed or StackOptionState.Caveat).Select(o => o.Option)];

    public static StackComposition Valid(StackSelection selection)
    {
        var validation = StackCompatibility.Validate(selection, Catalog);
        Assert.True(validation.IsValid, string.Join(" | ", validation.Violations.Select(v => $"{v.Layer} {v.Option} {v.Rule} {v.Reason}").Concat(validation.InputErrors.Select(e => e.ToString()))));
        return validation.Composition!;
    }

    public static IReadOnlyList<StackViolation> Violations(StackSelection selection)
    {
        var validation = StackCompatibility.Validate(selection, Catalog);
        Assert.Empty(validation.InputErrors);
        Assert.False(validation.IsValid, "Expected the composition to be rejected.");
        return validation.Violations;
    }
}
