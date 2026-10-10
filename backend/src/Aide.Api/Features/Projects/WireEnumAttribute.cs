using System.ComponentModel.DataAnnotations;
using Aide.Domain.Common;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aide.Api.Features.Projects;

/// <summary>
/// Marks a <c>string</c> query parameter as one of an enum's snake_case wire values (<see cref="EnumStrings"/>).
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
        if (!enumType.IsEnum)
        {
            throw new ArgumentException($"{enumType.Name} is not an enum.", nameof(enumType));
        }

        EnumType = enumType;
        Values = (IReadOnlyList<string>)typeof(EnumStrings).GetMethod(nameof(EnumStrings.Values))!
            .MakeGenericMethod(enumType)
            .Invoke(null, null)!;
    }

    public Type EnumType { get; }

    public IReadOnlyList<string> Values { get; }

    public override bool IsValid(object? value) => value is null || (value is string s && Values.Contains(s, StringComparer.Ordinal));

    public override string FormatErrorMessage(string name) => $"{name} must be one of: {string.Join(", ", Values)}.";
}

/// <summary>Adds the allowed values of a <see cref="WireEnumAttribute"/> parameter to its OpenAPI schema.</summary>
internal sealed class WireEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.ParameterDescription?.ParameterDescriptor is IParameterInfoParameterDescriptor { ParameterInfo: var parameter }
            && parameter.GetCustomAttributes(typeof(WireEnumAttribute), inherit: false).FirstOrDefault() is WireEnumAttribute wireEnum)
        {
            schema.Enum = [.. wireEnum.Values.Select(v => (System.Text.Json.Nodes.JsonNode)v)];
        }

        return Task.CompletedTask;
    }
}
