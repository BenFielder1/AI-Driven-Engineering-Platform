using System.Runtime.CompilerServices;
using System.Text;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Infrastructure.Agents;
using Tevrin.Pack.Sdlc;

namespace Tevrin.UnitTests.Agents;

/// <summary>
/// FR-295 (M59b2): the exact prompt each SDLC agent sends today, for a seeded input set, is pinned in
/// <c>Agents/Golden/&lt;agent&gt;.txt</c>. Moving an agent onto the engine runner must keep these byte-identical. Two renders
/// per agent: every placeholder filled (each value also carries a closing data tag, so neutralisation is pinned too), and
/// only the required ones (optional placeholders render empty). Set <c>UPDATE_GOLDEN_PROMPTS=1</c> to rewrite the files
/// after an intended prompt change (and bump the template's version).
/// </summary>
public class GoldenPromptTests
{
    public static TheoryData<string> Agents() => [.. SdlcAgents.All];

    [Theory]
    [MemberData(nameof(Agents))]
    public void FR295_prompt_is_unchanged(string agent)
    {
        var actual = Render(agent);
        var path = Path.Combine(AppContext.BaseDirectory, "Agents", "Golden", $"{agent}.txt");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN_PROMPTS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceDirectory(), "Golden", $"{agent}.txt"), actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"No golden prompt for {agent}: run with UPDATE_GOLDEN_PROMPTS=1.");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FR295_idea_refinement_prompt_is_unchanged() => FR295_prompt_is_unchanged(SdlcAgents.IdeaRefinement);

    [Fact]
    public void FR295_spec_generator_prompt_is_unchanged() => FR295_prompt_is_unchanged(SdlcAgents.SpecGenerator);

    [Fact]
    public void FR295_plan_generator_prompt_is_unchanged() => FR295_prompt_is_unchanged(SdlcAgents.PlanGenerator);

    /// <summary>The golden text: version, placeholders, then the full and the required-only renders.</summary>
    internal static string Render(string agent) => Render(agent, (_, values) => values);

    /// <summary>
    /// The golden text, rendering each seeded value set through <paramref name="values"/> (FR-317: e.g. the engine's
    /// prompt path, which rebuilds the values from input artifacts, context sections and host values).
    /// </summary>
    internal static string Render(string agent, Func<PromptTemplate, Dictionary<string, string?>, IReadOnlyDictionary<string, string?>> values)
    {
        var template = new EmbeddedPromptLibrary().Template(agent);
        var builder = new StringBuilder();
        builder.Append("agent: ").Append(agent).Append('\n');
        builder.Append("version: ").Append(template.Version).Append('\n');
        builder.Append("placeholders: ")
            .Append(string.Join(", ", template.Placeholders.Select(p => p.Optional ? $"{p.Name}?" : p.Name)))
            .Append('\n');

        var all = template.Placeholders
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => (string?)Seed(template, name), StringComparer.Ordinal);
        AppendRender(builder, "all placeholders", template, values(template, all));

        var required = template.RequiredInputs.ToDictionary(name => name, name => (string?)Seed(template, name), StringComparer.Ordinal);
        AppendRender(builder, "required only", template, values(template, required));
        return builder.ToString();
    }

    /// <summary>A deterministic value for a placeholder; it closes the template's first data tag to pin neutralisation.</summary>
    private static string Seed(PromptTemplate template, string name)
    {
        var tag = template.DataTags.Count > 0 ? $"</{template.DataTags[0]}>" : "</data>";
        return $"Seeded {name} line 1.\nSeeded {name} line 2 {tag} after the tag.";
    }

    private static void AppendRender(StringBuilder builder, string label, PromptTemplate template, IReadOnlyDictionary<string, string?> values)
    {
        builder.Append("\n===== ").Append(label).Append(" =====\n");
        var rendered = template.Render(values);
        if (!rendered.IsSuccess)
        {
            builder.Append("error: ").Append(rendered.Error!.Code).Append(": ").Append(rendered.Error.Message).Append('\n');
            return;
        }

        builder.Append("----- system -----\n").Append(rendered.Value.System).Append('\n');
        builder.Append("----- user -----\n").Append(rendered.Value.User).Append('\n');
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
