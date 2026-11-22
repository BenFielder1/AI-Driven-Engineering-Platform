using System.Reflection;
using System.Reflection.Emit;
using Aide.Api.Agents;
using Aide.Domain;
using Aide.Infrastructure;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Llm;

namespace Aide.UnitTests.Architecture;

/// <summary>
/// CLAUDE.md rule 10, FR-320: agent (LLM) calls go through the engine runner. No <c>Aide.*</c> type takes or holds a
/// provider (<see cref="ILlmProvider"/>, <see cref="IToolCallingLlmProvider"/>), calls one or the tool loop
/// (<see cref="LlmToolLoop"/>), or gets a provider or key to make a call (<see cref="ILlmResolver.ProviderFor"/>,
/// <see cref="ILlmResolver.KeyAsync"/>). Resolving which model a project uses (<see cref="ILlmResolver.ResolveAsync"/>,
/// e.g. FR-172's source-item budget) makes no call and stays allowed. The exceptions are the provider abstraction's own implementations (the resolver and the providers) and
/// an explicit list of the SDLC's engine hooks (<c>Hooks</c>: only the types that resolve the engine runner's provider or key). The check reads
/// constructor parameters, fields and the IL of every method, compiler-generated closures and state machines included.
/// </summary>
public class AgentCallBoundaryTests
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(DomainAssembly).Assembly,
        typeof(DependencyInjection).Assembly,
        typeof(SdlcAgentExtension).Assembly,
    ];

    private static readonly Type[] ProviderTypes = [typeof(ILlmProvider), typeof(IToolCallingLlmProvider), typeof(LlmToolLoop)];

    private static readonly string[] CallingResolverMembers = [nameof(ILlmResolver.ProviderFor), nameof(ILlmResolver.KeyAsync)];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void FR320_no_Aide_type_outside_the_engine_hooks_calls_an_LLM_directly()
    {
        var offending = Assemblies
            .SelectMany(LoadableTypes)
            .Where(t => !Allowed(Owner(t)))
            .SelectMany(t => Uses(t).Select(use => $"{t.FullName}: {use}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void FR320_the_check_sees_a_provider_call_in_an_async_method()
    {
        // The probe below calls a provider from an async method (a compiler-generated state machine).
        var uses = typeof(Probe).GetNestedTypes(Declared).Append(typeof(Probe)).SelectMany(Uses).ToList();

        Assert.Contains(uses, u => u.Contains(nameof(ILlmProvider.CompleteAsync), StringComparison.Ordinal));
        Assert.Contains(uses, u => u.Contains(nameof(ILlmResolver.ProviderFor), StringComparison.Ordinal));
        Assert.DoesNotContain(uses, u => u.Contains(nameof(ILlmResolver.ResolveAsync), StringComparison.Ordinal));
    }

    /// <summary>
    /// The SDLC engine hooks that may resolve a provider or key for the engine runner's call (all partials of a type are one
    /// type). Any other type, including <see cref="SdlcAgentStageJob"/>, the stage locator and the context providers, fails.
    /// </summary>
    private static readonly Type[] Hooks = [typeof(SdlcAgentExtension)];

    /// <summary>The provider abstraction's own types, and the SDLC's engine hooks.</summary>
    private static bool Allowed(Type type) =>
        typeof(ILlmProvider).IsAssignableFrom(type)
        || typeof(ILlmResolver).IsAssignableFrom(type)
        || Hooks.Contains(type);

    /// <summary>The outermost declaring type (a closure or state machine belongs to the type that declared it).</summary>
    private static Type Owner(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static bool IsProvider(Type? type) => type is not null && ProviderTypes.Any(t => t.IsAssignableFrom(type) || t == type);

    private static bool MakesCall(MethodBase called) =>
        IsProvider(called.DeclaringType)
        || (called.DeclaringType is { } declaring && typeof(ILlmResolver).IsAssignableFrom(declaring) && CallingResolverMembers.Contains(called.Name));

    /// <summary>Where <paramref name="type"/> takes or calls an LLM type: constructor parameters, fields and called members.</summary>
    private static IEnumerable<string> Uses(Type type)
    {
        foreach (var constructor in type.GetConstructors(Declared))
        {
            foreach (var parameter in constructor.GetParameters().Where(p => IsProvider(p.ParameterType)))
            {
                yield return $"takes {parameter.ParameterType.Name}";
            }
        }

        foreach (var field in type.GetFields(Declared).Where(f => IsProvider(f.FieldType)))
        {
            yield return $"holds {field.FieldType.Name}";
        }

        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            foreach (var called in Calls(method).Where(MakesCall))
            {
                yield return $"calls {called.DeclaringType!.Name}.{called.Name}";
            }
        }
    }

    /// <summary>The methods <paramref name="method"/>'s IL calls or constructs (<c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>).</summary>
    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            il = null;
        }

        if (il is null)
        {
            yield break;
        }

        var typeArgs = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE && i + 1 < il.Length ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            if (!OpCodesByValue.TryGetValue(value, out var op))
            {
                // Never go blind on a body: an opcode the table lacks is a failure, not the end of the scan.
                throw new InvalidOperationException($"Unknown IL opcode 0x{value:X4} at offset {i} in {method.DeclaringType?.FullName}.{method.Name}.");
            }

            if (op.OperandType == OperandType.InlineMethod && i + 4 <= il.Length)
            {
                // A call the check can't resolve is a failure: swallowing it would hide the call.
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs)
                    ?? throw new InvalidOperationException($"Unresolved call token at offset {i} in {method.DeclaringType?.FullName}.{method.Name}.");
            }

            i += OperandSize(op, il, i);
        }
    }

    private static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
        _ => 4,
    };

    /// <summary>A direct LLM call the check must see (only compiled, never run).</summary>
    private sealed class Probe(ILlmResolver resolver)
    {
        public async Task<string> CallAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            _ = await resolver.ResolveAsync(Guid.Empty, "probe", cancellationToken);
            var completion = await resolver.ProviderFor(LlmProvider.Fake).CompleteAsync(request, cancellationToken);
            return completion.Text;
        }
    }
}
