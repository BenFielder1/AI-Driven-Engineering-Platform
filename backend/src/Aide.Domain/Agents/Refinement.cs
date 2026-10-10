using System.Globalization;
using System.Text;
using System.Text.Json;
using Aide.Domain.Common;

namespace Aide.Domain.Agents;

/// <summary>A clarifying question asked by the Idea Refinement agent (FR-27).</summary>
public sealed record RefinementQuestion(string Id, string Question, string? Reason);

/// <summary>What one refinement round produced: the project request draft and the open questions (FR-27).</summary>
public sealed record RefinementResult(string ProjectRequest, IReadOnlyList<RefinementQuestion> Questions);

/// <summary>
/// An answer given for the next round (FR-27). <see cref="QuestionId"/> is set when it answers a question of the latest
/// round; <see cref="Question"/> always holds the question text the agent sees.
/// </summary>
public sealed record RefinementAnswer(string? QuestionId, string Question, string Answer);

/// <summary>An answer as submitted: a question id from the latest round, or a free question text.</summary>
public sealed record RefinementAnswerInput(string? QuestionId, string? Question, string? Answer);

/// <summary>FR-27: reading the Idea Refinement agent's reply and checking the answers given for the next round.</summary>
public static class RefinementRules
{
    public const int MaxAnswers = 20;
    public const int MaxAnswerLength = 4000;
    public const int MaxQuestionLength = 1000;

    /// <summary>Questions kept from one reply; the prompt asks for at most 7.</summary>
    public const int MaxQuestions = 20;

