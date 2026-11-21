using Aide.Infrastructure.Agents;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-318: what an SDLC job hands the engine runner for one agent stage attempt (<see cref="AgentStageRequest.HostState"/>):
/// the job's 0.6.1 <see cref="AgentRunRequest"/> (prompt values, input artifacts, feedback, stack, warnings, size budget)
/// and its <see cref="ArtifactAgentSpec"/> (the reply check, the draft content, the corrective re-run).
/// </summary>
public sealed record SdlcAgentStageCall(AgentRunRequest Request, ArtifactAgentSpec Spec)
{
    /// <summary>
    /// The prompt values the request renders with, as 0.6.1's runner built them: the inputs, and
    /// <c>USER_FEEDBACK</c> from the run's feedback unless an input sets it.
    /// </summary>
    public IReadOnlyDictionary<string, string?> PromptValues()
    {
        var values = new Dictionary<string, string?>(Request.Inputs, StringComparer.Ordinal);
        values.TryAdd(AgentRunner.FeedbackInput, Request.UserFeedback);
        return values;
    }

    /// <summary>
    /// The engine request of this call for <paramref name="stageId"/> of pipeline run <paramref name="runId"/>: the
    /// run id derived from the job (and run key) as in 0.6.1, the job's delivery attempt, and the host's prompt values in
    /// place of the engine's feedback (<c>USER_FEEDBACK</c>, recorded through the run metadata).
    /// </summary>
    public AgentStageRequest ToStageRequest(Guid runId, string stageId, string trigger) =>
        new(runId, stageId, trigger)
        {
            AgentRunId = AgentRunRules.RunId(Request.Job.Id, Request.RunKey),
            JobAttempt = Request.Job.Attempt,
            PromptValues = PromptValues(),
            AppendFeedback = false,
            HostState = this,
        };
}

/// <summary>
/// FR-295, FR-318: the SDLC agents' prompts are the embedded templates 0.6.1's runner used
/// (<see cref="EmbeddedPromptLibrary.Source"/>, pinned by the golden prompt tests); other pipelines use their pack's.
/// </summary>
public sealed class SdlcPromptSource(PipelineRegistry registry) : IPromptSource
{
    private readonly PackPromptSource _packs = new(registry);

    public string? Find(string packId, string pipelineId, string name) =>
        pipelineId == SdlcPack.PipelineId && SdlcAgents.All.Contains(name)
            ? EmbeddedPromptLibrary.Source(name)
            : _packs.Find(packId, pipelineId, name);
}
