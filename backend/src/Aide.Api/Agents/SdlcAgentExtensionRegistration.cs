using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-317: the SDLC's agent context providers and repo tools, for the engine's agent stage runner (scoped, like the
/// guards: <see cref="PipelineServiceCollectionExtensions.AddPipelineTool{TTool}"/> binds each tool name). Nothing in the
/// SDLC's live job path calls them yet: <see cref="AgentRunner"/> still runs the SDLC agents (M62c, M62d move them).
/// </summary>
public static class SdlcAgentExtensionRegistration
{
    public static IServiceCollection AddSdlcAgentExtensions(this IServiceCollection services)
    {
        services.AddScoped<ISdlcContextSource, SdlcContextSource>();
        services.AddScoped<IRepoToolOpener, AgentRepoToolOpener>();
        services.AddScoped<SdlcRepoToolScope>();
        services.AddScoped<IAgentContextProvider, SdlcProjectContextProvider>();
        services.AddScoped<IAgentContextProvider, SdlcIterationContextProvider>();
        services.AddScoped<IAgentContextProvider, SdlcRepoContextProvider>();

        services.AddPipelineTool<SdlcListFilesTool>(SdlcToolNames.ListFiles);
        services.AddPipelineTool<SdlcReadFileTool>(SdlcToolNames.ReadFile);
        services.AddPipelineTool<SdlcSearchCodeTool>(SdlcToolNames.SearchCode);
        return services;
    }
}
