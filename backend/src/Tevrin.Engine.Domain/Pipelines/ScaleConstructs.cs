using System.Globalization;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-355 (0.8.0, §4.49 amended, §4.68): <c>map: { over: &lt;artifact key&gt;.&lt;array path&gt;, agent | handler, max_parallel,
/// max_items, on_item_failure, max_failed }</c> on a stage runs one isolated item execution per element of the array at
/// <see cref="Over"/>. Exactly one of <see cref="Agent"/> (a key the stage binds under <c>agents:</c>) and
/// <see cref="Handler"/> is set. A map stage has no <c>agent:</c> or <c>handler:</c> of its own; its <c>output</c> is the
/// item collection. The loader fills the defaults, so writing one out doesn't change the hash.
/// </summary>
public sealed record MapDefinition(string Over, string? Agent, string? Handler)
{
    public const int DefaultMaxParallel = 8;
    public const int MinMaxParallel = 1;
    public const int MaxMaxParallel = 100;

    /// <summary>§4.68, SPEC Q4: 1,000 is both the default and the cap of <see cref="MaxItems"/>.</summary>
    public const int DefaultMaxItems = 1000;
    public const int MinMaxItems = 1;
    public const int MaxMaxItems = 1000;

    public int MaxParallel { get; init; } = DefaultMaxParallel;

    public int MaxItems { get; init; } = DefaultMaxItems;

    public MapItemFailure OnItemFailure { get; init; } = MapItemFailure.Skip;

    public MapFailureLimit MaxFailed { get; init; } = MapFailureLimit.Default;

    /// <summary>The artifact key of <see cref="Over"/>: the text before the first <c>.</c>.</summary>
    public string SourceArtifact => Over.Split('.', 2)[0];

    /// <summary>The array path inside the artifact: the text after the first <c>.</c>, or empty for the artifact itself.</summary>
    public string ItemPath => Over.Split('.', 2) is [_, var path] ? path : string.Empty;
}

/// <summary>FR-355, §4.68: what a failed item does.</summary>
public enum MapItemFailure
{
    /// <summary>The item is skipped; the stage fails only when failed items exceed <c>max_failed</c> (the default).</summary>
    Skip = 0,

    /// <summary>The first failed item fails the stage.</summary>
    Fail = 1,
}

/// <summary>
/// FR-355, §4.68: <c>max_failed</c>, a count (<c>5</c>) or a percentage of the items (<c>5%</c>, rounded down); the
/// default is <c>5%</c>.
/// </summary>
public sealed record MapFailureLimit(int Value, bool IsPercent)
{
    public static MapFailureLimit Default { get; } = new(5, IsPercent: true);

    /// <summary>The failed items allowed out of <paramref name="total"/>: the count, or the percentage rounded down.</summary>
    public int Allowed(int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        return IsPercent ? (int)((long)total * Value / 100) : Value;
    }

    /// <summary>As written: <c>5%</c> or <c>5</c>.</summary>
    public string ToWire() => IsPercent ? Value.ToString(CultureInfo.InvariantCulture) + "%" : Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads <c>&lt;n&gt;</c> or <c>&lt;n&gt;%</c> (n a non-negative integer); <c>null</c> otherwise. Ranges are the
    /// validator's.</summary>
    public static MapFailureLimit? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var percent = text.EndsWith('%');
        var digits = percent ? text[..^1] : text;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? new MapFailureLimit(value, percent)
            : null;
    }
}

/// <summary>FR-355: <c>reduce: { agent | handler }</c>, allowed only with <c>map</c>: it gets every item output (the FR-346
/// shape). Exactly one is set.</summary>
public sealed record ReduceDefinition(string? Agent, string? Handler);

/// <summary>
/// FR-356 (0.8.0, §4.69): <c>conversation: { participants: [{ agent, role }…], opener, max_turns, stop }</c>. Two to six
/// participants, each an agent the stage binds under <c>agents:</c>, with unique roles; <see cref="Opener"/> is one of the
/// roles. <see cref="Stop"/> is optional (the conversation then stops at <see cref="MaxTurns"/>). The stage's
/// <c>output</c> is the transcript.
/// </summary>
public sealed record ConversationDefinition(IReadOnlyList<ConversationParticipant> Participants, string Opener)
{
    public const int MinParticipants = 2;
    public const int MaxParticipants = 6;
    public const int DefaultMaxTurns = 20;
    public const int MinTurns = 1;
    public const int MaxTurns = 100;

    /// <summary><c>max_turns</c>, default <see cref="DefaultMaxTurns"/>.</summary>
    public int TurnLimit { get; init; } = DefaultMaxTurns;

    public ConversationStop? Stop { get; init; }
}

/// <summary>FR-356: one participant, a bound agent key and its role name.</summary>
public sealed record ConversationParticipant(string Agent, string Role);

/// <summary>FR-356: <c>stop: { guard: &lt;name&gt; }</c> or <c>stop: { phrase: "&lt;text&gt;" }</c>; exactly one is set.</summary>
public sealed record ConversationStop(string? Guard, string? Phrase);

/// <summary>
/// FR-357 (0.8.0): a stage input <c>{ from_runs: { pipeline, artifact, window, status } }</c>: the final versions of
/// <see cref="Artifact"/> from the project's runs of <see cref="Pipeline"/> that ended with <see cref="Status"/> within
/// <see cref="Window"/> (a duration such as <c>7d</c>, as follow-ups' <c>after</c>), as a list. <see cref="Position"/> is
/// the entry's index in the stage's <c>inputs:</c> list (the plain keys are in <see cref="StageDefinition.Inputs"/>).
/// </summary>
public sealed record RunsInputDefinition(string Pipeline, string Artifact, string Window)
{
    /// <summary>§3.6: at most this many runs are collected, newest first (SPEC Q4).</summary>
    public const int MaxRuns = 1000;

    public GenericRunStatus Status { get; init; } = GenericRunStatus.Completed;

    public int Position { get; init; }
}
