using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>
/// FR-343–FR-349 (M68): a pack for one flow-construct fixture (<c>fixture-parallel</c>, <c>fixture-veto</c>,
/// <c>fixture-forecast</c>, <c>fixture-editorial</c>, <c>fixture-variants</c>): its definition plus every prompt, guard
/// and job type the fixtures name. Definition syntax only; the driver steps add the runtime.
/// </summary>
public sealed class FlowFixturePack(string pipelineId) : IPipelinePack
{
    public const string Parallel = "fixture-parallel";
    public const string Veto = "fixture-veto";
    public const string Forecast = "fixture-forecast";
    public const string Editorial = "fixture-editorial";
    public const string Variants = "fixture-variants";

    /// <summary>FR-350 (M69): a writer stage with a fact-checker validator agent.</summary>
    public const string FactCheck = "fixture-factcheck";

    /// <summary>FR-351 (M69): a worker with its agent feedback injected, then a reviewer whose lessons become feedback.</summary>
    public const string Postmortem = "fixture-postmortem";

    /// <summary>FR-355 (M70): a scorer mapped over 200 transcripts, then a summariser reducing the scores.</summary>
    public const string Qa = "fixture-qa";

    /// <summary>FR-356 (M70): customer and support simulators in a conversation that stops at a phrase.</summary>
    public const string CallSim = "fixture-callsim";

    /// <summary>FR-357 (M70): a weekly report over the last 7 days of <c>fixture-qa</c> runs.</summary>
    public const string Trends = "fixture-trends";

    /// <summary>FR-358, FR-359, FR-361 (M71): a webhook alert, an investigator with a read tool, then an executor whose
    /// <c>tool_policy</c> allows the destructive <c>restart-pod</c>.</summary>
    public const string OnCall = "fixture-oncall";

    /// <summary>FR-358: the investigator's log reader, <c>read</c>.</summary>
    public const string ReadLogsTool = "flow.read-logs";

    /// <summary>FR-358, FR-359: the executor's pod restart, <c>destructive</c>.</summary>
    public const string RestartPodTool = "flow.restart-pod";

    /// <summary>FR-356: a conversation stop guard (for the guard-stop edits; the fixture stops at a phrase).</summary>
    public const string CallResolvedGuard = "flow.call-resolved";

    /// <summary>FR-355: a map or reduce handler (for the handler edits; the fixture uses agents).</summary>
    public const string QaTallyHandler = "flow.qa-tally";

    public const string PositionLimitGuard = "flow.position-limit";
    public const string ForecastSpreadGuard = "flow.forecast-spread";

    public static IReadOnlyList<string> All { get; } = [Parallel, Veto, Forecast, Editorial, Variants, FactCheck, Postmortem, Qa, CallSim, Trends, OnCall];

    public static IReadOnlyList<string> Prompts { get; } =
    [
        "flow.bull", "flow.bull-review", "flow.bear", "flow.decide",
        "flow.trade", "flow.size", "flow.risk", "flow.order",
        "flow.forecast", "flow.forecast-base-rate", "flow.forecast-trend", "flow.forecast-contrarian", "flow.aggregate",
        "flow.research", "flow.write", "flow.edit",
        "flow.headline", "flow.tagline", "flow.judge",
        "flow.factwrite", "flow.fact-check",
        "flow.pm-work", "flow.pm-review", "flow.pm-grade",
        "flow.qa-score", "flow.qa-summarise", "flow.callsim-customer", "flow.callsim-support", "flow.trends",
        "flow.oncall-investigate", "flow.oncall-execute",
    ];

    public static IReadOnlyList<string> JobTypes { get; } =
        [
            "flow.risk-review", "flow.forecast-base-rate", "flow.forecast-trend", "flow.forecast-contrarian", "flow.judge", "flow.fact-check",
            "flow.qa-score", "flow.qa-summarise", "flow.callsim-customer", "flow.callsim-support",
        ];

    /// <summary>The catalog the fixtures validate against (prompts unchecked), for the invalid-case tests.</summary>
    public static PackCatalog Catalog { get; } = new()
    {
        Guards = [PositionLimitGuard, ForecastSpreadGuard, CallResolvedGuard],
        Handlers = [QaTallyHandler],
        JobTypes = JobTypes,
        Tools = [ReadLogsTool, RestartPodTool],
        ToolTiers = new Dictionary<string, ToolTier>(StringComparer.Ordinal)
        {
            [ReadLogsTool] = ToolTier.Read,
            [RestartPodTool] = ToolTier.Destructive,
        },
    };

