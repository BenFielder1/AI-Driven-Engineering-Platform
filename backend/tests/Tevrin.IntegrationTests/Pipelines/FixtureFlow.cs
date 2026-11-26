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
/// bear analysts in a parallel group, then the decision), FR-345, FR-346 <c>fixture-forecast</c> and FR-349
/// <c>fixture-variants</c>, read from the engine tests' fixture files.
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

    /// <summary>FR-349: three headline variants picked at a <c>variant_select</c> gate, then two tagline variants a judge picks.</summary>
    public const string Variants = "fixture-variants";

    /// <summary>FR-350: a writer stage with a fact-checker validator agent (retry with feedback, 2 attempts, then fail).</summary>
    public const string FactCheck = "fixture-factcheck";

    /// <summary>FR-351: a worker with its agent feedback injected, then a reviewer whose lessons become worker feedback.</summary>
    public const string Postmortem = "fixture-postmortem";

    /// <summary>FR-355: a scorer mapped over transcripts (at most 8 at a time, 5% may fail), then a summariser.</summary>
    public const string Qa = "fixture-qa";

    /// <summary>FR-355: <c>fixture-qa</c> with a handler scoring each item and a handler tallying the scores.</summary>
    public const string QaHandlers = "fixture-qa-handlers";

    /// <summary>FR-355: <see cref="QaHandlers"/> with <c>on_item_failure: fail</c>.</summary>
    public const string QaFail = "fixture-qa-fail";
    public const string QaScoreHandler = "flow.qa-score-item";

    /// <summary>FR-355: <c>fixture-qa</c> whose scorer escalates an output the score guard refuses (a zero score).</summary>
    public const string QaEscalate = "fixture-qa-escalate";
    public const string QaScoreGuard = "flow.qa-score-valid";
    public const string QaTallyHandler = "flow.qa-tally";

    /// <summary>FR-356: a customer simulator and a support agent simulator talk until a reply says "goodbye" (6 turns at most).</summary>
    public const string CallSim = "fixture-callsim";

    /// <summary>FR-356: <c>fixture-callsim</c> stopped by the <see cref="CallResolvedGuard"/> instead of the phrase.</summary>
    public const string CallSimGuard = "fixture-callsim-guard";
    public const string CallResolvedGuard = "flow.call-resolved";

    /// <summary>FR-356: the persona prompts (the same as the engine tests', which check that they validate).</summary>
    public const string CallSimCustomerPrompt =
        "---\nversion: flow.callsim-customer-1\n---\nYou are a customer calling support.\n<!-- user -->\nScenario: {{SCENARIO}}\nYou are the {{ROLE}}.\nThe call so far:\n{{TRANSCRIPT}}\n";

    public const string CallSimSupportPrompt =
        "---\nversion: flow.callsim-support-1\n---\nYou are a support agent.\n<!-- user -->\nScenario: {{SCENARIO}}\nYou are the {{ROLE}}.\nThe call so far:\n{{TRANSCRIPT}}\n";

    /// <summary>FR-357: a weekly trend report over the final scores of the last 7 days' <c>fixture-qa</c> runs.</summary>
    public const string Trends = "fixture-trends";

    /// <summary>FR-357: <c>fixture-trends</c> with a <see cref="TrendsRecapStage"/> follow-up a day after completion that
    /// reads this pipeline's own earlier reports (so the current, completed run must be left out).</summary>
    public const string TrendsRecap = "fixture-trends-recap";
    public const string TrendsRecapStage = "recap";

    /// <summary>FR-357: <c>fixture-trends</c> with the <see cref="TrendTallyHandler"/> instead of the analyst.</summary>
    public const string TrendsHandler = "fixture-trends-handler";
    public const string TrendTallyHandler = "flow.trend-tally";

    /// <summary>FR-357: the analyst's prompt (the same as the engine tests', which check that it validates).</summary>
    public const string TrendsPrompt =
        "---\nversion: flow.trends-1\n---\nYou write weekly trend reports.\n<!-- user -->\nScores of recent runs, newest first:\n{{SCORES_RUNS}}\n";

    public const string TrendsRecapPrompt =
        "---\nversion: flow.trends-recap-1\n---\nYou recap trend reports.\n<!-- user -->\nEarlier reports:\n{{TREND_REPORT_RUNS?}}\n";

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
        registry.AddDefinition(Variants + ".pipeline.yaml", Read(Variants + ".pipeline.yaml"));
        registry.AddDefinition(FactCheck + ".pipeline.yaml", Read(FactCheck + ".pipeline.yaml"));
        registry.AddDefinition(Postmortem + ".pipeline.yaml", Read(Postmortem + ".pipeline.yaml"));
        registry.AddDefinition(Qa + ".pipeline.yaml", Read(Qa + ".pipeline.yaml"));
        registry.AddDefinition(QaHandlers + ".pipeline.yaml", QaHandlerYaml(QaHandlers, "on_item_failure: skip"));
        registry.AddDefinition(QaFail + ".pipeline.yaml", QaHandlerYaml(QaFail, "on_item_failure: fail"));
        registry.AddDefinition(QaEscalate + ".pipeline.yaml", QaEscalateYaml());
        registry.AddGuard(QaScoreGuard);
        registry.AddDefinition(CallSim + ".pipeline.yaml", Read(CallSim + ".pipeline.yaml"));
        registry.AddDefinition(CallSimGuard + ".pipeline.yaml", CallSimGuardYaml());
        registry.AddPrompt("flow.callsim-customer", CallSimCustomerPrompt);
        registry.AddPrompt("flow.callsim-support", CallSimSupportPrompt);
        registry.AddJobType("flow.callsim-customer");
        registry.AddJobType("flow.callsim-support");
        registry.AddGuard(CallResolvedGuard);
        registry.AddDefinition(Trends + ".pipeline.yaml", Read(Trends + ".pipeline.yaml"));
        registry.AddDefinition(TrendsRecap + ".pipeline.yaml", TrendsRecapYaml());
        registry.AddDefinition(TrendsHandler + ".pipeline.yaml", TrendsHandlerYaml());
        registry.AddPrompt("flow.trends", TrendsPrompt);
        registry.AddPrompt("flow.trends-recap", TrendsRecapPrompt);
        registry.AddStageHandler(TrendTallyHandler);
        registry.AddPrompt("flow.qa-score", "---\nversion: flow.qa-score-1\n---\nYou score calls.\n<!-- user -->\nTranscript {{MAP_INDEX}}:\n{{MAP_ITEM}}\n");
        registry.AddPrompt("flow.qa-summarise", "---\nversion: flow.qa-summarise-1\n---\nYou summarise scores.\n<!-- user -->\nScores:\n{{SCORES_ALL}}\n");
        registry.AddJobType("flow.qa-score");
        registry.AddJobType("flow.qa-summarise");
        registry.AddStageHandler(QaScoreHandler);
        registry.AddStageHandler(QaTallyHandler);
        registry.AddPrompt("flow.pm-work", "---\nversion: flow.pm-work-1\n---\nYou do tasks.\n<!-- user -->\nTask: {{TASK}}\nLessons from earlier runs:\n{{AGENT_FEEDBACK?}}\n");
        registry.AddPrompt("flow.pm-review", "---\nversion: flow.pm-review-1\n---\nYou write postmortems.\n<!-- user -->\nResult: {{RESULT}}\n");
        registry.AddPrompt("flow.pm-grade", "---\nversion: flow.pm-grade-1\n---\nYou grade results.\n<!-- user -->\nResult: {{RESULT}}\nPostmortem: {{POSTMORTEM}}\nOutcome scores:\n{{OUTCOME_SCORES}}\nLabel: {{VARIANT}}\n");
        registry.AddPrompt("flow.factwrite", "---\nversion: flow.factwrite-1\n---\nYou write articles.\n<!-- user -->\nTopic: {{TOPIC}}\n");
        registry.AddPrompt("flow.fact-check", "---\nversion: flow.fact-check-1\n---\nYou check facts.\n<!-- user -->\nArticle:\n{{OUTPUT}}\n");
        registry.AddJobType("flow.fact-check");
        registry.AddPrompt("flow.headline", "---\nversion: flow.headline-1\n---\nYou write headlines.\n<!-- user -->\nProduct: {{PRODUCT}}\nWrite variant {{VARIANT}}.\n");
        registry.AddPrompt("flow.tagline", "---\nversion: flow.tagline-1\n---\nYou write taglines.\n<!-- user -->\nProduct: {{PRODUCT}}\nHeadline: {{HEADLINE}}\nWrite variant {{VARIANT}}.\n");
        registry.AddPrompt("flow.judge", "---\nversion: flow.judge-1\n---\nYou pick the best tagline.\n<!-- user -->\nTaglines:\n{{TAGLINE_ALL?}}\n");
        registry.AddJobType("flow.judge");
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

    private static string QaHandlerYaml(string pipelineId, string onItemFailure)
    {
        var yaml = Read(Qa + ".pipeline.yaml");
        // The scorer and summariser bindings go too: their prompts need {{MAP_ITEM}} and {{SCORES_ALL}}, which only the map's
        // and reduce's own agents get, so the validator would refuse the definition (and it would never register).
        string[] lines =
        [
            "pipeline: " + Qa + "\n", "agent: qa-scorer, max_parallel", "reduce: { agent: qa-summariser }", "on_item_failure: skip",
            "    agents:\n      - { key: qa-scorer, job_type: flow.qa-score, prompt: flow.qa-score }\n      - { key: qa-summariser, job_type: flow.qa-summarise, prompt: flow.qa-summarise }\n",
        ];
        if (lines.Any(l => !yaml.Contains(l, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("fixture-qa no longer has the lines its handler variants replace.");
        }

        return yaml.Replace(lines[0], "pipeline: " + pipelineId + "\n", StringComparison.Ordinal)
            .Replace(lines[1], "handler: " + QaScoreHandler + ", max_parallel", StringComparison.Ordinal)
            .Replace(lines[2], "reduce: { handler: " + QaTallyHandler + " }", StringComparison.Ordinal)
            .Replace(lines[3], onItemFailure, StringComparison.Ordinal)
            .Replace(lines[4], string.Empty, StringComparison.Ordinal);
    }

    private static string CallSimGuardYaml()
    {
        var yaml = Read(CallSim + ".pipeline.yaml");
        // Only the stop changes, so the prompts still validate (ConversationRulesTests checks this edit with them).
        string[] lines = ["pipeline: " + CallSim + "\n", "stop: { phrase: goodbye }"];
        if (lines.Any(l => !yaml.Contains(l, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("fixture-callsim no longer has the lines fixture-callsim-guard replaces.");
        }

        return yaml.Replace(lines[0], "pipeline: " + CallSimGuard + "\n", StringComparison.Ordinal)
            .Replace(lines[1], "stop: { guard: " + CallResolvedGuard + " }", StringComparison.Ordinal);
    }

    private static string TrendsRecapYaml()
    {
        var yaml = Read(Trends + ".pipeline.yaml");
        string[] lines =
        [
            "pipeline: " + Trends + "\n",
            "  trend_report: { title: Trend report, format: markdown, approval: auto_lock, cascade: none }\n",
            "    output: trend_report\n",
        ];
        if (lines.Any(l => !yaml.Contains(l, StringComparison.Ordinal)) || !yaml.EndsWith(lines[2], StringComparison.Ordinal))
        {
            throw new InvalidOperationException("fixture-trends no longer has the lines fixture-trends-recap replaces.");
        }

        return yaml.Replace(lines[0], "pipeline: " + TrendsRecap + "\n", StringComparison.Ordinal)
            .Replace(lines[1], lines[1] + "  recap: { title: Recap, format: markdown, approval: auto_lock, cascade: none }\n", StringComparison.Ordinal)
            + "\n  - id: " + TrendsRecapStage + "\n"
            + "    title: Recap the earlier reports\n"
            + "    status: reporting\n"
            + "    agent: { key: trend-recapper, prompt: flow.trends-recap }\n"
            + "    inputs:\n"
            + "      - from_runs: { pipeline: " + TrendsRecap + ", artifact: trend_report, window: 7d }\n"
            + "    output: recap\n"
            + "\nfollowups:\n"
            + "  - { stage: " + TrendsRecapStage + ", after: [1d] }\n";
    }

    private static string TrendsHandlerYaml()
    {
        var yaml = Read(Trends + ".pipeline.yaml");
        string[] lines = ["pipeline: " + Trends + "\n", "    agent: { key: trend-analyst, prompt: flow.trends }\n", "    output: trend_report\n"];
        if (lines.Any(l => !yaml.Contains(l, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("fixture-trends no longer has the lines fixture-trends-handler replaces.");
        }

        return yaml.Replace(lines[0], "pipeline: " + TrendsHandler + "\n", StringComparison.Ordinal)
            .Replace(lines[1], "    handler: " + TrendTallyHandler + "\n", StringComparison.Ordinal)
            .Replace(lines[2], string.Empty, StringComparison.Ordinal);
    }

    private static string QaEscalateYaml()
    {
        var yaml = Read(Qa + ".pipeline.yaml");
        string[] lines = ["pipeline: " + Qa + "\n", "prompt: flow.qa-score }"];
        if (lines.Any(l => !yaml.Contains(l, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("fixture-qa no longer has the lines fixture-qa-escalate replaces.");
        }

        return yaml.Replace(lines[0], "pipeline: " + QaEscalate + "\n", StringComparison.Ordinal)
            .Replace(
                lines[1],
                "prompt: flow.qa-score, validate: [{ guard: " + QaScoreGuard + " }], on_invalid: { retry_with_feedback: true, max_attempts: 1, then: escalate } }",
                StringComparison.Ordinal);
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

/// <summary>FR-355: what the <c>fixture-qa</c> handlers saw: the items scored and the lists tallied.</summary>
public sealed class QaLog
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _scored = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<AggregateItem>> _tallies = new();

    public IReadOnlyList<int> Scored => [.. _scored];

    public IReadOnlyList<IReadOnlyList<AggregateItem>> Tallies => [.. _tallies];

    public void Score(int index) => _scored.Enqueue(index);

    public void Tally(IReadOnlyList<AggregateItem> items) => _tallies.Enqueue(items);
}

/// <summary>FR-355: the <c>flow.qa-score-item</c> handler: it scores one transcript, and fails one marked <c>"fail": true</c>.</summary>
public sealed class QaScoreItem(QaLog log) : IStageHandler
{
    public Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var item = context.Facts.Get<MapItemFact>() ?? throw new InvalidOperationException("No map item.");
        log.Score(item.Index);
        using var doc = System.Text.Json.JsonDocument.Parse(item.Item);
        if (doc.RootElement.TryGetProperty("fail", out var fail) && fail.GetBoolean())
        {
            return Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Failed, $"Transcript {item.Index} is unreadable."));
        }

        var output = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"item\": {item.Index}, \"score\": 4}}");
        return Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Completed) { Output = output });
    }
}

/// <summary>FR-355: the <c>flow.qa-score-valid</c> guard: a zero score is refused (so the scorer escalates).</summary>
public sealed class QaScoreGuard : IGuard
{
    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = context.Facts.Get<StageOutputFact>()?.Content ?? string.Empty;
        return Task.FromResult(output.Contains("\"score\": 0", StringComparison.Ordinal) ? GuardResult.Fail("A zero score needs a person.") : GuardResult.Pass);
    }
}

/// <summary>FR-355: a Fake scorer: a zero score for transcript <see cref="ZeroFor"/> on its first call, else 4; counts the
/// calls per transcript.</summary>
public sealed class QaScorer(int zeroFor) : IFakeAgentResponder
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _calls = new();

    public string Agent => "qa-scorer";

    public int ZeroFor => zeroFor;

    public int CallsFor(int index) => _calls.GetValueOrDefault(index);

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = string.Join('\n', request.Messages.Select(m => m.Content));
        var marker = "Transcript ";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var index = int.Parse(prompt[start..prompt.IndexOf(':', start)], System.Globalization.CultureInfo.InvariantCulture);
        var calls = _calls.AddOrUpdate(index, 1, (_, n) => n + 1);
        return index == zeroFor && calls == 1 ? "{\"score\": 0}" : "{\"score\": 4}";
    }
}

