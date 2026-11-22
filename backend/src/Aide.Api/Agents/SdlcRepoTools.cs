using System.Text.Json;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.RepoTools;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>
/// FR-317, FR-71: one of the read-only repo tools as an engine <see cref="IAgentTool"/>. Its spec is the definition sent
/// to the model today (<see cref="RepoToolDefinitions"/>: same name, description and JSON schema), and a call runs on the
/// run's <see cref="RepoToolSession"/> (<see cref="SdlcRepoToolScope"/>): same results, budget, redaction and log. Without
/// a snapshot the call is an error result (0.6.1 offered no tools then; the host keeps it so, see M62d).
/// </summary>
public abstract class SdlcRepoTool : IAgentTool
{
    /// <summary>The result of a call when the run has no snapshot.</summary>
    public const string NoSnapshotMessage = $"{RepoToolErrors.NotFound}: The repository snapshot is not available for this run.";

    private readonly SdlcRepoToolScope _scope;

    protected SdlcRepoTool(SdlcRepoToolScope scope, LlmToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        _scope = scope;
        Spec = new ToolSpec(definition.Name, definition.Description, definition.InputSchema);
    }

    public ToolSpec Spec { get; }

    public async Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken cancellationToken)
    {
        // The first call opens the attempt's snapshot when the host didn't open it before the call.
        if (await _scope.OpenAsync(cancellationToken) is not { } session)
        {
            return new ToolResult(NoSnapshotMessage, IsError: true);
        }

        var result = await session.ExecuteAsync(new LlmToolCall(Spec.Name, Spec.Name, args.GetRawText()), cancellationToken);
        return new ToolResult(result.Content, result.IsError);
    }
}

/// <summary>FR-317: <c>list_files</c> (<see cref="SdlcToolNames.ListFiles"/>).</summary>
public sealed class SdlcListFilesTool(SdlcRepoToolScope scope) : SdlcRepoTool(scope, RepoToolDefinitions.ListFiles);

/// <summary>FR-317: <c>read_file</c> (<see cref="SdlcToolNames.ReadFile"/>).</summary>
public sealed class SdlcReadFileTool(SdlcRepoToolScope scope) : SdlcRepoTool(scope, RepoToolDefinitions.ReadFile);

/// <summary>FR-317: <c>search_code</c> (<see cref="SdlcToolNames.SearchCode"/>).</summary>
public sealed class SdlcSearchCodeTool(SdlcRepoToolScope scope) : SdlcRepoTool(scope, RepoToolDefinitions.SearchCode);
