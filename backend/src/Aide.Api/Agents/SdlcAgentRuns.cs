using System.Text.Json;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;

namespace Aide.Api.Agents;

/// <summary>
/// FR-311–FR-316, FR-320: the SDLC's agent run rules that outlived 0.6.1's runner, unchanged: the stored output, the
/// outcome a job gets back, the stack stamp, a failed run and its <c>RUN_FAILED</c>, and the invalidated connection.
/// Used by <see cref="SdlcAgentExtension"/>, <see cref="SdlcAgentStageJob"/>, <see cref="AgentRunJobEndedHook"/> and the
/// run readers.
/// </summary>
public static partial class SdlcAgentRuns
{
    /// <summary>The longest error text stored on a run.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>The placeholder the run's feedback fills unless an input sets it.</summary>
    public const string FeedbackInput = "USER_FEEDBACK";

    private static readonly JsonSerializerOptions PacksJson = new(JsonSerializerDefaults.Web);

    /// <summary>The stored output of a run (<c>{"text": …, "stopReason": …}</c>), or <c>null</c>.</summary>
    public static RunOutput? ReadOutput(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return AideJson.Deserialize<RunOutput>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A succeeded run as the job gets it back: its id, the provider's text, what it cost and when it completed.</summary>
    internal static AgentRunOutcome Outcome(AgentRun run)
    {
        var output = ReadOutput(run.Output);
        return new AgentRunOutcome(run.Id, output?.Text ?? string.Empty, output?.StopReason ?? string.Empty, run.Provider, run.Model, run.PromptVersion, run.InputTokens, run.OutputTokens, run.CompletedAt ?? run.CreatedAt)
        {
            Warnings = output?.Warnings ?? [],
        };
    }

    /// <summary>
    /// FR-121: a run of a composed project records the catalog version it resolved against and the packs it loaded
    /// (<c>[{id, version, sections[]}]</c>; <c>[]</c> when the agent loads none); a plan step also records its <c>agent_key</c>:
    /// the specialist, or <c>generalist</c> for a generalist step (an optimisation plan's fix steps, base prompt).
    /// </summary>
    internal static void StampStack(AgentRun run, AgentRunRequest request)
    {
        if (request.Stack is not { } stack)
        {
            return;
        }

        run.CatalogVersion = stack.CatalogVersion;
        var specialist = request.SpecialistAgentKey is { } key ? stack.Specialist(key) : null;
        if (specialist is not null)
        {
            run.AgentKey = specialist.Key;
        }
        else if (request.SpecialistAgentKey == Domain.Execution.PlanAgents.Generalist)
        {
            run.AgentKey = Domain.Execution.PlanAgents.Generalist;
        }

        run.Packs = JsonSerializer.Serialize(
            (specialist?.Packs ?? []).Select(p => new { id = p.Id, version = p.Version, sections = p.Sections }),
            PacksJson);
    }

    /// <summary>Fails <paramref name="run"/> and adds its <c>RUN_FAILED</c> event (the caller saves).</summary>
    internal static void MarkFailed(AideDbContext db, AgentRun run, string errorCode, string message, bool deadLetter, DateTimeOffset now)
    {
        SdlcRunEvents.Fail(run, errorCode, message, now);
        db.AddEvent(
            EventTypes.RunFailed,
            run.ProjectId,
            SdlcRunEvents.RunFailed(run, errorCode, deadLetter),
            SystemActor.Name,
            now);
    }

    /// <summary>
    /// FR-78: marks the run's bound connection <c>invalid</c> and records <c>CONNECTION_INVALID</c> (no secret). The run's
    /// <c>RUN_FAILED</c> notifies the project's Owners; the dedicated notification to the connection's owner is FR-92.
    /// Saved with the run's failure.
    /// </summary>
    internal static async Task InvalidateCredentialAsync(AideDbContext db, TimeProvider time, ILogger logger, AgentRun run, Guid credentialId)
    {
        var credential = await db.LlmCredentials.FirstOrDefaultAsync(c => c.Id == credentialId, CancellationToken.None);
        if (credential is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        credential.Status = LlmCredentialStatus.Invalid;
        credential.LastValidatedAt = now;
        db.AddEvent(
            EventTypes.ConnectionInvalid,
            run.ProjectId,
            new { credentialId, userId = credential.UserId, provider = credential.Provider.ToWire(), runId = run.Id },
            SystemActor.Name,
            now);
        LogCredentialInvalid(logger, credentialId, credential.Provider);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The provider rejected the key of LLM connection {CredentialId} ({Provider}); it is marked invalid")]
    private static partial void LogCredentialInvalid(ILogger logger, Guid credentialId, LlmProvider provider);
}