/// <summary>FR-355: the <c>flow.qa-tally</c> reduce handler: it counts the item scores it gets.</summary>
public sealed class QaTally(QaLog log) : IStageHandler
{
    public Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = context.Facts.Get<AggregateListFact>()?.Items ?? [];
        log.Tally(items);
        var output = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"count\": {items.Count}}}");
        return Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Completed) { Output = output });
    }
}

/// <summary>FR-356: the <c>flow.call-resolved</c> stop guard: it passes once a turn says <c>[resolved]</c> (it sees the transcript).</summary>
public sealed class CallResolvedGuard : IGuard
{
    public const string Marker = "[resolved]";

    public Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var turns = context.Facts.Get<ConversationTranscriptFact>()?.Turns ?? [];
        return Task.FromResult(turns.Any(t => t.Text.Contains(Marker, StringComparison.Ordinal))
            ? GuardResult.Pass
            : GuardResult.Fail("The call isn't resolved yet."));
    }
}

/// <summary>FR-356: a Fake participant answering <paramref name="answers"/> in order (then the last again) and recording every
/// prompt (system and user) it was sent.</summary>
public sealed class TurnResponder(string agent, params string[] answers) : IFakeAgentResponder
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();
    private int _calls;

    public string Agent => agent;

    public IReadOnlyList<string> Prompts => [.. _prompts];

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _prompts.Enqueue(request.System + "\n" + string.Join("\n", request.Messages.Select(m => m.Content)));
        var call = Interlocked.Increment(ref _calls);
        return answers[Math.Min(call, answers.Length) - 1];
    }
}

