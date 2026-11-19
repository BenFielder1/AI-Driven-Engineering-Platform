using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-295, D-0.7.0-33: every agent stage's prompt must resolve to one its pack registered, and be renderable.</summary>
public class PromptReadinessTests
{
    private static Dictionary<string, string> Prompts(params (string Name, string Template)[] changes)
    {
        var prompts = new Dictionary<string, string>(FixturePack.PromptTemplates, StringComparer.Ordinal);
        foreach (var (name, template) in changes)
        {
            prompts[name] = template;
        }

        return prompts;
    }

    private static List<(string Code, string Path)> Errors(PackCatalog catalog, PipelineDefinition? definition = null) =>
        [.. PipelineDefinitionValidator.Validate(definition ?? Fixtures.Load("fixture-review.pipeline.yaml"), catalog).Select(e => (e.Code, e.Path))];

    private static string Template(string user) => $"---\nversion: t1\n---\nSystem.\n<!-- user -->\n{user}";

    [Fact]
    public void FR295_the_fixture_prompts_are_ready() => Assert.Empty(Errors(FixturePack.Catalog with { Prompts = FixturePack.PromptTemplates }));

    [Fact]
    public void FR295_DEF_UNKNOWN_PROMPT_an_agent_stage_prompt_the_pack_did_not_register()
    {
        var prompts = Prompts();
        prompts.Remove(FixturePack.SummarisePrompt);

        Assert.Equal([(DefinitionErrorCodes.UnknownPrompt, "stages[2].agent.prompt")], Errors(FixturePack.Catalog with { Prompts = prompts }));
    }

    [Fact]
    public void FR295_DEF_UNKNOWN_PROMPT_a_defaulted_prompt_is_the_agent_key()
    {
        var definition = Fixtures.Load("fixture-review.pipeline.yaml");
        var draft = definition.Stages[0] with { Agent = definition.Stages[0].Agent! with { Prompt = null } };
        definition = definition with { Stages = [draft, .. definition.Stages.Skip(1)] };

        Assert.Equal([(DefinitionErrorCodes.UnknownPrompt, "stages[0].agent.key")], Errors(FixturePack.Catalog with { Prompts = Prompts() }, definition));
    }

    [Theory]
    [InlineData("{{FEEDBACK}}")]
    [InlineData("{{UNKNOWN}}")]
    [InlineData("{{NOTES}}")]
    [InlineData("{{REPORT}}")]
    public void FR295_DEF_UNKNOWN_PROMPT_a_required_placeholder_the_stage_cannot_fill(string placeholder) =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownPrompt, "stages[1].agent.prompt")],
            Errors(FixturePack.Catalog with { Prompts = Prompts((FixturePack.CheckPrompt, Template($"<draft>{{{{DRAFT}}}}</draft> {placeholder}"))) }));

    [Fact]
    public void FR295_a_required_placeholder_of_a_registered_context_name_is_ready() =>
        Assert.Empty(Errors(FixturePack.Catalog with
        {
            Prompts = Prompts((FixturePack.CheckPrompt, Template("{{DRAFT}} {{NOTES}} {{FEEDBACK?}}"))),
            ContextNames = ["notes"],
        }));

    [Fact]
    public void FR295_DEF_UNKNOWN_PROMPT_a_template_that_does_not_parse() =>
        Assert.Equal(
            [(DefinitionErrorCodes.UnknownPrompt, "stages[1].agent.prompt")],
            Errors(FixturePack.Catalog with { Prompts = Prompts((FixturePack.CheckPrompt, "no front matter")) }));

    [Fact]
    public void FR295_a_pack_that_registers_no_prompts_is_not_checked() => Assert.Empty(Errors(FixturePack.Catalog));

    [Fact]
    public void FR295_the_registry_checks_a_pack_that_registers_prompts_and_the_prompt_source_finds_them()
    {
        var services = new ServiceCollection();
        services.AddPipelineRegistry();
        services.AddPipelinePack(new FixturePack(yaml => yaml.Replace("prompt: fixture.summarise", "prompt: fixture.missing", StringComparison.Ordinal)));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<PipelineRegistry>();

        var error = Assert.Single(registry.Errors).Error;
        Assert.Equal((DefinitionErrorCodes.UnknownPrompt, "stages[2].agent.prompt"), (error.Code, error.Path));
        var prompts = new PackPromptSource(registry);
        Assert.StartsWith("---", prompts.Find("fixture", "fixture-review", FixturePack.DraftPrompt), StringComparison.Ordinal);
        Assert.Null(prompts.Find("fixture", "fixture-review", "fixture.missing"));
        Assert.Null(prompts.Find("other", "fixture-review", FixturePack.DraftPrompt));
    }
}
