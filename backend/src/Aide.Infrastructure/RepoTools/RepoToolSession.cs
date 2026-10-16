using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.RepoTools;

/// <summary>
/// One logged repo tool call (FR-71), as stored in <c>agent_runs.tool_calls</c>: the tool, its arguments (secret-redacted,
/// as a JSON object when they parse, else the raw text), the bytes returned to the model, whether the result was
/// truncated, and the error code when the call failed.
/// </summary>
public sealed record RepoToolCallRecord(string Tool, JsonNode? Arguments, int Bytes, bool Truncated, string? Error);

/// <summary>
/// The repo tools of one agent run (FR-71, NFR-SEC2), over a credential-free snapshot directory
/// (<see cref="IRepoSnapshotProvider"/>): <c>list_files</c>, <c>read_file</c> and <c>search_code</c>.
/// <list type="bullet">
/// <item>Arguments are validated by <see cref="RepoToolArguments"/>; a path breaking the v1 §4.6 rules, resolving outside the
/// snapshot or through a symbolic link is <c>UNSAFE_PATH</c> and nothing is read.</item>
/// <item><c>.git/</c> and <c>.aide/</c> are hidden from every tool (<see cref="RepoToolPaths.IsHidden"/>).</item>
/// <item>File content passes through the secret scanner (<see cref="SecretScanner.Redact"/>) before it is returned or searched
/// (a regex can't probe a secret), so flagged lines reach the model as <c>[REDACTED:…]</c>.</item>
/// <item>Per run: <see cref="RepoToolLimits.MaxCallsPerRun"/> calls and <see cref="RepoToolLimits.MaxBytesPerRun"/> bytes of
/// output (<see cref="RepoToolBudget"/>); the next call returns <c>TOOL_LIMIT</c>.</item>
/// <item>Every call, refused ones included, is logged in <see cref="Calls"/>; <see cref="ToJson"/> is the run's
/// <c>tool_calls</c> value.</item>
/// </list>
/// One run calls its tools in order; the session is not thread-safe.
/// </summary>
public sealed class RepoToolSession : ILlmToolExecutor
{
    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly List<RepoToolCallRecord> _calls = [];

    /// <param name="root">The snapshot directory (<see cref="RepoSnapshot.Root"/>).</param>
    /// <param name="time">Clock for the search time limit.</param>
    /// <param name="budget">The run's limits (defaults: 25 calls, 400 KB).</param>
    public RepoToolSession(string root, TimeProvider? time = null, RepoToolBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException("The repository snapshot does not exist.");
        }