/// <summary>FR-357: the lists the <c>flow.trend-tally</c> handler was given, one per run.</summary>
public sealed class TrendLog
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Guid RunId, RunsInputFact? Fact)> _calls = new();

    public IReadOnlyList<(Guid RunId, RunsInputFact? Fact)> Calls => [.. _calls];

    public void Record(Guid runId, RunsInputFact? fact) => _calls.Enqueue((runId, fact));
}

/// <summary>FR-357: the <c>flow.trend-tally</c> handler of <c>fixture-trends-handler</c>: it records its <c>from_runs</c> lists.</summary>
public sealed class TrendTally(TrendLog log) : IStageHandler
{
    public Task<StageHandlerResult> ExecuteAsync(StageHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        log.Record(context.RunId, context.Facts.Get<RunsInputFact>());
        return Task.FromResult(new StageHandlerResult(StageHandlerOutcome.Completed));
    }
}

/// <summary>The API with the <c>flow</c> pack registered; its <c>stage.run</c> jobs are driven with <see cref="FixtureLinearApp"/>.</summary>
public static class FixtureFlowApp
{
    public static WebApplicationFactory<Program> Create(TevrinApiFactory factory, PositionLimit limit, params IFakeAgentResponder[] responders) =>
        Create(factory, limit, qaLog: null, responders);

