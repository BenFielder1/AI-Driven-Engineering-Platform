using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-279: an artifact schema's <c>$ref</c>s resolve at load, or the schema is <c>DEF_SCHEMA_NOT_FOUND</c>.</summary>
public class SchemaRefTests
{
    private const string Definition = """
        pipeline: refs
        version: 1
        title: Refs
        approval_modes: [strict]
        default_approval_mode: strict
        start: [one]
        artifacts:
          out: { title: Out, format: json, schema: out.schema.json }
        stages:
          - { id: one, title: One, status: active, agent: { key: a }, output: out }
        """;

    private static List<(string Code, string Path, string Message)> Errors(string outSchema, string? otherSchema = null)
    {
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal) { ["out.schema.json"] = outSchema };
        if (otherSchema is not null)
        {
            schemas["other.schema.json"] = otherSchema;
        }

        var registry = new PipelineRegistry([new SchemaPack(schemas)], [], Array.Empty<JobHandlerRegistration>());
        return [.. registry.Errors.Select(e => (e.Error.Code, e.Error.Path, e.Error.Message))];
    }

    [Fact]
    public void FR279_a_local_ref_that_resolves_is_valid() =>
        Assert.Empty(Errors("""{ "$defs": { "name": { "type": "string" } }, "properties": { "n": { "$ref": "#/$defs/name" } } }"""));

    [Fact]
    public void FR279_a_ref_to_an_anchor_or_the_root_is_valid() =>
        Assert.Empty(Errors("""{ "$defs": { "a": { "$anchor": "item", "type": "string" } }, "items": { "$ref": "#item" }, "not": { "not": { "$ref": "#" } } }"""));

    [Fact]
    public void FR279_a_missing_local_ref_is_refused()
    {
        var (code, path, message) = Assert.Single(Errors("""{ "properties": { "n": { "$ref": "#/$defs/missing" } } }"""));
        Assert.Equal((DefinitionErrorCodes.SchemaNotFound, "artifacts.out.schema"), (code, path));
        Assert.Contains("#/$defs/missing", message, StringComparison.Ordinal);
        Assert.Contains("/properties/n", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/schema.json")]
    [InlineData("http://example.com/schema.json#/$defs/x")]
    [InlineData("urn:example:schema")]
    public void FR279_a_remote_ref_is_refused(string reference)
    {
        var (code, _, message) = Assert.Single(Errors($$"""{ "properties": { "n": { "$ref": "{{reference}}" } } }"""));
        Assert.Equal(DefinitionErrorCodes.SchemaNotFound, code);
        Assert.Contains("remote", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR279_a_ref_to_another_pack_schema_must_be_registered_and_resolve()
    {
        const string other = """{ "$defs": { "id": { "type": "integer" } } }""";
        Assert.Empty(Errors("""{ "properties": { "n": { "$ref": "other.schema.json#/$defs/id" } } }""", other));
        Assert.Single(Errors("""{ "properties": { "n": { "$ref": "other.schema.json#/$defs/nope" } } }""", other));
        Assert.Contains("doesn't register", Assert.Single(Errors("""{ "properties": { "n": { "$ref": "missing.schema.json" } } }""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR279_refs_inside_data_keywords_are_not_checked() =>
        Assert.Empty(Errors("""{ "const": { "$ref": "#/nowhere" } }"""));

    private sealed class SchemaPack(Dictionary<string, string> schemas) : IPipelinePack
    {
        public string Id => "refs";

        public void Register(IPackRegistry registry)
        {
            registry.AddDefinition("refs.pipeline.yaml", Definition);
            foreach (var (name, json) in schemas)
            {
                registry.AddSchema(name, json);
            }
        }
    }
}
