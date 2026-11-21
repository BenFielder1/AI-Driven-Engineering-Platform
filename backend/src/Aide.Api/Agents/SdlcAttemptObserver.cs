using System.Diagnostics;
using Aide.Api.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;

namespace Aide.Api.Agents;

/// <summary>
/// FR-311, NFR-OBS: 0.6.1's log scope (<c>RunId</c>, <c>Agent</c>, <c>ProjectId</c>) and <c>agent run</c> span of one SDLC
/// agent attempt on the engine runner, as <see cref="AgentRunner.RunAsync"/> opened them: the span carries the run id,
/// agent and project, then the run's status, pipeline run and stage; a failure (or a rejected final attempt) is an error
/// span described by its code, never its message.
/// </summary>
internal sealed class SdlcAttemptObserver : IAgentAttemptObserver
{
    private readonly IDisposable? _scope;
    private readonly Activity? _activity;
    private readonly Guid _runId;

    public SdlcAttemptObserver(ILogger logger, AgentStageRequest request, SdlcAgentStageCall call)
    {
        _runId = request.AgentRunId ?? AgentRunRules.RunId(call.Request.Job.Id, call.Request.RunKey);
        _scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RunId"] = _runId,
            ["Agent"] = call.Request.Agent,
            [ProjectLogScope.ProjectIdKey] = call.Request.ProjectId,
        });
        _activity = AideTelemetry.Source.StartActivity("agent run", ActivityKind.Internal);
        _activity?.SetTag(AideTelemetry.RunIdTag, _runId.ToString());

        // 0.6.1 tagged the AgentKind value, which exporters render by member name (IdeaRefinement).
        _activity?.SetTag(AideTelemetry.AgentTag, KeyNames.ToPascalCase(call.Request.Agent));
        _activity?.SetTag(AideTelemetry.ProjectIdTag, call.Request.ProjectId.ToString());
    }

    /// <summary>The attempt's agent run once it exists (set by the extension).</summary>
    public AgentRun? Run { get; set; }

    /// <summary>The attempt (set at its first step): a redelivered failed run is tagged with its stored status, as 0.6.1.</summary>
    public AgentAttempt? Attempt { get; set; }

    public void Completed(AgentStageOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Run = outcome.AgentRun;
        Tag();
        if (outcome.Failure is { } failure)
        {
            AideTelemetry.SetError(_activity, failure.ErrorCode);
        }
        else
        {
            _activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    public void Failed(Exception exception)
    {
        Tag();
        switch (exception)
        {
            case JobFailedException failed:
                AideTelemetry.SetError(_activity, failed.ErrorCode ?? nameof(JobFailedException));
                break;
            case OperationCanceledException:
                break;
            default:
                AideTelemetry.SetError(_activity, exception.GetType().Name);
                break;
        }
    }

    public void Dispose()
    {
        _activity?.Dispose();
        _scope?.Dispose();
    }

    /// <summary>The run's status as the attempt left it (<c>not_started</c> without a run), pipeline run and stage.</summary>
    private void Tag()
    {
        if (_activity is null)
        {
            return;
        }

        var run = (Run ?? Attempt?.AgentRun ?? Attempt?.Existing) is { } known && known.Id == _runId ? known : null;
        _activity.SetTag(AideTelemetry.RunStatusTag, run?.Status.ToWire() ?? "not_started");
        if (run is not null)
        {
            _activity.SetTag(AideTelemetry.PipelineRunIdTag, run.PipelineRunId?.ToString());
            _activity.SetTag(AideTelemetry.StageTag, run.Stage);
        }
    }
}