    /// <summary>As above, with the <c>fixture-qa</c> handlers recording into <paramref name="qaLog"/>.</summary>
    public static WebApplicationFactory<Program> Create(TevrinApiFactory factory, PositionLimit limit, QaLog? qaLog, params IFakeAgentResponder[] responders) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddPipelinePack(new FlowFixturePack());
            s.AddSingleton(limit);
            s.AddPipelineGuard<PositionLimitGuard>(FlowFixturePack.PositionLimitGuard);
            s.AddPipelineStageHandler<FailingBearDesk>(FlowFixturePack.BearDeskHandler);
            s.AddPipelineGuard<ForecastSpreadGuard>(FlowFixturePack.SpreadGuard);
            s.AddScoped<IAgentContextProvider, LeakingForecastContext>();
            s.AddSingleton(qaLog ?? new QaLog());
            s.AddPipelineStageHandler<QaScoreItem>(FlowFixturePack.QaScoreHandler);
            s.AddPipelineStageHandler<QaTally>(FlowFixturePack.QaTallyHandler);
            s.AddPipelineGuard<QaScoreGuard>(FlowFixturePack.QaScoreGuard);
            s.AddPipelineGuard<CallResolvedGuard>(FlowFixturePack.CallResolvedGuard);
            s.AddSingleton<TrendLog>();
            s.AddPipelineStageHandler<TrendTally>(FlowFixturePack.TrendTallyHandler);
            foreach (var responder in responders)
            {
                s.AddSingleton(responder);
            }
        }));
}
