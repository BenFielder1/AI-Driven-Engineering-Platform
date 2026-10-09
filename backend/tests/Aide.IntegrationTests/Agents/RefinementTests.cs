using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Refinement;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Agents;

/// <summary>FR-27: refinement rounds through <c>/refinement/run</c> and <c>GET /refinement</c>, with the Fake agent.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RefinementTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR27_Starting_refinement_queues_a_round_that_creates_a_draft_and_asks_questions()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var queued = await ProjectApi.ReadAsync<RefinementRoundDto>(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Accepted);
        Assert.Equal((AgentRunStatus.Queued, (AgentJobTrigger?)AgentJobTrigger.UserRequested, queued.JobId), (queued.Status, queued.Trigger, queued.RunId));
        var before = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal((AgentRunStatus.Queued, (Guid?)null), (before.LatestRound!.Status, before.QuestionsRunId));
        Assert.Null(before.LatestDraft);

        await AgentJobs.RunQueuedAsync(factory, id);

        var state = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal(AgentRunStatus.Succeeded, state.LatestRound!.Status);
        Assert.Equal((1, ArtifactStatus.Draft, (Guid?)queued.RunId), (state.LatestDraft!.Version, state.LatestDraft.Status, state.LatestDraft.Author.AgentRunId));
        Assert.Null(state.LatestDraft.Author.User);
        Assert.Equal((state.LatestDraft.Id, 1), (state.LatestRound.DraftArtifactId, state.LatestRound.DraftVersion));
        Assert.Equal(queued.RunId, state.QuestionsRunId);
        Assert.Equal(["q1", "q2", "q3"], state.Questions.Select(q => q.Id));
        Assert.All(state.Questions, q => Assert.Null(q.Answer));

        var draft = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/project_request/latest"));
        Assert.StartsWith($"# {fixture.Project.Name}", draft.Content, StringComparison.Ordinal);
        var created = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactCreated)).Payload);
        Assert.Equal(("agent", queued.RunId.ToString()), (created.GetProperty("author").GetString(), created.GetProperty("agentRunId").GetString()));
    }

    [Fact]
    public async Task FR27_Answering_two_of_three_questions_and_re_running_creates_a_new_draft_that_used_the_answers()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<RefinementRoundDto>(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(factory, id);
        var first = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal(3, first.Questions.Count);

        var second = await ProjectApi.ReadAsync<RefinementRoundDto>(
            await StartAsync(fixture.Contributor.Client, id, new { questionId = "q1", answer = "Small teams, with sign-in" }, new { questionId = "q2", answer = "API only" }),
            HttpStatusCode.Accepted);
        Assert.Equal(
            [new RefinementAnswerDto("q1", first.Questions[0].Question, "Small teams, with sign-in"), new RefinementAnswerDto("q2", first.Questions[1].Question, "API only")],
            second.Answers);

        // While the round is queued the answers show on the questions they answer.
        var pending = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal(["Small teams, with sign-in", "API only", null], pending.Questions.Select(q => q.Answer));
        Assert.Equal(first.QuestionsRunId, pending.QuestionsRunId);

        await AgentJobs.RunQueuedAsync(factory, id);

        var versions = await AgentJobs.ArtifactsAsync(factory, id, ArtifactType.ProjectRequest);
        Assert.Equal([(1, ArtifactStatus.Superseded), (2, ArtifactStatus.Draft)], versions.Select(v => (v.Version, v.Status)));
        Assert.Equal((int?)1, versions[1].ParentVersion);
        Assert.Equal(second.RunId, versions[1].AgentRunId);
        Assert.Contains("Small teams, with sign-in", versions[1].Content, StringComparison.Ordinal);
        Assert.Contains("API only", versions[1].Content, StringComparison.Ordinal);

        var run = (await AgentJobs.RunsAsync(factory, id)).Single(r => r.Id == second.RunId);
        Assert.Contains("A: Small teams, with sign-in", run.UserFeedback, StringComparison.Ordinal);
        Assert.Contains("A: API only", run.UserFeedback, StringComparison.Ordinal);
        Assert.Equal([new InputArtifactRef(versions[0].Id, ArtifactType.ProjectRequest, 1)], run.InputArtifacts);

        var state = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal(["q3"], state.Questions.Select(q => q.Id));
        Assert.Equal(second.RunId, state.QuestionsRunId);
        Assert.Equal(2, state.LatestRound!.Answers.Count);
        Assert.Equal(2, state.LatestDraft!.Version);
    }

    [Fact]
    public async Task FR27_A_free_question_can_be_answered_and_a_round_without_answers_is_fine()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var round = await ProjectApi.ReadAsync<RefinementRoundDto>(
            await StartAsync(fixture.Owner.Client, id, new { question = "Deadline?", answer = "June" }),
            HttpStatusCode.Accepted);

        Assert.Equal([new RefinementAnswerDto(null, "Deadline?", "June")], round.Answers);
    }

    [Fact]
    public async Task FR27_Only_one_round_at_a_time_REFINEMENT_IN_PROGRESS()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<RefinementRoundDto>(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Accepted);

        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id), HttpStatusCode.Conflict, ErrorCodes.RefinementInProgress);

        Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR27_A_round_whose_job_finds_no_draft_allowed_is_cancelled_with_the_reason_not_succeeded()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<RefinementRoundDto>(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Accepted);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Release, 6, ApprovalMode.Advisory, ProjectStatus.Releasing);

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(factory, id));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        var state = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal((AgentRunStatus.Cancelled, RefinementService.NoDraftReason, (Guid?)null), (state.LatestRound!.Status, state.LatestRound.Error, state.LatestRound.DraftArtifactId));
        Assert.Null(state.LatestDraft);
        Assert.Null(state.QuestionsRunId);
    }

    [Fact]
    public async Task FR27_Refinement_outside_the_refinement_phase_is_WRONG_PHASE()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await LifecycleSeed.SetProjectAsync(factory, fixture.Project.Id, Phase.Specification, 2, ApprovalMode.Advisory);

        await Problems.AssertAsync(await StartAsync(fixture.Contributor.Client, fixture.Project.Id), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
    }

    [Fact]
    public async Task FR26_Refinement_with_the_budget_used_up_is_BUDGET_EXCEEDED_and_queues_nothing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokenBudget, (long?)1_000L).SetProperty(p => p.TokensUsed, 1_000L), Ct));

        await Problems.AssertAsync(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.BudgetExceeded);

        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.BudgetExceeded));
    }

    [Fact]
    public async Task FR27_Unknown_question_ids_and_blank_answers_are_VALIDATION()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var body = await Problems.AssertAsync(
            await StartAsync(fixture.Contributor.Client, id, new { questionId = "q1", answer = "x" }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty("answers[0].questionId", out _));

        await Problems.AssertAsync(await StartAsync(fixture.Contributor.Client, id, new { question = "Who?", answer = "" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR27_Viewers_can_read_but_not_run_and_outsiders_do_not_see_the_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await StartAsync(fixture.Viewer.Client, id), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await StartAsync(fixture.Outsider.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"/api/projects/{id}/refinement", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var empty = await GetAsync(fixture.Viewer.Client, id);
        Assert.Null(empty.LatestDraft);
        Assert.Null(empty.LatestRound);
        Assert.Empty(empty.Questions);
    }

    [Fact]
    public async Task FR27_An_unreadable_reply_is_re_run_once_then_fails_with_INVALID_OUTPUT_and_no_draft()
    {
        var responder = new ScriptedResponder(AgentKind.IdeaRefinement, "Sorry, here is some prose instead of JSON.");
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var round = await ProjectApi.ReadAsync<RefinementRoundDto>(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Accepted);

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(2, responder.Requests.Count);
        Assert.Contains("Your previous reply was rejected", responder.Prompts[1], StringComparison.Ordinal);
        var runs = await AgentJobs.RunsAsync(app, id);
        Assert.Equal([(round.RunId, AgentRunStatus.Succeeded), (AgentRunRules.RunId(round.JobId, AgentOutput.CorrectiveRunKey), AgentRunStatus.Failed)], runs.Select(r => (r.Id, r.Status)));
        Assert.StartsWith("INVALID_OUTPUT: ", runs[1].Error, StringComparison.Ordinal);
        Assert.Empty(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ProjectRequest));

        var state = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal((AgentRunStatus.Failed, runs[1].Id), (state.LatestRound!.Status, state.LatestRound.RunId));
        Assert.StartsWith("INVALID_OUTPUT", state.LatestRound.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR17_Rejecting_an_agent_project_request_re_runs_refinement_on_the_rejected_draft_with_the_comment()
    {
        var responder = new CountingResponder(new Aide.Infrastructure.Agents.FakeIdeaRefinementResponder());
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<RefinementRoundDto>(await StartAsync(fixture.Contributor.Client, id), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, id);
        var v1 = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ProjectRequest));

        await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, v1.Id, "reject", "Mention offline use."));
        await AgentJobs.RunQueuedAsync(app, id);

        var versions = await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ProjectRequest);
        Assert.Equal([(1, ArtifactStatus.Rejected), (2, ArtifactStatus.Draft)], versions.Select(v => (v.Version, v.Status)));
        Assert.Contains("Reviewer feedback addressed: Mention offline use.", versions[1].Content, StringComparison.Ordinal);
        var rerun = (await AgentJobs.RunsAsync(app, id)).Single(r => r.Id == versions[1].AgentRunId);
        Assert.Equal("Feedback:\nMention offline use.", rerun.UserFeedback);
        Assert.Equal([new InputArtifactRef(v1.Id, ArtifactType.ProjectRequest, 1)], rerun.InputArtifacts);

        var state = await GetAsync(fixture.Viewer.Client, id);
        Assert.Equal((AgentJobTrigger?)AgentJobTrigger.ArtifactRejected, state.LatestRound!.Trigger);
        Assert.Equal("Mention offline use.", state.LatestRound.Feedback);
        Assert.Equal(2, responder.Calls);
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid projectId, params object[] answers) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/refinement/run", answers.Length == 0 ? (object)new { } : new { answers }, Ct);

    private static async Task<RefinementDto> GetAsync(HttpClient client, Guid projectId) =>
        await ProjectApi.ReadAsync<RefinementDto>(await client.GetAsync(new Uri($"/api/projects/{projectId}/refinement", UriKind.Relative), Ct));
}
