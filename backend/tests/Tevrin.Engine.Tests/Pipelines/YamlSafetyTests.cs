using System.Text;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-275: safe parsing: no type tags, bounded size and alias expansion.</summary>
public class YamlSafetyTests
{
    [Theory]
    [InlineData("pipeline: !!python/object:os.system x\n")]
    [InlineData("pipeline: !custom x\n")]
    [InlineData("pipeline: !!str x\n")]
    [InlineData("stages: !!seq []\n")]
    [InlineData("--- !map\npipeline: x\n")]
    public void FR275_a_type_tag_is_refused(string yaml)
    {
        var result = PipelineDefinitionLoader.Parse(yaml);
        Assert.Null(result.Definition);
        var error = Assert.Single(result.Errors);
        Assert.Equal((DefinitionErrorCodes.Parse, "$"), (error.Code, error.Path));
        Assert.Contains("tags", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR275_an_alias_bomb_is_refused()
    {
        var yaml = new StringBuilder("a0: &a0 [\"lol\", \"lol\", \"lol\", \"lol\", \"lol\", \"lol\", \"lol\", \"lol\", \"lol\"]\n");
        for (var i = 1; i < 10; i++)
        {
            yaml.Append(CultureInvariant($"a{i}: &a{i} [*a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}]\n"));
        }

        var started = DateTime.UtcNow;
        var result = PipelineDefinitionLoader.Parse(yaml.ToString());

        Assert.Null(result.Definition);
        Assert.Contains("aliases expand", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FR275_small_aliases_are_allowed()
    {
        var yaml = DefinitionYaml.Edit("approval_modes: [strict, advisory]", "approval_modes: &modes [strict, advisory]");
        yaml = DefinitionYaml.Edit("modes: [strict]", "modes: *modes", yaml);
        var result = PipelineDefinitionLoader.ParseAndValidate(yaml, DefinitionYaml.Catalog);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Definition!.FindStage("summarise")!.Gate!.Modes.Count);
    }

    [Fact]
    public void FR275_an_alias_to_an_unknown_anchor_is_refused() =>
        Assert.Equal("$", Assert.Single(PipelineDefinitionLoader.Parse("start: *nowhere\n").Errors).Path);

    [Fact]
    public void FR275_a_file_over_1_mb_is_refused()
    {
        var yaml = "title: " + new string('x', 1024 * 1024) + "\n";
        var error = Assert.Single(PipelineDefinitionLoader.Parse(yaml).Errors);
        Assert.Equal((DefinitionErrorCodes.Parse, "$"), (error.Code, error.Path));
    }

    [Fact]
    public void FR275_deep_nesting_is_refused()
    {
        var yaml = "title: " + new string('[', 200) + new string(']', 200) + "\n";
        Assert.Contains("nested", Assert.Single(PipelineDefinitionLoader.Parse(yaml).Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR275_more_than_one_document_is_refused() =>
        Assert.Equal("$", Assert.Single(PipelineDefinitionLoader.Parse("pipeline: a\n---\npipeline: b\n").Errors).Path);

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);
}
