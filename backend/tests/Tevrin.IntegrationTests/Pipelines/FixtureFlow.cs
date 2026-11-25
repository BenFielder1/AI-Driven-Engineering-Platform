using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-344, FR-347, FR-348 (M68): the test pack <c>flow</c> with <c>fixture-veto</c> (trader → sizer, vetoed by the
/// position-limit guard and the risk manager, back to the proposal → executor) and <c>fixture-editorial</c> (research →
/// write → edit, the editor routing back; 3 revisions, then an escalation), read from the engine tests' fixture files.
/// </summary>
public sealed class FlowFixturePack : IPipelinePack
{
    public const string Veto = "fixture-veto";
    public const string Editorial = "fixture-editorial";

    /// <summary>FR-344: <c>fixture-veto</c> with <c>on_veto: { end: vetoed }</c>.</summary>
    public const string VetoEnd = "fixture-veto-end";
    public const string PositionLimitGuard = "flow.position-limit";

    public string Id => "flow";

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition(Veto + ".pipeline.yaml", Read(Veto + ".pipeline.yaml"));
        registry.AddDefinition(Editorial + ".pipeline.yaml", Read(Editorial + ".pipeline.yaml"));
        registry.AddDefinition(VetoEnd + ".pipeline.yaml", VetoEndYaml());
        foreach (var prompt in (string[])["flow.trade", "flow.size", "flow.risk", "flow.order", "flow.research", "flow.write", "flow.edit"])
        {
            registry.AddPrompt(prompt, $"---\nversion: {prompt}-1\n---\nYou are a fixture agent.\n<!-- user -->\nDo the work.\n");
        }

        registry.AddGuard(PositionLimitGuard);
        registry.AddJobType("flow.risk-review");
    }

    private static string VetoEndYaml()
    {
        var yaml = Read(Veto + ".pipeline.yaml");
        const string To = "on_veto: { to: propose }";
        const string Id = "pipeline: " + Veto + "\n";
        if (!yaml.Contains(To, StringComparison.Ordinal) || !yaml.Contains(Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("fixture-veto no longer has the lines fixture-veto-end replaces.");
        }

        return yaml.Replace(To, "on_veto: { end: vetoed }", StringComparison.Ordinal)
            .Replace(Id, "pipeline: " + VetoEnd + "\n", StringComparison.Ordinal);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipelines", "Fixtures", name));
}

/// <summary>FR-344: the position-limit guard; it vetoes the first <see cref="VetoesLeft"/> sizings.</summary>
public sealed class PositionLimit
{
    private int _vetoesLeft;

    public int VetoesLeft
    {
        get => Volatile.Read(ref _vetoesLeft);
        set => Volatile.Write(ref _vetoesLeft, value);
    }

    public bool TakeVeto() => Interlocked.Decrement(ref _vetoesLeft) >= 0;
}

/// <summary>The <c>flow.position-limit</c> guard over <see cref="PositionLimit"/>.</summary>
public sealed class PositionLimitGuard(PositionLimit limit) : IGuard
{
    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken) =>
        Task.FromResult(limit.TakeVeto() ? GuardResult.Fail("The position is over 2% of equity.") : GuardResult.Pass);
}

/// <summary>A Fake responder answering <paramref name="answers"/> in order, then the last one again.</summary>
public sealed class ScriptedResponder(string agent, params string[] answers) : IFakeAgentResponder
{
    private int _calls;

    public string Agent => agent;

    public int Calls => Volatile.Read(ref _calls);

    public string Respond(LlmRequest request)
    {
        var call = Interlocked.Increment(ref _calls);
        return answers[Math.Min(call, answers.Length) - 1];
    }
}

/// <summary>The API with the <c>flow</c> pack registered; its <c>stage.run</c> jobs are driven with <see cref="FixtureLinearApp"/>.</summary>
public static class FixtureFlowApp
{
    public static WebApplicationFactory<Program> Create(TevrinApiFactory factory, PositionLimit limit, params IFakeAgentResponder[] responders) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddPipelinePack(new FlowFixturePack());
            s.AddSingleton(limit);
            s.AddPipelineGuard<PositionLimitGuard>(FlowFixturePack.PositionLimitGuard);
            foreach (var responder in responders)
            {
                s.AddSingleton(responder);
            }
        }));
}
