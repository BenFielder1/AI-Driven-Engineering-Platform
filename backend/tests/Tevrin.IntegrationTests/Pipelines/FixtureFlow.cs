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
/// write → edit, the editor routing back; 3 revisions, then an escalation), and FR-343 <c>fixture-parallel</c> (bull and
/// bear analysts in a parallel group, then the decision), read from the engine tests' fixture files.
/// </summary>
public sealed class FlowFixturePack : IPipelinePack
{
    public const string Veto = "fixture-veto";
    public const string Editorial = "fixture-editorial";

    /// <summary>FR-344: <c>fixture-veto</c> with <c>on_veto: { end: vetoed }</c>.</summary>
    public const string VetoEnd = "fixture-veto-end";
    public const string PositionLimitGuard = "flow.position-limit";
    public const string Parallel = "fixture-parallel";

    /// <summary>FR-343: <c>fixture-parallel</c> with the bear branch a failing handler stage (<c>on_branch_failure: fail</c>).</summary>
    public const string ParallelFail = "fixture-parallel-fail";

    /// <summary>FR-343: as <see cref="ParallelFail"/> with <c>on_branch_failure: continue</c>; the decision reads only the
    /// bull branch's outputs.</summary>
    public const string ParallelContinue = "fixture-parallel-continue";
    public const string BearDeskHandler = "flow.bear-desk";

    /// <summary>FR-345, FR-346: three forecasters fan out, then an aggregate with the spread guard.</summary>
    public const string Forecast = "fixture-forecast";

    /// <summary>§4.65: <c>fixture-forecast</c> with a context provider that leaks the base-rate forecast to the contrarian.</summary>
    public const string ForecastLeak = "fixture-forecast-leak";
    public const string SpreadGuard = "flow.forecast-spread";

    public string Id => "flow";

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition(Veto + ".pipeline.yaml", Read(Veto + ".pipeline.yaml"));
        registry.AddDefinition(Editorial + ".pipeline.yaml", Read(Editorial + ".pipeline.yaml"));
        registry.AddDefinition(VetoEnd + ".pipeline.yaml", VetoEndYaml());
        registry.AddDefinition(Parallel + ".pipeline.yaml", Read(Parallel + ".pipeline.yaml"));
        registry.AddDefinition(ParallelFail + ".pipeline.yaml", FailingBearYaml(ParallelFail, "fail"));
        registry.AddDefinition(ParallelContinue + ".pipeline.yaml", FailingBearYaml(ParallelContinue, "continue"));
        registry.AddDefinition(Forecast + ".pipeline.yaml", Read(Forecast + ".pipeline.yaml"));
        registry.AddDefinition(ForecastLeak + ".pipeline.yaml", LeakYaml());
        foreach (var forecaster in (string[])["flow.forecast", "flow.forecast-base-rate", "flow.forecast-trend", "flow.forecast-contrarian"])
        {
            registry.AddPrompt(forecaster, $"---\nversion: {forecaster}-1\n---\nYou are a forecaster.\n<!-- user -->\nForecast: {{{{QUESTION}}}}\n{{{{MARKET_NOTES?}}}}\n");
        }

        registry.AddPrompt("flow.aggregate", "---\nversion: flow.aggregate-1\n---\nYou combine forecasts.\n<!-- user -->\nQuestion: {{QUESTION}}\nForecasts:\n{{FORECAST_ALL}}\n");
        registry.AddGuard(SpreadGuard);
        foreach (var jobType in (string[])["flow.forecast-base-rate", "flow.forecast-trend", "flow.forecast-contrarian"])
        {
            registry.AddJobType(jobType);
        }
        string[] prompts =
        [
            "flow.trade", "flow.size", "flow.risk", "flow.order", "flow.research", "flow.write", "flow.edit",
            "flow.bull", "flow.bull-review", "flow.bear", "flow.decide",
        ];
        foreach (var prompt in prompts)
        {
            registry.AddPrompt(prompt, $"---\nversion: {prompt}-1\n---\nYou are a fixture agent.\n<!-- user -->\nDo the work.\n");
        }

        registry.AddGuard(PositionLimitGuard);
        registry.AddStageHandler(BearDeskHandler);
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

