using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-317: the project's context for an SDLC agent stage: <c>{{PROJECT_NAME}}</c>, <c>{{IDEA}}</c> and
/// <c>{{MAX_FILES_PER_STEP}}</c>, for the agents whose 0.6.1 handler gave them (<see cref="SdlcAgentContext"/>).
/// </summary>
public sealed class SdlcProjectContextProvider(ISdlcContextSource source) : IAgentContextProvider
{
    private static readonly string[] Names = [SdlcContextNames.ProjectName, SdlcContextNames.Idea, SdlcContextNames.MaxFilesPerStep];

    public string PipelineId => SdlcPack.PipelineId;

    public async Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SdlcAgentContext.NamesFor(request.AgentKey).Any(Names.Contains))
        {
            return [];
        }

        var project = await source.ProjectAsync(request.ProjectId, cancellationToken);
        return project is null ? [] : SdlcAgentContext.Sections(request.AgentKey, SdlcContextValues.FromProject(project));
    }
}

/// <summary>
/// FR-317, FR-70, FR-72: the iteration context of the run's release version (its subject, as a 0.6.1 job's version): the
/// codebase summary, the baseline project request and spec, the spec mode and the version's request and base, for the
/// agents whose 0.6.1 handler gave them (<see cref="SdlcAgentContext"/>).
/// </summary>
public sealed class SdlcIterationContextProvider(ISdlcContextSource source) : IAgentContextProvider
{
    private static readonly string[] Names =
    [
        SdlcContextNames.CodebaseSummary, SdlcContextNames.SpecMode, SdlcContextNames.BaselineSpecification,
        SdlcContextNames.PreviousProjectRequest, SdlcContextNames.PreviousSpecification, SdlcContextNames.VersionRequest,
        SdlcContextNames.Base,
    ];

    public string PipelineId => SdlcPack.PipelineId;

    public async Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SdlcAgentContext.NamesFor(request.AgentKey).Any(Names.Contains))
        {
            return [];
        }

        var iteration = await source.IterationAsync(request.RunId, request.ProjectId, cancellationToken);
        return SdlcAgentContext.Sections(request.AgentKey, SdlcContextValues.FromIteration(iteration));
    }
}

/// <summary>
/// FR-317, FR-71: <see cref="AgentRepoContext"/> as context: records which repository the attempt's repo tools see
/// (<see cref="SdlcRepoToolScope.Prepare"/>). It opens nothing and adds no prompt section: the snapshot is opened just
/// before the call or on the first tool call (<see cref="SdlcRepoToolScope.OpenAsync"/>).
/// </summary>
public sealed class SdlcRepoContextProvider(SdlcRepoToolScope tools) : IAgentContextProvider
{
    public string PipelineId => SdlcPack.PipelineId;

    public Task<IReadOnlyList<AgentContextSection>> GetContextAsync(AgentContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        tools.Prepare(request.ProjectId, request.AgentKey);
        return Task.FromResult<IReadOnlyList<AgentContextSection>>([]);
    }
}
