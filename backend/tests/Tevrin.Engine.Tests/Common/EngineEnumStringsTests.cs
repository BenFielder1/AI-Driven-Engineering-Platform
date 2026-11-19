using System.Reflection;
using Tevrin.Engine.Domain;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Tests.Common;

/// <summary>
/// FR-264–FR-268: the enums that moved to the engine keep their stored and wire values (snake_case of the member name,
/// <see cref="EnumStrings"/>). The engine-assembly counterpart of Aide.UnitTests' round-trip test.
/// </summary>
public class EngineEnumStringsTests
{
    [Fact]
    public void FR264_wire_values_round_trip_for_every_engine_domain_enum()
    {
        var enums = typeof(EngineDomainAssembly).Assembly.GetTypes().Where(t => t.IsEnum && t.IsPublic).ToList();
        var roundTrip = typeof(EngineEnumStringsTests).GetMethod(nameof(RoundTrip), BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.NotEmpty(enums);
        foreach (var type in enums)
        {
            roundTrip.MakeGenericMethod(type).Invoke(null, null);
        }
    }

    [Fact]
    public void FR264_moved_enums_keep_their_0_6_1_wire_values()
    {
        Assert.Equal(["queued", "running", "succeeded", "failed", "dead", "cancelled", "retried"], EnumStrings.Values<JobStatus>());
        Assert.Equal(["pending", "approved", "rejected", "cancelled"], EnumStrings.Values<GateStatus>());
        Assert.Equal(["strict", "advisory", "autonomous"], EnumStrings.Values<ApprovalMode>());
        Assert.Equal(["anthropic", "openai", "ollama", "fake"], EnumStrings.Values<LlmProvider>());
    }

    private static void RoundTrip<TEnum>()
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var wire = value.ToWire();
            Assert.Matches("^[a-z]+(_[a-z]+)*$", wire);
            Assert.Equal(value, EnumStrings.Parse<TEnum>(wire));
        }
    }
}
