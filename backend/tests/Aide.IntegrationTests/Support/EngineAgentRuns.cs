using Aide.Api.Agents;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// FR-320: runs one SDLC agent request the way the job handlers do, on the engine runner (<see cref="SdlcAgentStageJob"/>
/// with <see cref="SdlcAgentExtension"/>), with a reply check that accepts any reply and stores nothing, so a test sees
/// the run itself (what 0.6.1's runner returned for a request without a check).
/// </summary>
public static class EngineAgentRuns
{
    /// <summary>Accepts every reply as sent; no draft, no corrective re-run.</summary>
    public static readonly ArtifactAgentSpec AnyReply = new(
        SdlcArtifacts.TechnicalSpecification,
        text => Result.Success(text),
        (_, error) => throw new JobFailedException($"Unreachable: every reply is accepted ({error.Code}).", transient: false, error.Code))
    {
        StoresDraft = false,
        LogsRejection = true,
    };

    /// <summary>Runs <paramref name="request"/> in a new scope of <paramref name="services"/>.</summary>
    public static async Task<AgentRunOutcome> RunAsync(IServiceProvider services, AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        return await RunInScopeAsync(scope.ServiceProvider, request, cancellationToken);
    }

    /// <summary>Runs <paramref name="request"/> with the services of the current scope (a job handler's).</summary>
    public static Task<AgentRunOutcome> RunInScopeAsync(IServiceProvider scoped, AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        return scoped.GetRequiredService<SdlcAgentStageJob>().RunReplyAsync(request, AnyReply, JobTriggers.StageEntered, cancellationToken);
    }
}
