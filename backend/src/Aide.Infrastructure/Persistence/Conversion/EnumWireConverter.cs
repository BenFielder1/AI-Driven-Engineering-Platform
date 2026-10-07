using Aide.Domain.Common;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aide.Infrastructure.Persistence.Conversion;

/// <summary>Stores a domain enum as its snake_case wire value (<see cref="EnumStrings"/>), e.g. <c>in_progress</c>.</summary>
public sealed class EnumWireConverter<TEnum>()
    : ValueConverter<TEnum, string>(v => EnumStrings.ToWire(v), s => EnumStrings.Parse<TEnum>(s))
    where TEnum : struct, Enum;
