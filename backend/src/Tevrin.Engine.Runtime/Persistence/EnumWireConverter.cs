using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>Stores a domain enum as its snake_case wire value (<see cref="EnumStrings"/>), e.g. <c>in_progress</c>.</summary>
public sealed class EnumWireConverter<TEnum>()
    : ValueConverter<TEnum, string>(v => EnumStrings.ToWire(v), s => EnumStrings.Parse<TEnum>(s))
    where TEnum : struct, Enum;
