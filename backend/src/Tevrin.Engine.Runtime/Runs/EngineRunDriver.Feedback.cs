using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Runs;

public sealed partial class EngineRunDriver
{
    /// <summary>
    /// FR-351: after a stage with <c>writes_feedback</c> completes, one <c>stage</c> feedback entry for its agent key per
    /// item of the named field of its output (a string, or an object's <c>text</c>), linked to the run and the agent run
    /// that produced the output. Entry ids derive from the stage execution, so a redelivered job writes none twice.
    /// </summary>
    private async Task WriteFeedbackAsync(PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, string? output, CancellationToken cancellationToken)
    {
        if (stage.WritesFeedback is not { } writes)
        {
            return;
        }

        var firstId = FeedbackEntryId(execution, 0);
        if (await db.AgentFeedback.AnyAsync(f => f.Id == firstId, cancellationToken))
        {
            return;
        }

        var artifact = stage.Output is { } key
            ? await db.Artifacts.AsNoTracking()
                .Where(a => a.PipelineRunId == run.Id && a.Type == key && a.Variant == null)
                .OrderByDescending(a => a.Version)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var texts = FeedbackInjectionRules.Texts(FeedbackItems(artifact?.Content ?? output, writes.From));
        if (texts.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        for (var i = 0; i < texts.Count; i++)
        {
            db.AgentFeedback.Add(new AgentFeedback
            {
                Id = FeedbackEntryId(execution, i),
                PipelineId = definition.Pipeline,
                AgentKey = writes.Agent,
                ProjectId = run.ProjectId,
                Source = AgentFeedbackSource.Stage,
                Text = texts[i],
                RunId = run.Id,
                AgentRunId = artifact?.AgentRunId,

                // Later items are newer by a microsecond, so newest-first lists them in reverse output order deterministically.
                CreatedAt = now.AddTicks(i * TimeSpan.TicksPerMicrosecond),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Guid FeedbackEntryId(StageExecution execution, int index) => AgentRunRules.RunId(execution.Id, $"feedback/{index}");

    /// <summary>The items of field <paramref name="field"/> of a JSON object output: each string, or each object's
    /// <c>text</c> string (a single string field is one item); none when the output isn't such an object.</summary>
    internal static IEnumerable<string?> FeedbackItems(string? output, string field)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(field, out var value))
            {
                return [];
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => [value.GetString()],
                JsonValueKind.Array => [.. value.EnumerateArray().Select(ItemText)],
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ItemText(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString(),
        JsonValueKind.Object when item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String => text.GetString(),
        _ => null,
    };
}
