using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Aide.Api.Jobs;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Tevrin.Engine.Domain.Common;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Features.Projects;

/// <summary>
/// Marks a <c>string</c> query parameter as one of an enum's snake_case wire values (<see cref="EnumStrings"/>), or a
/// <c>string</c> parameter or DTO property as one of a key set's values (FR-269–FR-271: a class with a static
/// <c>All</c> list, such as <see cref="SdlcAgents"/>).
/// Minimal APIs bind enum query values with <c>Enum.TryParse</c> (PascalCase names), which doesn't accept wire values
/// such as <c>rolled_back</c>, so these parameters bind as strings: this attribute validates them at the edge
/// (400 <c>VALIDATION</c>) and <see cref="WireEnumSchemaTransformer"/> documents the allowed values.
/// Handlers parse with <see cref="EnumStrings.Parse{TEnum}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class WireEnumAttribute : ValidationAttribute
{
    public WireEnumAttribute(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        EnumType = enumType;
        Values = enumType.IsEnum
            ? (IReadOnlyList<string>)typeof(EnumStrings).GetMethod(nameof(EnumStrings.Values))!
                .MakeGenericMethod(enumType)
                .Invoke(null, null)!
            : enumType.GetProperty("All", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IReadOnlyList<string>
                ?? throw new ArgumentException($"{enumType.Name} is neither an enum nor a key set with a static All list.", nameof(enumType));
    }

    /// <summary>The enum, or the key set class (FR-269–FR-271).</summary>
    public Type EnumType { get; }

    public IReadOnlyList<string> Values { get; }

    public override bool IsValid(object? value) => value is null || (value is string s && Values.Contains(s, StringComparer.Ordinal));

    public override string FormatErrorMessage(string name) => $"{name} must be one of: {string.Join(", ", Values)}.";
}

/// <summary>
/// Documents <see cref="WireEnumAttribute"/> values. A parameter gets its allowed values inline. A DTO property marked
/// with a key set (FR-269–FR-271, D-0.7.0-24) is documented as the component schema the 0.6.1 enum had
/// (<see cref="KeySetSchemas"/>: same name, values, order and description), so <c>openapi/api.json</c> and the generated
/// client are unchanged.
/// </summary>
internal sealed class WireEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>Microsoft.AspNetCore.OpenApi's schema metadata: a schema with this id is moved to
    /// <c>components/schemas</c> and replaced by a reference to it.</summary>
    private const string SchemaIdKey = "x-schema-id";

    /// <summary>Microsoft.AspNetCore.OpenApi's schema metadata: the reference of a nullable property is wrapped in
    /// <c>oneOf: [null, $ref]</c>.</summary>
    private const string NullablePropertyKey = "x-is-nullable-property";

    /// <summary>
    /// The component schema of each key set: the name and description of the 0.6.1 enum it replaces, and whether that
    /// enum's component listed <c>null</c> among its values (an enum only ever used nullable, such as
    /// <c>AgentJobTrigger</c>, FR-273).
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, (string Name, string? Description, bool NullInEnum)> KeySetSchemas =
        new Dictionary<Type, (string Name, string? Description, bool NullInEnum)>
        {
            [typeof(SdlcAgents)] = ("AgentKind", "The cognitive agents (§1, FR-23), plus the Repository Analyst (FR-70) and the Stack Analyst (FR-180).", false),
            [typeof(SdlcArtifacts)] = ("ArtifactType", null, false),
            [typeof(SdlcGates)] = ("GateType", null, false),
            [typeof(AgentJobTriggerWire)] = ("AgentJobTrigger", null, true),
        };

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.ParameterDescription?.ParameterDescriptor is IParameterInfoParameterDescriptor { ParameterInfo: var parameter }
            && parameter.GetCustomAttributes(typeof(WireEnumAttribute), inherit: false).FirstOrDefault() is WireEnumAttribute wireEnum)
        {
            schema.Enum = [.. wireEnum.Values.Select(v => (System.Text.Json.Nodes.JsonNode)v)];
        }
        else if (context.JsonPropertyInfo is { } property
            && PropertyAttribute(property.DeclaringType, property.AttributeProvider) is { } keys
            && KeySetSchemas.TryGetValue(keys.EnumType, out var component))
        {
            // This relies on Microsoft.AspNetCore.OpenApi internals: it hoists a schema carrying the x-schema-id metadata
            // into components/schemas (replacing it with a $ref), and wraps the $ref of one carrying
            // x-is-nullable-property in oneOf [null, $ref]. Neither key is a public API; if a package update changes
            // them, the regenerated openapi/api.json differs and scripts/ci/check-generated.sh fails CI.
            var nullable = schema.Type is { } type && type.HasFlag(JsonSchemaType.Null);
            schema.Type = null;
            schema.Format = null;
            schema.Enum = [.. keys.Values.Select(v => (System.Text.Json.Nodes.JsonNode)v)];
            if (component.NullInEnum)
            {
                schema.Enum.Add(null!);
            }

            schema.Description = component.Description;
            schema.Metadata ??= new Dictionary<string, object>();
            schema.Metadata[SchemaIdKey] = component.Name;
            if (nullable)
            {
                schema.Metadata[NullablePropertyKey] = true;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>The attribute on a DTO property, or on the record's primary-constructor parameter of the same name.</summary>
    private static WireEnumAttribute? PropertyAttribute(Type declaringType, ICustomAttributeProvider? provider)
    {
        if (provider is not PropertyInfo property)
        {
            return null;
        }

        if (property.GetCustomAttribute<WireEnumAttribute>() is { } onProperty)
        {
            return onProperty;
        }

        return declaringType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal))
            .Select(p => p.GetCustomAttribute<WireEnumAttribute>())
            .FirstOrDefault(a => a is not null);
    }
}
