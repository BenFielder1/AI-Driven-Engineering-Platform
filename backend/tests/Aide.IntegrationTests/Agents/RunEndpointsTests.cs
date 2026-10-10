using System.Net;
using Aide.Api.Agents;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Runs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Agents;

/// <summary>FR-23: <c>GET /api/projects/{id}/runs[/{runId}]</c> — paged newest first, agent filter, member access, NOT_FOUND.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RunEndpointsTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR23_Members_list_runs_newest_first_with_paging_and_an_agent_filter()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var oldest = await AddRunAsync(fixture.Project.Id, AgentKind.IdeaRefinement, now.AddMinutes(-3));
        var middle = await AddRunAsync(fixture.Project.Id, AgentKind.SpecGenerator, now.AddMinutes(-2));
        var newest = await AddRunAsync(fixture.Project.Id, AgentKind.SpecGenerator, now.AddMinutes(-1), status: AgentRunStatus.Failed, error: "PROVIDER_ERROR: Ollama returned HTTP 503.");

        var all = await ProjectApi.ReadAsync<PagedResult<AgentRunDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/runs", UriKind.Relative), Ct));
        Assert.Equal([newest.Id, middle.Id, oldest.Id], all.Items.Select(r => r.Id));
        Assert.Equal((1, 20, 3), (all.Page, all.Limit, all.Total));

        var page2 = await ProjectApi.ReadAsync<PagedResult<AgentRunDto>>(await fixture.Contributor.Client.GetAsync(new Uri($"{fixture.Path}/runs?page=2&limit=2", UriKind.Relative), Ct));
        Assert.Equal([oldest.Id], page2.Items.Select(r => r.Id));
        Assert.Equal(3, page2.Total);

        var specs = await ProjectApi.ReadAsync<PagedResult<AgentRunDto>>(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/runs?agent=spec_generator", UriKind.Relative), Ct));
        Assert.Equal([newest.Id, middle.Id], specs.Items.Select(r => r.Id));

        var failed = specs.Items[0];
        Assert.Equal((AgentKind.SpecGenerator, Phase.Specification, LlmProvider.Ollama, "qwen2.5-coder", "1"), (failed.Agent, failed.Phase, failed.Provider, failed.Model, failed.PromptVersion));
        Assert.Equal((AgentRunStatus.Failed, 4, "PROVIDER_ERROR: Ollama returned HTTP 503."), (failed.Status, failed.Attempts, failed.Error));
        Assert.Equal([new RunInputArtifactDto(ArtifactType.ProjectRequest, 3)], failed.InputArtifacts);
        Assert.Equal((120, 45, 0.0012m, 900L), (failed.InputTokens, failed.OutputTokens, failed.CostEstimate, failed.DurationMs));
    }

    [Fact]
    public async Task FR23_Run_detail_includes_the_output_text()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var run = await AddRunAsync(fixture.Project.Id, AgentKind.SpecGenerator, DateTimeOffset.UtcNow, output: new RunOutput("## 1. Overview", "end_turn"));

        var detail = await ProjectApi.ReadAsync<AgentRunDetailDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/runs/{run.Id}", UriKind.Relative), Ct));

        Assert.Equal((run.Id, "## 1. Overview", false, "Answer: yes"), (detail.Id, detail.Output, detail.OutputTruncated, detail.UserFeedback));
    }

    [Fact]
    public async Task FR23_Long_output_is_truncated()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var run = await AddRunAsync(fixture.Project.Id, AgentKind.CodeGenerator, DateTimeOffset.UtcNow, output: new RunOutput(new string('x', RunEndpoints.MaxOutputLength + 10), "max_tokens"));

        var detail = await ProjectApi.ReadAsync<AgentRunDetailDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/runs/{run.Id}", UriKind.Relative), Ct));

        Assert.Equal((RunEndpoints.MaxOutputLength, true), (detail.Output!.Length, detail.OutputTruncated));
    }

    [Fact]
    public async Task FR23_Unknown_runs_and_runs_of_other_projects_are_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var other = await ProjectApi.FixtureAsync(factory);
        var foreign = await AddRunAsync(other.Project.Id, AgentKind.SpecGenerator, DateTimeOffset.UtcNow);

        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/runs/{foreign.Id}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "NOT_FOUND");
        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/runs/{Guid.CreateVersion7()}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task FR23_Outsiders_get_404_anonymous_callers_401_and_bad_filters_400()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var run = await AddRunAsync(fixture.Project.Id, AgentKind.SpecGenerator, DateTimeOffset.UtcNow);

        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/runs", UriKind.Relative), Ct), HttpStatusCode.NotFound, "NOT_FOUND");
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/runs/{run.Id}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "NOT_FOUND");
        using var anonymous = new ApiSession(factory);
        await Problems.AssertAsync(await anonymous.Client.GetAsync(new Uri($"{fixture.Path}/runs", UriKind.Relative), Ct), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/runs?agent=nope", UriKind.Relative), Ct), HttpStatusCode.BadRequest, "VALIDATION");
        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/runs?limit=101", UriKind.Relative), Ct), HttpStatusCode.BadRequest, "VALIDATION");
    }

    private Task<AgentRun> AddRunAsync(
        Guid projectId,
        AgentKind agent,
        DateTimeOffset createdAt,
        AgentRunStatus status = AgentRunStatus.Succeeded,
        string? error = null,
        RunOutput? output = null) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var run = new AgentRun
            {
                ProjectId = projectId,
                Agent = agent,
                Phase = Phase.Specification,
                Provider = LlmProvider.Ollama,
                Model = "qwen2.5-coder",
                PromptVersion = "1",
                InputArtifacts = [new InputArtifactRef(Guid.CreateVersion7(), ArtifactType.ProjectRequest, 3)],
                UserFeedback = "Answer: yes",
                Output = output is null ? null : AideJson.Serialize(output),
                Status = status,
                Attempts = 4,
                InputTokens = 120,
                OutputTokens = 45,
                CostEstimate = 0.0012m,
                DurationMs = 900,
                Error = error,
                CreatedAt = createdAt,
                CompletedAt = createdAt,
            };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(Ct);
            return run;
        });
}