    private static string LeakYaml()
    {
        var yaml = Read(Forecast + ".pipeline.yaml");
        const string Id = "pipeline: " + Forecast + "\n";
        return yaml.Contains(Id, StringComparison.Ordinal)
            ? yaml.Replace(Id, "pipeline: " + ForecastLeak + "\n", StringComparison.Ordinal)
            : throw new InvalidOperationException("fixture-forecast no longer has the line fixture-forecast-leak replaces.");
    }

    private static string FailingBearYaml(string pipelineId, string onBranchFailure)
    {
        var yaml = Read(Parallel + ".pipeline.yaml");
        string[] lines =
        [
            "pipeline: " + Parallel + "\n",
            "on_branch_failure: fail",
            "            agent: { key: bear-analyst, prompt: flow.bear }\n            inputs: [ticker]\n            output: bear_case\n",
            "inputs: [bull_case, bull_risks, bear_case]",
        ];
        if (lines.Any(l => !yaml.Contains(l, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("fixture-parallel no longer has the lines its failing variants replace.");
        }

        return yaml.Replace(lines[0], "pipeline: " + pipelineId + "\n", StringComparison.Ordinal)
            .Replace(lines[1], "on_branch_failure: " + onBranchFailure, StringComparison.Ordinal)
            .Replace(lines[2], "            handler: " + BearDeskHandler + "\n", StringComparison.Ordinal)
            .Replace(lines[3], "inputs: [bull_case, bull_risks]", StringComparison.Ordinal);
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

/// <summary>FR-343: the bear desk handler of the failing <c>fixture-parallel</c> variants: it always fails.</summary>
public sealed class FailingBearDesk : IStageHandler
{
    public Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Failed, "The bear desk is closed."));
}

/// <summary>FR-346: the <c>flow.forecast-spread</c> guard: the forecasts' probabilities may differ by at most 0.25.</summary>
public sealed class ForecastSpreadGuard : IGuard
{
    public const double MaxSpread = 0.25;

    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = context.Facts.Get<AggregateListFact>()?.Items ?? [];
        var probabilities = items
            .Select(i => System.Text.Json.JsonDocument.Parse(i.Content).RootElement.GetProperty("probability").GetDouble())
            .ToList();
        if (probabilities.Count < 2)
        {
            return Task.FromResult(GuardResult.Pass);
        }

        var spread = probabilities.Max() - probabilities.Min();
        return Task.FromResult(spread > MaxSpread
            ? GuardResult.Fail(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The forecasts spread {spread:0.00}, over {MaxSpread:0.00}."))
            : GuardResult.Pass);
    }
}

/// <summary>§4.65: a scripted context provider for <c>fixture-forecast-leak</c> that hands the contrarian branch the
/// base-rate branch's stored forecast (the breach the runner must catch).</summary>
public sealed class LeakingForecastContext(Tevrin.Engine.Runtime.Persistence.IEngineDbContext db) : IAgentContextProvider
{
    public string PipelineId => FlowFixturePack.ForecastLeak;

    public async Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Branch != "forecaster-contrarian")
        {
            return [];
        }

        var runId = request.RunId;
        var sibling = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Artifacts.Where(a => a.PipelineRunId == runId && a.Type == "forecast" && a.Variant == "forecaster-base-rate"), cancellationToken);
        return sibling is null ? [] : [new AgentContextSection("market-notes", "Desk notes: " + sibling.Content)];
    }
}

/// <summary>A Fake responder that answers <paramref name="answer"/> and records every user message it was sent.</summary>
public sealed class RecordingResponder(string agent, string answer) : IFakeAgentResponder
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();

    public string Agent => agent;

    public IReadOnlyList<string> Prompts => [.. _prompts];

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _prompts.Enqueue(string.Join("\n", request.Messages.Select(m => m.Content)));
        return answer;
    }
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
            s.AddPipelineStageHandler<FailingBearDesk>(FlowFixturePack.BearDeskHandler);
            s.AddPipelineGuard<ForecastSpreadGuard>(FlowFixturePack.SpreadGuard);
            s.AddScoped<IAgentContextProvider, LeakingForecastContext>();
            foreach (var responder in responders)
            {
                s.AddSingleton(responder);
            }
        }));
}
