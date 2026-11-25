using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-350, §4.55 (M69): validator agents end to end on the Fake provider, with <c>fixture-factcheck</c>: the writer's
/// article is checked by a fact-checker validator agent; its error findings retry the writer with feedback, warnings are
/// stored only, and an answer outside the verdict schema fails the job with <c>VALIDATOR_INVALID_OUTPUT</c>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ValidatorAgentTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Wrong = "Water boils at 90 C at sea level.";
    private const string Right = "Water boils at 100 C at sea level.";

    private const string Reject = """
        { "verdict": "reject", "findings": [
          { "claim": "Water boils at 90 C", "reason": "It boils at 100 C at sea level.", "severity": "error" },
          { "claim": "sea level", "reason": "Name the pressure.", "severity": "warning" }
        ] }
        """;

    private const string Pass = """{ "verdict": "pass", "findings": [] }""";

    private const string PassWithWarning = """
        { "verdict": "pass", "findings": [{ "path": "/1", "reason": "Cite a source.", "severity": "warning" }] }
        """;

    /// <summary>A Fake writer answering <paramref name="answers"/> in order (then the last again), recording each prompt.</summary>
    private sealed class Writer(params string[] answers) : IFakeAgentResponder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();

        public string Agent => "writer";

        public IReadOnlyList<string> Prompts => [.. _prompts];

        public string Respond(LlmRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _prompts.Enqueue(string.Join("\n", request.Messages.Select(m => m.Content)));
            return answers[Math.Min(_prompts.Count, answers.Length) - 1];
        }
    }

    private static async Task<Guid> StartAsync(ProjectFixture fixture)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FlowFixturePack.FactCheck, approvalMode = "advisory", inputs = new { topic = "Boiling water." } },
                Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static async Task<List<AgentRun>> AgentRunsAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId, string agent) =>
        [.. (await AgentJobs.RunsAsync(app, projectId)).Where(r => r.PipelineRunId == runId && r.Stage == "write" && r.Agent == agent)];

    private static Task<List<Artifact>> ArticlesAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.PipelineRunId == runId && a.Type == "article").ToListAsync(Ct));

    private static List<(string Agent, string Severity)> Stored(string? findings) =>
        [.. JsonDocument.Parse(findings!).RootElement.EnumerateArray()
            .Select(f => (f.GetProperty("agent").GetString()!, f.GetProperty("severity").GetString()!))];

    [Fact]
    public async Task FR350_error_findings_reject_the_draft_and_retry_the_writer_with_feedback_then_it_passes()
    {
        var writer = new Writer(Wrong, Right);
        var checker = new ScriptedResponder("fact-checker", Reject, Pass);
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), writer, checker);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal(GenericRunStatus.Completed, run.Status);
        var article = Assert.Single(await ArticlesAsync(app, fixture.Project.Id, runId));
        Assert.Equal(Right, article.Content.Trim());

        // The retry carried the error finding (not the warning) as feedback.
        Assert.Equal(2, writer.Prompts.Count);
        Assert.Contains("It boils at 100 C at sea level.", writer.Prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Name the pressure.", writer.Prompts[1], StringComparison.Ordinal);

        // Each attempt's run holds its findings; the rejected one counts as a validation failure.
        var writes = await AgentRunsAsync(app, fixture.Project.Id, runId, "writer");
        Assert.Equal(2, writes.Count);
        var rejected = Assert.Single(writes, r => r.Error is not null);
        Assert.StartsWith(StageValidationRules.RejectedErrorPrefix, rejected.Error, StringComparison.Ordinal);
        Assert.Equal([("fact-checker", "error"), ("fact-checker", "warning")], Stored(rejected.ValidatorFindings));
        var accepted = Assert.Single(writes, r => r.Error is null);
        Assert.Equal(AgentRunStatus.Succeeded, accepted.Status);
        Assert.Empty(Stored(accepted.ValidatorFindings));

        // The validator's own runs are recorded, one per attempt.
        Assert.Equal(2, checker.Calls);
        var checks = await AgentRunsAsync(app, fixture.Project.Id, runId, "fact-checker");
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(AgentRunStatus.Succeeded, c.Status));
    }

    [Fact]
    public async Task FR350_warning_only_findings_pass_and_are_stored_on_the_run()
    {
        var writer = new Writer(Right);
        var checker = new ScriptedResponder("fact-checker", PassWithWarning);
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), writer, checker);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Single(await ArticlesAsync(app, fixture.Project.Id, runId));
        Assert.Single(writer.Prompts);
        var write = Assert.Single(await AgentRunsAsync(app, fixture.Project.Id, runId, "writer"));
        Assert.Null(write.Error);
        Assert.Equal([("fact-checker", "warning")], Stored(write.ValidatorFindings));
        Assert.Contains("Cite a source.", write.ValidatorFindings, StringComparison.Ordinal);
        Assert.Single(await AgentRunsAsync(app, fixture.Project.Id, runId, "fact-checker"));
    }

    [Fact]
    public async Task FR350_a_validator_answer_outside_the_verdict_schema_fails_the_job_with_VALIDATOR_INVALID_OUTPUT()
    {
        var writer = new Writer(Right);
        var checker = new ScriptedResponder("fact-checker", "Looks accurate to me.");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), writer, checker);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var failed = Assert.Single(await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Failed)
            .ToListAsync(Ct)));
        Assert.StartsWith(ErrorCodes.ValidatorInvalidOutput, failed.LastError, StringComparison.Ordinal);
        Assert.Empty(await ArticlesAsync(app, fixture.Project.Id, runId));
        Assert.Single(writer.Prompts);
        var write = Assert.Single(await AgentRunsAsync(app, fixture.Project.Id, runId, "writer"));
        Assert.Equal(AgentRunStatus.Failed, write.Status);
        Assert.StartsWith(ErrorCodes.ValidatorInvalidOutput, write.Error, StringComparison.Ordinal);
        Assert.Single(await AgentRunsAsync(app, fixture.Project.Id, runId, "fact-checker"));
    }
}
