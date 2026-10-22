namespace Aide.Domain.RepoTools;

/// <summary>The read-only repo tools agents may call (FR-71, D-V2-9).</summary>
public static class RepoToolNames
{
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string SearchCode = "search_code";

    public static IReadOnlyList<string> All { get; } = [ListFiles, ReadFile, SearchCode];
}

/// <summary>
/// Error codes a repo tool returns to the model (FR-71). They are tool results, not HTTP errors: the run goes on and the
/// model decides what to do next.
/// </summary>
public static class RepoToolErrors
{
    /// <summary>The path breaks the v1 §4.6 path rules or escapes the snapshot; nothing was read.</summary>
    public const string UnsafePath = "UNSAFE_PATH";

    /// <summary>The run used its 25 calls or 400 KB; the agent must answer with what it has.</summary>
    public const string ToolLimit = "TOOL_LIMIT";

    /// <summary>No such file or directory (hidden paths such as <c>.aide/</c> look missing too).</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>The arguments are missing, of the wrong type, or the tool name is unknown.</summary>
    public const string InvalidArgument = "INVALID_ARGUMENT";

    /// <summary>A regular-expression search took longer than its time limit.</summary>
    public const string SearchTimeout = "SEARCH_TIMEOUT";

    /// <summary>The file is larger than <see cref="RepoToolLimits.MaxReadableFileBytes"/>; nothing was read.</summary>
    public const string FileTooLarge = "FILE_TOO_LARGE";
}

/// <summary>Per-call and per-run limits of the repo tools (FR-71).</summary>
public static class RepoToolLimits
{
    /// <summary>Tool calls per run; the next call returns <see cref="RepoToolErrors.ToolLimit"/>.</summary>
    public const int MaxCallsPerRun = 25;

    /// <summary>Bytes of tool output per run (400 KB); every tool's output counts.</summary>
    public const int MaxBytesPerRun = 400 * 1024;

    public const int MaxReadLines = 400;

    /// <summary>Bytes per <c>read_file</c> call (64 KB).</summary>
    public const int MaxReadBytes = 64 * 1024;

    public const int MaxListDepth = 3;

    public const int DefaultListDepth = 2;

    /// <summary>Entries one <c>list_files</c> call returns.</summary>
    public const int MaxListEntries = 500;

    public const int MaxSearchMatches = 50;

    public const int MaxQueryLength = 500;

    /// <summary>A matched line is shortened to this many characters.</summary>
    public const int MaxMatchLineLength = 240;

    /// <summary>Files larger than this can't be read by <c>read_file</c> (8 MB): their secrets couldn't be scanned cheaply.</summary>
    public const long MaxReadableFileBytes = 8 * 1024 * 1024;

    /// <summary>Files larger than this are skipped by <c>search_code</c>.</summary>
    public const long MaxSearchFileBytes = 1024 * 1024;

    /// <summary>Files <c>search_code</c> scans per call.</summary>
    public const int MaxSearchFiles = 5000;

    /// <summary>Wall-clock limit of one regular-expression search.</summary>
    public static TimeSpan SearchTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Arguments longer than this are shortened in the run's tool-call log.</summary>
    public const int MaxLoggedArgumentLength = 2000;
}

/// <summary>
/// The per-run budget (FR-71): at most <see cref="RepoToolLimits.MaxCallsPerRun"/> calls and
/// <see cref="RepoToolLimits.MaxBytesPerRun"/> bytes of output. Not thread-safe: one run calls its tools in order.
/// </summary>
public sealed class RepoToolBudget(int maxCalls = RepoToolLimits.MaxCallsPerRun, int maxBytes = RepoToolLimits.MaxBytesPerRun)
{
    public int CallsUsed { get; private set; }

    public int BytesUsed { get; private set; }

    public int MaxCalls { get; } = maxCalls;

    public int MaxBytes { get; } = maxBytes;

    public int RemainingBytes => Math.Max(0, MaxBytes - BytesUsed);

    /// <summary>Whether the calls or bytes are used up (the next call is refused).</summary>
    public bool Exhausted => CallsUsed >= MaxCalls || RemainingBytes == 0;

    /// <summary>
    /// Counts a call. Returns <c>false</c> (the call must return <see cref="RepoToolErrors.ToolLimit"/>) when the run already
    /// made <see cref="MaxCalls"/> calls or read <see cref="MaxBytes"/> bytes; a refused call isn't counted.
    /// </summary>
    public bool TryBeginCall()
    {
        if (Exhausted)
        {
            return false;
        }

        CallsUsed++;
        return true;
    }

    /// <summary>Records <paramref name="bytes"/> of output (capped at what is left).</summary>
    public void Consume(int bytes) => BytesUsed = Math.Min(MaxBytes, BytesUsed + Math.Max(0, bytes));
}