    /// <summary>
    /// Reads <c>{"projectRequest": "…", "questions": [{"id", "question" (or "text"), "reason"?}]}</c>. The draft must be
    /// non-empty Markdown; <c>questions</c> may be missing or empty. Question ids that are missing or repeated become
    /// <c>q1</c>, <c>q2</c>, … by position; questions beyond <see cref="MaxQuestions"/> are dropped. Anything else
    /// unreadable is <c>INVALID_OUTPUT</c>.
    /// </summary>
    public static Result<RefinementResult> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var json = AgentOutput.ExtractJsonObject(text);
        if (json is null)
        {
            return Invalid("The reply is not a JSON object.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("projectRequest", out var draft) || draft.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(draft.GetString()))
            {
                return Invalid("\"projectRequest\" must be a non-empty string with the Markdown project request.");
            }

            var questions = new List<RefinementQuestion>();
            if (root.TryGetProperty("questions", out var list) && list.ValueKind != JsonValueKind.Null)
            {
                if (list.ValueKind != JsonValueKind.Array)
                {
                    return Invalid("\"questions\" must be an array.");
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list.EnumerateArray().Take(MaxQuestions))
                {
                    var question = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object => StringOf(item, "question") ?? StringOf(item, "text"),
                        _ => null,
                    };
                    if (string.IsNullOrWhiteSpace(question))
                    {
                        return Invalid($"Question {questions.Count + 1} has no text.");
                    }

                    var id = item.ValueKind == JsonValueKind.Object ? StringOf(item, "id")?.Trim() : null;
                    if (string.IsNullOrEmpty(id) || id.Length > 20 || !ids.Add(id))
                    {
                        id = "q" + (questions.Count + 1).ToString(CultureInfo.InvariantCulture);
                        ids.Add(id);
                    }

                    var reason = item.ValueKind == JsonValueKind.Object ? StringOf(item, "reason")?.Trim() : null;
                    questions.Add(new RefinementQuestion(id, Cap(question.Trim(), MaxQuestionLength), string.IsNullOrEmpty(reason) ? null : Cap(reason, MaxQuestionLength)));
                }
            }

            return Result.Success(new RefinementResult(AgentOutput.UnwrapFence(draft.GetString()!), questions));
        }
        catch (JsonException ex)
        {
            return Invalid($"The reply is not valid JSON ({ex.Message}).");
        }
    }

    /// <summary>
    /// FR-27: the answers for the next round. Each needs a non-blank answer (at most <see cref="MaxAnswerLength"/>
    /// characters) and either the id of a question of the latest round (<paramref name="latestQuestions"/>) or the
    /// question text; at most <see cref="MaxAnswers"/>, each question answered once. Failures are <c>VALIDATION</c>
    /// with <c>answers[i].field: message</c> details.
    /// </summary>
    public static Result<IReadOnlyList<RefinementAnswer>> ResolveAnswers(IReadOnlyList<RefinementAnswerInput>? inputs, IReadOnlyList<RefinementQuestion> latestQuestions)
    {
        ArgumentNullException.ThrowIfNull(latestQuestions);
        if (inputs is null || inputs.Count == 0)
        {
            return Result.Success<IReadOnlyList<RefinementAnswer>>([]);
        }

        var errors = new List<string>();
        if (inputs.Count > MaxAnswers)
        {
            errors.Add($"answers: At most {MaxAnswers} answers can be given in one round.");
        }

        var byId = latestQuestions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var answers = new List<RefinementAnswer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var key = $"answers[{i.ToString(CultureInfo.InvariantCulture)}]";
            var answer = input.Answer?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                errors.Add($"{key}.answer: The answer can't be empty.");
            }
            else if (answer.Length > MaxAnswerLength)
            {
                errors.Add($"{key}.answer: The answer can be at most {MaxAnswerLength} characters.");
            }

            string? questionText = null;
            var questionId = string.IsNullOrWhiteSpace(input.QuestionId) ? null : input.QuestionId.Trim();
            if (questionId is not null)
            {
                if (byId.TryGetValue(questionId, out var asked))
                {
                    questionText = asked.Question;
                }
                else
                {
                    errors.Add($"{key}.questionId: '{questionId}' is not a question of the latest round.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(input.Question))
            {
                questionText = input.Question.Trim();
                if (questionText.Length > MaxQuestionLength)
                {
                    errors.Add($"{key}.question: The question can be at most {MaxQuestionLength} characters.");
                }
            }
            else
            {
                errors.Add($"{key}.questionId: Give the id of the question or the question text.");
            }

            if (questionText is not null && !seen.Add(questionId ?? questionText))
            {
                errors.Add($"{key}.questionId: The question is answered more than once.");
            }

            if (questionText is not null && !string.IsNullOrEmpty(answer))
            {
                answers.Add(new RefinementAnswer(questionId, questionText, answer));
            }
        }

        return errors.Count == 0
            ? Result.Success<IReadOnlyList<RefinementAnswer>>(answers)
            : Result.Failure<IReadOnlyList<RefinementAnswer>>(new DomainError(ErrorCodes.Validation, "One or more answers are invalid.") { Details = errors });
    }

    /// <summary>
    /// The answers as the agent reads them (<c>{{ANSWERS}}</c>) and as stored on the run's <c>user_feedback</c>:
    /// <c>Q (q1): …</c> / <c>A: …</c> pairs, or <c>null</c> when there are none.
    /// </summary>
    public static string? FormatAnswers(IReadOnlyList<RefinementAnswer>? answers)
    {
        if (answers is null || answers.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var answer in answers)
        {
            sb.Append('Q');
            if (answer.QuestionId is { } id)
            {
                sb.Append(" (").Append(id).Append(')');
            }

            sb.Append(": ").Append(OneLine(answer.Question)).Append('\n');
            sb.Append("A: ").Append(answer.Answer.Trim()).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>What the run stores as its feedback: the answers, then any reviewer feedback.</summary>
    public static string? RunFeedback(IReadOnlyList<RefinementAnswer>? answers, string? feedback)
    {
        var formatted = FormatAnswers(answers);
        var parts = new List<string>();
        if (formatted is not null)
        {
            parts.Add("Answers:\n" + formatted);
        }

        if (!string.IsNullOrWhiteSpace(feedback))
        {
            parts.Add("Feedback:\n" + feedback.Trim());
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string? StringOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;

    private static Result<RefinementResult> Invalid(string reason) =>
        Result.Failure<RefinementResult>(new DomainError(ErrorCodes.InvalidOutput, $"The idea refinement reply can't be read: {reason}") { Details = [reason] });
}
