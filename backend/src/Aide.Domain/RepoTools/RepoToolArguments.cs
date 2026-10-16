using System.Text.Json;
using Aide.Domain.Common;

namespace Aide.Domain.RepoTools;

/// <summary><c>list_files(path, depth≤3)</c>: <see cref="Path"/> is normalised and safe, empty for the repository root.</summary>
public sealed record ListFilesArguments(string Path, int Depth);

/// <summary><c>read_file(path, startLine?, endLine?)</c>, 1-based and inclusive.</summary>
public sealed record ReadFileArguments(string Path, int StartLine, int? EndLine);

/// <summary><c>search_code(query, pathGlob?, regex?)</c>: plain text (case-insensitive) unless <see cref="Regex"/>.</summary>
public sealed record SearchCodeArguments(string Query, string? PathGlob, bool Regex);

/// <summary>
/// Parses and validates the JSON arguments a model sends with a tool call (FR-71). A path that breaks the v1 §4.6 rules is
/// <see cref="RepoToolErrors.UnsafePath"/>; anything else malformed is <see cref="RepoToolErrors.InvalidArgument"/>. Both
/// camelCase and snake_case names are accepted (<c>startLine</c> / <c>start_line</c>).
/// </summary>
public static class RepoToolArguments
{
    public static Result<ListFilesArguments> ParseListFiles(string? json)
    {
        if (Parse(json) is not { } args)
        {
            return Invalid<ListFilesArguments>("The arguments must be a JSON object.");
        }

        if (!TryString(args, out var raw, "path"))
        {
            return Invalid<ListFilesArguments>("'path' must be a string.");
        }

        if (!TryInt(args, out var depth, "depth"))
        {
            return Invalid<ListFilesArguments>("'depth' must be an integer.");
        }

        var path = RepoToolPaths.Check(raw ?? ".", allowRoot: true);
        if (path.IsFailure)
        {
            return Result.Failure<ListFilesArguments>(path.Error!);
        }

        return Result.Success(new ListFilesArguments(path.Value, Math.Clamp(depth ?? RepoToolLimits.DefaultListDepth, 1, RepoToolLimits.MaxListDepth)));
    }

    public static Result<ReadFileArguments> ParseReadFile(string? json)
    {
        if (Parse(json) is not { } args)
        {
            return Invalid<ReadFileArguments>("The arguments must be a JSON object.");
        }

        if (!TryString(args, out var raw, "path") || string.IsNullOrWhiteSpace(raw))
        {
            return Invalid<ReadFileArguments>("'path' is required.");
        }

        if (!TryInt(args, out var start, "startLine", "start_line") || !TryInt(args, out var end, "endLine", "end_line"))
        {
            return Invalid<ReadFileArguments>("'startLine' and 'endLine' must be integers.");
        }

        if (start is < 1 || end is < 1 || (start is { } s && end is { } e && e < s))
        {
            return Invalid<ReadFileArguments>("Lines are 1-based and 'endLine' can't be before 'startLine'.");
        }

        var path = RepoToolPaths.Check(raw, allowRoot: false);
        return path.IsFailure
            ? Result.Failure<ReadFileArguments>(path.Error!)
            : Result.Success(new ReadFileArguments(path.Value, start ?? 1, end));
    }

    public static Result<SearchCodeArguments> ParseSearchCode(string? json)
    {
        if (Parse(json) is not { } args)
        {
            return Invalid<SearchCodeArguments>("The arguments must be a JSON object.");
        }

        if (!TryString(args, out var query, "query") || string.IsNullOrEmpty(query))
        {
            return Invalid<SearchCodeArguments>("'query' is required.");
        }

        if (query.Length > RepoToolLimits.MaxQueryLength)
        {
            return Invalid<SearchCodeArguments>($"'query' is longer than {RepoToolLimits.MaxQueryLength} characters.");
        }

        if (!TryString(args, out var glob, "pathGlob", "path_glob"))
        {
            return Invalid<SearchCodeArguments>("'pathGlob' must be a string.");
        }

        if (!TryBool(args, out var regex, "regex"))
        {
            return Invalid<SearchCodeArguments>("'regex' must be a boolean.");
        }

        if (!string.IsNullOrWhiteSpace(glob) && !RepoGlob.IsSafe(glob))
        {
            return Result.Failure<SearchCodeArguments>(RepoToolPaths.Unsafe(glob));
        }

        return Result.Success(new SearchCodeArguments(query, string.IsNullOrWhiteSpace(glob) ? null : RepoGlob.Normalize(glob), regex ?? false));
    }

    private static Result<T> Invalid<T>(string message) => Result.Failure<T>(new DomainError(RepoToolErrors.InvalidArgument, message));

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Find(JsonElement args, string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryString(JsonElement args, out string? value, params string[] names)
    {
        value = null;
        if (Find(args, names) is not { } element)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryInt(JsonElement args, out int? value, params string[] names)
    {
        value = null;
        if (Find(args, names) is not { } element)
        {
            return true;
        }

        // Models sometimes send numbers as strings ("12").
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            value = number;
            return true;
        }

        return false;
    }

    private static bool TryBool(JsonElement args, out bool? value, params string[] names)
    {
        value = null;
        if (Find(args, names) is not { } element)
        {
            return true;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        return false;
    }
}
