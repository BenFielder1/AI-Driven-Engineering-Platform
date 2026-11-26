using System.Globalization;
using System.Text;
using System.Text.Json;
using Tevrin.Engine.Domain.Agents;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-356: one accepted turn of a conversation: the speaker's role and agent key, its reply and its agent run.</summary>
public sealed record ConversationTurn(string Role, string AgentKey, string Text, Guid AgentRunId);

/// <summary>FR-356: the fact a conversation's stop guard gets: every turn so far, in order (turn 1 first).</summary>
public sealed record ConversationTranscriptFact(IReadOnlyList<ConversationTurn> Turns);

/// <summary>FR-356, §4.69: why a conversation stopped, in the order the checks run.</summary>
public enum ConversationStopReason
{
    /// <summary>The reply holds the stop phrase (case-insensitive).</summary>
    Phrase = 1,

    /// <summary>The stop guard passed on the transcript.</summary>
    Guard = 2,

    /// <summary>The turns reached <c>max_turns</c>.</summary>
    MaxTurns = 3,
}

/// <summary>
/// FR-356, §4.69: the pure rules of conversation stages. Turn <c>n</c> (1-based) is spoken by the opener, then the next
/// participants in declared order, wrapping around (a rotation that starts at the opener). After each turn the stop
/// checks run in order: the stop phrase in the reply, the stop guard on the transcript, then <c>turns = max_turns</c>.
/// A reply is the agent's text as-is, or the <c>text</c> field when the agent answers a JSON object <c>{ "text": … }</c>.
/// </summary>
public static class ConversationRules
{
    /// <summary>What <see cref="AgentPromptPlaceholders.Transcript"/> holds before the first turn (a required placeholder
    /// can't be empty).</summary>
    public const string EmptyTranscript = "(no turns yet)";

    private static readonly JsonSerializerOptions TranscriptJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The participant who speaks turn <paramref name="turn"/> (1-based).</summary>
    public static ConversationParticipant SpeakerOf(ConversationDefinition conversation, int turn)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentOutOfRangeException.ThrowIfLessThan(turn, 1);
        var participants = conversation.Participants;
        var opener = 0;
        for (var i = 0; i < participants.Count; i++)
        {
            if (string.Equals(participants[i].Role, conversation.Opener, StringComparison.Ordinal))
            {
                opener = i;
                break;
            }
        }

        return participants[(opener + turn - 1) % participants.Count];
    }

    /// <summary>Whether <paramref name="reply"/> holds the stop phrase (case-insensitive); false without a phrase.</summary>
    public static bool PhraseIn(ConversationStop? stop, string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return stop?.Phrase is { Length: > 0 } phrase && reply.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the stop guard must be asked after a turn: one is declared and the phrase didn't already stop it.</summary>
    public static bool NeedsGuard(ConversationDefinition conversation, bool phraseFound)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return !phraseFound && conversation.Stop?.Guard is not null;
    }

    /// <summary>
    /// §4.69: whether the conversation stops after <paramref name="turns"/> turns, checked in order: the phrase, the guard,
    /// then <c>max_turns</c>; <c>null</c> to go on.
    /// </summary>
    public static ConversationStopReason? StopAfter(ConversationDefinition conversation, int turns, bool phraseFound, bool guardPassed)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (phraseFound)
        {
            return ConversationStopReason.Phrase;
        }

        if (guardPassed && conversation.Stop?.Guard is not null)
        {
            return ConversationStopReason.Guard;
        }

        return turns >= conversation.TurnLimit ? ConversationStopReason.MaxTurns : null;
    }

    /// <summary>A participant's reply from its output text: the <c>text</c> field of a JSON object, else the text trimmed.</summary>
    public static string ReadReply(string? output)
    {
        var trimmed = output?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()!.Trim()
                : trimmed;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    /// <summary>The turns as a participant's prompt sees them: <c>### turn n (role)</c> then the reply, per turn.</summary>
    public static string RenderTranscript(IReadOnlyList<ConversationTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0)
        {
            return EmptyTranscript;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < turns.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"### turn {i + 1} ({turns[i].Role})\n{turns[i].Text}\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>The stage's output: <c>{ "turns": [{ "role", "agentKey", "text", "agentRunId" }] }</c>.</summary>
    public static string TranscriptJson(IReadOnlyList<ConversationTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return JsonSerializer.Serialize(new { turns }, TranscriptJsonOptions);
    }

    /// <summary>
    /// The participant of <paramref name="stage"/>'s conversation bound to <paramref name="agentKey"/> with role
    /// <paramref name="role"/>, or <c>null</c> (not a conversation stage, or no such participant).
    /// </summary>
    public static ConversationParticipant? ParticipantOf(StageDefinition stage, string? agentKey, string? role)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Conversation?.Participants.FirstOrDefault(p =>
            string.Equals(p.Agent, agentKey, StringComparison.Ordinal) && string.Equals(p.Role, role, StringComparison.Ordinal));
    }

    /// <summary>
    /// The input keys among <paramref name="keys"/> whose placeholder is one a participant's prompt reserves
    /// (<c>transcript</c> → <c>{{TRANSCRIPT}}</c>, <c>role</c> → <c>{{ROLE}}</c>); the validator refuses them on a conversation
    /// stage and its participants' bindings. (The runner fills the reserved values last, so they would win.)
    /// </summary>
    public static IReadOnlyList<string> ReservedInputs(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return [.. keys.Where(k => AgentPromptPlaceholders.Of(k) is AgentPromptPlaceholders.Transcript or AgentPromptPlaceholders.Role)];
    }

    /// <summary>
    /// The placeholders the engine fills for agent <paramref name="agentKey"/> of conversation stage
    /// <paramref name="stage"/>: a participant gets <c>{{TRANSCRIPT}}</c>, <c>{{ROLE}}</c> and the stage's own inputs.
    /// Empty for other agents and stages.
    /// </summary>
    public static IReadOnlyList<string> SuppliedPlaceholders(StageDefinition stage, string? agentKey)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Conversation is not { } conversation
            || !conversation.Participants.Any(p => string.Equals(p.Agent, agentKey, StringComparison.Ordinal)))
        {
            return [];
        }

        return [AgentPromptPlaceholders.Transcript, AgentPromptPlaceholders.Role, .. stage.Inputs.Select(AgentPromptPlaceholders.Of)];
    }
}