    /// <summary>FR-358: the investigator reads the alert.</summary>
    public const string OnCallInvestigatePrompt =
        "---\nversion: flow.oncall-investigate-1\n---\nYou investigate production alerts.\n<!-- user -->\nAlert:\n{{ALERT}}\n";

    /// <summary>FR-359: the executor reads the alert and the findings.</summary>
    public const string OnCallExecutePrompt =
        "---\nversion: flow.oncall-execute-1\n---\nYou remediate production incidents.\n<!-- user -->\nAlert:\n{{ALERT}}\nFindings:\n{{FINDINGS}}\n";

    /// <summary>FR-356: the customer simulator's persona prompt (the scenario, its role and the transcript so far).</summary>
    public const string CallSimCustomerPrompt =
        "---\nversion: flow.callsim-customer-1\n---\nYou are a customer calling support.\n<!-- user -->\nScenario: {{SCENARIO}}\nYou are the {{ROLE}}.\nThe call so far:\n{{TRANSCRIPT}}\n";

    /// <summary>FR-356: the support agent simulator's persona prompt.</summary>
    public const string CallSimSupportPrompt =
        "---\nversion: flow.callsim-support-1\n---\nYou are a support agent.\n<!-- user -->\nScenario: {{SCENARIO}}\nYou are the {{ROLE}}.\nThe call so far:\n{{TRANSCRIPT}}\n";

    /// <summary>FR-357: the trend analyst's prompt reads the scores of the last 7 days' fixture-qa runs as a list.</summary>
    public const string TrendsPrompt =
        "---\nversion: flow.trends-1\n---\nYou write weekly trend reports.\n<!-- user -->\nScores of recent runs, newest first:\n{{SCORES_RUNS}}\n";

    /// <summary>The guard and job type namespace (<c>flow.&lt;name&gt;</c>).</summary>
    public string Id => "flow";

    /// <summary>The fixture's YAML.</summary>
    public static string Yaml(string pipelineId) => Fixtures.Read(pipelineId + ".pipeline.yaml");

    public void Register(IPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDefinition(pipelineId + ".pipeline.yaml", Yaml(pipelineId));
        // FR-350: real placeholders, so a registry refusing them fails here as the integration host would.
        registry.AddPrompt("flow.factwrite", "---\nversion: flow.factwrite-1\n---\nYou write articles.\n<!-- user -->\nTopic: {{TOPIC}}\n");
        registry.AddPrompt("flow.fact-check", "---\nversion: flow.fact-check-1\n---\nYou check facts.\n<!-- user -->\nArticle:\n{{OUTPUT}}\n");
        // FR-355: the map's item agent reads its element and index, the reduce agent every item output.
        registry.AddPrompt("flow.qa-score", "---\nversion: flow.qa-score-1\n---\nYou score calls.\n<!-- user -->\nTranscript {{MAP_INDEX}}:\n{{MAP_ITEM}}\n");
        registry.AddPrompt("flow.qa-summarise", "---\nversion: flow.qa-summarise-1\n---\nYou summarise scores.\n<!-- user -->\nScores:\n{{SCORES_ALL}}\n");
        // FR-356: each participant's persona, with the scenario, its role and the transcript so far.
        registry.AddPrompt("flow.callsim-customer", CallSimCustomerPrompt);
        registry.AddPrompt("flow.callsim-support", CallSimSupportPrompt);
        // FR-357: the from_runs list placeholder.
        registry.AddPrompt("flow.trends", TrendsPrompt);
        // FR-358, FR-359: the on-call agents read their inputs.
        registry.AddPrompt("flow.oncall-investigate", OnCallInvestigatePrompt);
        registry.AddPrompt("flow.oncall-execute", OnCallExecutePrompt);
        string[] own =
        [
            "flow.factwrite", "flow.fact-check", "flow.qa-score", "flow.qa-summarise", "flow.callsim-customer", "flow.callsim-support",
            "flow.trends", "flow.oncall-investigate", "flow.oncall-execute",
        ];
        foreach (var prompt in Prompts.Except(own))
        {
            registry.AddPrompt(prompt, $"""
                ---
                version: {prompt}-1
                ---
                You are a fixture agent.
                <!-- user -->
                Do the work.
                """);
        }

        registry.AddGuard(PositionLimitGuard);
        registry.AddGuard(ForecastSpreadGuard);
        registry.AddGuard(CallResolvedGuard);
        registry.AddStageHandler(QaTallyHandler);

        // FR-358: the on-call tools with their tiers.
        registry.AddTool(ReadLogsTool, ToolTier.Read);
        registry.AddTool(RestartPodTool, ToolTier.Destructive);
        foreach (var jobType in JobTypes)
        {
            registry.AddJobType(jobType);
        }
    }
}
