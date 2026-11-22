using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-317: the SDLC's agent context providers and repo tools, for the engine's agent stage runner (scoped, like the
/// guards: <see cref="PipelineServiceCollectionExtensions.AddPipelineTool{TTool}"/> binds each tool name), and FR-318's
/// engine runner with the SDLC extension, hooks and prompts for the agents that moved onto it (<see cref="SdlcAgentStageJob"/>).
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

        // FR-310–FR-316, FR-318: the engine runner with the SDLC behaviour (one extension and hooks instance per scope).
        services.AddScoped<ISdlcAgentRunData, SdlcAgentRunData>();
        services.AddScoped<SdlcPlanChecks>();
        services.AddScoped<SdlcAgentStageHooks>();
        services.AddScoped<IAgentStageHooks>(sp => sp.GetRequiredService<SdlcAgentStageHooks>());
        services.AddScoped<SdlcAgentExtension>();
        services.AddScoped<AgentStageExtension>(sp => sp.GetRequiredService<SdlcAgentExtension>());
        services.AddSingleton<IPromptSource, SdlcPromptSource>();
        services.AddAgentStages();
        services.AddScoped<ISdlcStageLocator, SdlcStageLocator>();
        services.AddScoped<SdlcAgentStageJob>();
        return services;
    }
}
