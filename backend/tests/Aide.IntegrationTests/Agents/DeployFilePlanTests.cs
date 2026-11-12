using System.Text.Json.Nodes;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Agents;

/// <summary>
/// FR-243, FR-244, §4.41: a generated plan of a Vercel-deployed stack that leaves out <c>vercel.json</c> is stored with a
/// deploy-engineer step added by Tevrin straight after step 1, and the <c>MISSING_VERCEL_CONFIG</c> warning isn't raised.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeployFilePlanTests(AideApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Recipe Finder\n\n## Summary\nRecipes.";

    /// <summary>Next.js (full-stack, at <c>frontend/</c>) deployed to Vercel.</summary>
    private static readonly object NextOnVercel = new { frontend = new { pack = "fe-nextjs", path = "frontend" }, deploy = new { frontendHost = "deploy-vercel" } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR243_A_generated_Vercel_plan_without_vercel_json_is_stored_with_the_deploy_engineer_step_at_position_2()
    {
        var responder = new WithoutFileResponder(new FakePlanGeneratorResponder(), VercelConfigRules.FileName);
        await using var app = AgentJobs.WithResponders(factory, responder);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, id, NextOnVercel);
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Specification, 2, ApprovalMode.Autonomous);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: ProjectRequest);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: Spec(), projectVersionAtCreation: 2);
        await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id));

        Assert.Equal((JobTypes.PlanGeneration, JobStatus.Succeeded), (job.Type, job.Status));
        var agentSteps = Assert.Single(responder.Replies);
        Assert.DoesNotContain(VercelConfigRules.FileName, agentSteps, StringComparison.Ordinal);
        var plan = Assert.Single(await AgentJobs.ArtifactsAsync(app, id, ArtifactType.ImplementationPlan));
        var steps = PlanDocument.ReadSteps(plan.Content); // the block as stored, with provenance (no generalist-only validation)
        Assert.Equal(JsonNode.Parse(agentSteps)!["steps"]!.AsArray().Count + 1, steps.Count);
        var added = steps[1];
        Assert.Equal((2, StackAgents.DeployEngineer), (added.Number, added.Agent));
        Assert.Equal([new DeclaredFile("frontend/vercel.json", FileAction.Create)], added.Files);
        Assert.Equal([1], added.Dependencies);
        Assert.Equal((PlanStep.AddedByTevrin, "frontend/vercel.json was missing from the plan"), (added.AddedBy, added.AddedReason));
        Assert.All(steps.Where(s => s.Number != 2), s => Assert.Null(s.AddedBy));
        Assert.Contains("\"addedBy\": \"tevrin\"", plan.Content, StringComparison.Ordinal);

        var dto = await ProjectApi.ReadAsync<ArtifactDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/artifacts/implementation_plan/latest", UriKind.Relative), Ct));
        Assert.DoesNotContain(dto.Warnings, w => w.Code == ErrorCodes.MissingVercelConfig);
    }

    private static string Spec() =>
        string.Join("\n\n", SpecSections.Required.Select((name, i) => name == "Functional Requirements"
            ? $"## {i + 1}. {name}\n- **FR-1** Create a recipe. Given a title, when it is posted, then it is stored."
            : $"## {i + 1}. {name}\nDetails about {name}."));

    /// <summary>
    /// The default Fake plan generator's reply with every declaration of a file named <paramref name="fileName"/> removed
    /// (steps left without files are dropped and the rest renumbered), so the plan misses that deployment file.
    /// </summary>
    private sealed class WithoutFileResponder(IFakeAgentResponder inner, string fileName) : IFakeAgentResponder
    {
        private readonly List<string> _replies = [];

        public AgentKind Agent => inner.Agent;

        /// <summary>The replies sent to Tevrin (after the file was removed).</summary>
        public IReadOnlyList<string> Replies
        {
            get
            {
                lock (_replies)
                {
                    return [.. _replies];
                }
            }
        }

        public string Respond(LlmRequest request)
        {
            var root = JsonNode.Parse(inner.Respond(request))!.AsObject();
            var kept = new JsonArray();
            var renumber = new Dictionary<int, int>();
            foreach (var step in root["steps"]!.AsArray().Select(s => s!.AsObject()))
            {
                var files = step["files"]!.AsArray();
                foreach (var file in files.Where(f => ((string)f!["path"]!).Split('/')[^1] == fileName).ToList())
                {
                    files.Remove(file);
                }

                if (files.Count == 0)
                {
                    continue;
                }

                renumber[(int)step["number"]!] = kept.Count + 1;
                kept.Add(step.DeepClone());
            }

            foreach (var step in kept.Select(s => s!.AsObject()))
            {
                step["number"] = renumber[(int)step["number"]!];
                var deps = step["dependencies"]!.AsArray().Select(d => (int)d!).Where(renumber.ContainsKey).Select(d => renumber[d]).ToArray();
                step["dependencies"] = new JsonArray([.. deps.Select(d => JsonValue.Create(d))]);
            }

            var reply = new JsonObject { ["steps"] = kept }.ToJsonString();
            lock (_replies)
            {
                _replies.Add(reply);
            }

            return reply;
        }
    }
}