        _time = time ?? TimeProvider.System;
        Budget = budget ?? new RepoToolBudget();
    }

    public RepoToolBudget Budget { get; }

    public IReadOnlyList<LlmToolDefinition> Definitions => RepoToolDefinitions.All;

    public bool LimitReached { get; private set; }

    public IReadOnlyList<RepoToolCallRecord> Calls => _calls;

    /// <summary>The call log as the JSON array stored in <c>agent_runs.tool_calls</c> (<c>[]</c> when none).</summary>
    public string ToJson() => JsonSerializer.Serialize(_calls, LogJson);

    public async Task<LlmToolResult> ExecuteAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var arguments = LoggedArguments(toolCall.ArgumentsJson);
        if (!Budget.TryBeginCall())
        {
            LimitReached = true;
            var message = Budget.CallsUsed >= Budget.MaxCalls
                ? $"This run already made {Budget.MaxCalls} tool calls, the limit. Answer now with what you have."
                : $"This run already read {Budget.MaxBytes / 1024} KB through tools, the limit. Answer now with what you have.";
            return Record(toolCall, arguments, new Outcome(Error(RepoToolErrors.ToolLimit, message), RepoToolErrors.ToolLimit, false));
        }

        Outcome outcome;
        try
        {
            outcome = toolCall.Name switch
            {
                RepoToolNames.ListFiles => ListFiles(toolCall.ArgumentsJson),
                RepoToolNames.ReadFile => await ReadFileAsync(toolCall.ArgumentsJson, cancellationToken),
                RepoToolNames.SearchCode => await SearchCodeAsync(toolCall.ArgumentsJson, cancellationToken),
                _ => Failed(RepoToolErrors.InvalidArgument, $"Unknown tool '{Shorten(toolCall.Name, 60)}'. Use {string.Join(", ", RepoToolNames.All)}."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The snapshot is ours, so this is rare (a file vanished, a permission): the model gets a plain failure.
            outcome = Failed(RepoToolErrors.NotFound, "The path could not be read.");
        }

        return Record(toolCall, arguments, outcome);
    }

    /// <summary>For a provider without tool support: a depth-2 listing of the repository, not counted against the budget.</summary>
    public Task<string?> FallbackContextAsync(CancellationToken cancellationToken)
    {
        var (lines, truncated) = List(string.Empty, RepoToolLimits.DefaultListDepth);
        var text = new StringBuilder("<repository_files>\n");
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        if (truncated)
        {
            text.Append($"… (truncated at {RepoToolLimits.MaxListEntries} entries)\n");
        }

        text.Append("</repository_files>");
        return Task.FromResult<string?>(text.ToString());
    }

    // ------------------------------------------------------------------ tools

    private Outcome ListFiles(string json)
    {
        var parsed = RepoToolArguments.ParseListFiles(json);
        if (parsed.IsFailure)
        {
            return Failed(parsed.Error!);
        }

        var args = parsed.Value;
        if (args.Path.Length > 0)
        {
            if (RepoToolPaths.IsHidden(args.Path))
            {
                return Failed(RepoToolErrors.NotFound, $"'{args.Path}' does not exist.");
            }

            if (!TryResolve(args.Path, out var full, out var unsafePath))
            {
                return unsafePath!;
            }

            if (File.Exists(full))
            {
                return Ok($"{args.Path}  ({new FileInfo(full).Length} bytes)\n", truncated: false);
            }

            if (!Directory.Exists(full))
            {
                return Failed(RepoToolErrors.NotFound, $"'{args.Path}' does not exist.");
            }
        }

        var (lines, truncated) = List(args.Path, args.Depth);
        var header = $"Entries under '{(args.Path.Length == 0 ? "." : args.Path)}' (depth {args.Depth}): {lines.Count}{(truncated ? $", truncated at {RepoToolLimits.MaxListEntries}" : string.Empty)}\n";
        return Ok(header + string.Join('\n', lines) + (lines.Count > 0 ? "\n" : string.Empty), truncated);
    }

    private async Task<Outcome> ReadFileAsync(string json, CancellationToken cancellationToken)
    {
        var parsed = RepoToolArguments.ParseReadFile(json);
        if (parsed.IsFailure)
        {
            return Failed(parsed.Error!);
        }

        var args = parsed.Value;
        if (RepoToolPaths.IsHidden(args.Path))
        {
            return Failed(RepoToolErrors.NotFound, $"'{args.Path}' does not exist.");
        }

        if (!TryResolve(args.Path, out var full, out var unsafePath))
        {
            return unsafePath!;
        }

        if (Directory.Exists(full))
        {
            return Failed(RepoToolErrors.InvalidArgument, $"'{args.Path}' is a directory; use list_files.");
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return Failed(RepoToolErrors.NotFound, $"'{args.Path}' does not exist.");
        }

        if (info.Length > RepoToolLimits.MaxReadableFileBytes)
        {
            return Failed(RepoToolErrors.FileTooLarge, $"'{args.Path}' is {info.Length} bytes, larger than the {RepoToolLimits.MaxReadableFileBytes / (1024 * 1024)} MB tools can read.");
        }

        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        if (IsBinary(bytes))
        {
            return Ok($"'{args.Path}' is a binary file ({bytes.Length} bytes); its content is not shown.\n", truncated: false);
        }

        var content = SecretScanner.Redact(Decode(bytes));
        var maxBytes = Math.Min(RepoToolLimits.MaxReadBytes, Math.Max(0, Budget.RemainingBytes - 200));
        var slice = RepoFileSlice.Take(content, args.StartLine, args.EndLine, RepoToolLimits.MaxReadLines, maxBytes);
        var range = slice.LastLine >= slice.FirstLine ? $"lines {slice.FirstLine}-{slice.LastLine}" : $"no lines from {slice.FirstLine}";
        var header = $"{args.Path} ({range} of {slice.TotalLines}{(slice.Truncated ? $"; truncated at {RepoToolLimits.MaxReadLines} lines / {maxBytes / 1024} KB, ask for the next range" : string.Empty)})\n";
        return Ok(header + slice.Text, slice.Truncated);
    }

    private async Task<Outcome> SearchCodeAsync(string json, CancellationToken cancellationToken)
    {
        var parsed = RepoToolArguments.ParseSearchCode(json);
        if (parsed.IsFailure)
        {
            return Failed(parsed.Error!);
        }

        var args = parsed.Value;
        Regex? regex = null;
        var backtracking = false;
        if (args.Regex)
        {
            // Linear-time matching where the pattern allows it; otherwise backtracking, bounded by the search budget.
            try
            {
                regex = new Regex(args.Query, SearchRegexOptions | RegexOptions.NonBacktracking, RepoToolLimits.SearchTimeout);
            }
            catch (NotSupportedException)
            {
                backtracking = true;
            }
            catch (ArgumentException)
            {
                return Failed(RepoToolErrors.InvalidArgument, "'query' is not a valid regular expression.");
            }

            if (backtracking)
            {
                try
                {
                    regex = new Regex(args.Query, SearchRegexOptions, RepoToolLimits.SearchTimeout);
                }
                catch (ArgumentException)
                {
                    return Failed(RepoToolErrors.InvalidArgument, "'query' is not a valid regular expression.");
                }
            }
        }

        var glob = args.PathGlob is { } g ? RepoGlob.ToRegex(g) : null;
        var globOnName = args.PathGlob is { } p && !p.Contains('/', StringComparison.Ordinal);
        var started = _time.GetTimestamp();
        var matches = new List<string>();
        var truncated = false;
        var scanned = 0;
        foreach (var (relative, full) in Files(string.Empty, int.MaxValue))
        {
            if (glob is not null && !glob.IsMatch(globOnName ? relative[(relative.LastIndexOf('/') + 1)..] : relative))
            {
                continue;
            }

            if (++scanned > RepoToolLimits.MaxSearchFiles || _time.GetElapsedTime(started) > RepoToolLimits.SearchTimeout)
            {
                truncated = true;
                break;
            }

            var info = new FileInfo(full);
            if (info.Length > RepoToolLimits.MaxSearchFileBytes)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
            if (IsBinary(bytes))
            {
                continue;
            }

            // Searched after redaction: a pattern can't find (or probe) a secret.
            var lines = SecretScanner.Redact(Decode(bytes)).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                // The whole search shares one budget, checked on every line (not only between files).
                var remaining = RepoToolLimits.SearchTimeout - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return TimedOut(matches);
                }

                if (backtracking && regex is not null && remaining < regex.MatchTimeout / 2)
                {
                    // A backtracking match may take at most what is left of the budget (rebuilt as the budget halves).
                    regex = new Regex(args.Query, SearchRegexOptions, remaining);
                }

                bool hit;
                try
                {
                    hit = regex?.IsMatch(lines[i]) ?? lines[i].Contains(args.Query, StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException)
                {
                    return TimedOut(matches);
                }

                if (!hit)
                {
                    continue;
                }

                if (matches.Count == RepoToolLimits.MaxSearchMatches)
                {
                    truncated = true;
                    break;
                }

                matches.Add($"{relative}:{i + 1}: {Shorten(lines[i].Trim(), RepoToolLimits.MaxMatchLineLength)}");
            }

            if (truncated)
            {
                break;
            }

            if (_time.GetElapsedTime(started) > RepoToolLimits.SearchTimeout)
            {
                truncated = true;
                break;
            }
        }

        var header = $"{matches.Count} match{(matches.Count == 1 ? string.Empty : "es")} for '{Shorten(args.Query, 80)}'{(args.PathGlob is null ? string.Empty : $" in '{args.PathGlob}'")}{(truncated ? $" (truncated: at most {RepoToolLimits.MaxSearchMatches} matches, {RepoToolLimits.MaxSearchFiles} files, {RepoToolLimits.SearchTimeout.TotalSeconds:0} s)" : string.Empty)}\n";
        return Ok(header + string.Join('\n', matches) + (matches.Count > 0 ? "\n" : string.Empty), truncated);
    }

    private const RegexOptions SearchRegexOptions = RegexOptions.CultureInvariant | RegexOptions.Multiline;

    /// <summary><c>SEARCH_TIMEOUT</c> with the matches found before the budget ran out.</summary>
    private static Outcome TimedOut(List<string> matches)
    {
        var message = Error(
            RepoToolErrors.SearchTimeout,
            $"The search ran out of its {RepoToolLimits.SearchTimeout.TotalSeconds:0} s budget; simplify the regular expression, narrow pathGlob or use a plain-text query. {matches.Count} match{(matches.Count == 1 ? string.Empty : "es")} found before that:");
        return new Outcome(message + "\n" + string.Join('\n', matches) + (matches.Count > 0 ? "\n" : string.Empty), RepoToolErrors.SearchTimeout, true);
    }

    // ------------------------------------------------------------------ file system

    /// <summary>Listing lines (directories end with <c>/</c>, files show their size), breadth by directory, sorted.</summary>
    private (List<string> Lines, bool Truncated) List(string start, int depth)
    {
        var lines = new List<string>();
        var truncated = false;
        Walk(start, 1);
        return (lines, truncated);

        void Walk(string relativeDirectory, int level)
        {
            foreach (var entry in Entries(relativeDirectory))
            {
                if (lines.Count == RepoToolLimits.MaxListEntries)
                {
                    truncated = true;
                    return;
                }

                lines.Add(entry.IsDirectory ? entry.Relative + "/" : $"{entry.Relative}  ({entry.Length} bytes)");
                if (entry.IsDirectory && level < depth)
                {
                    Walk(entry.Relative, level + 1);
                    if (truncated)
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Every visible file under <paramref name="start"/>, depth-first, sorted.</summary>
    private IEnumerable<(string Relative, string Full)> Files(string start, int depth, int level = 1)
    {
        foreach (var entry in Entries(start))
        {
            if (!entry.IsDirectory)
            {
                yield return (entry.Relative, entry.Full);
            }
            else if (level < depth)
            {
                foreach (var file in Files(entry.Relative, depth, level + 1))
                {
                    yield return file;
                }
            }
        }
    }

    private sealed record Entry(string Relative, string Full, bool IsDirectory, long Length);

    /// <summary>The visible entries of a directory: no links, nothing hidden, sorted ordinally (directories and files mixed).</summary>
    private List<Entry> Entries(string relativeDirectory)
    {
        var directory = relativeDirectory.Length == 0 ? _root : Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var entries = new List<Entry>();
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var relative = relativeDirectory.Length == 0 ? info.Name : relativeDirectory + "/" + info.Name;
            if (RepoToolPaths.IsHidden(relative))
            {
                continue;
            }

            entries.Add(info is DirectoryInfo
                ? new Entry(relative, info.FullName, true, 0)
                : new Entry(relative, info.FullName, false, ((FileInfo)info).Length));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return entries;
    }

    /// <summary>The full path inside the snapshot (no link on the way, never <c>.git</c>), else an <c>UNSAFE_PATH</c> outcome.</summary>
    private bool TryResolve(string relative, out string full, out Outcome? unsafePath)
    {
        try
        {
            full = WorkingCopies.Resolve(_root, relative, allowProtected: true);
            unsafePath = null;
            return true;
        }
        catch (WorkingCopyException)
        {
            full = string.Empty;
            unsafePath = Failed(RepoToolPaths.Unsafe(relative));
            return false;
        }
    }

    private static bool IsBinary(byte[] bytes) => Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0;

    private static string Decode(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);

    // ------------------------------------------------------------------ results and the log

    private sealed record Outcome(string Content, string? ErrorCode, bool Truncated);

    private static Outcome Ok(string content, bool truncated) => new(content, null, truncated);

    private static Outcome Failed(DomainError error) => Failed(error.Code, error.Message);

    private static Outcome Failed(string code, string message) => new(Error(code, message), code, false);

    private static string Error(string code, string message) => $"{code}: {message}";

    private LlmToolResult Record(LlmToolCall call, JsonNode? arguments, Outcome outcome)
    {
        var content = outcome.Content;
        var truncated = outcome.Truncated;
        if (outcome.ErrorCode is null)
        {
            // The run's byte budget: whatever doesn't fit is cut (on a character boundary).
            var size = Encoding.UTF8.GetByteCount(content);
            if (size > Budget.RemainingBytes)
            {
                content = CutToBytes(content, Budget.RemainingBytes);
                truncated = true;
            }
        }

        var bytes = Encoding.UTF8.GetByteCount(content);
        if (outcome.ErrorCode is null)
        {
            Budget.Consume(bytes);
        }

        _calls.Add(new RepoToolCallRecord(Shorten(call.Name, 60), arguments, outcome.ErrorCode is null ? bytes : 0, truncated, outcome.ErrorCode));
        return new LlmToolResult(call.Id, content, outcome.ErrorCode is not null);
    }

    /// <summary>The arguments as logged: secret-redacted, shortened, a JSON object when they parse.</summary>
    private static JsonNode? LoggedArguments(string json)
    {
        var redacted = SecretScanner.Redact(json ?? string.Empty);
        if (redacted.Length <= RepoToolLimits.MaxLoggedArgumentLength)
        {
            try
            {
                if (JsonNode.Parse(redacted) is JsonObject parsed)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Logged as text below.
            }
        }

        return JsonValue.Create(Shorten(redacted, RepoToolLimits.MaxLoggedArgumentLength));
    }

    private static string CutToBytes(string text, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var encoder = Encoding.UTF8;
        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && encoder.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length--;
        }

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
