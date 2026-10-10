using System.Globalization;
using System.Text;
using Aide.Cli.Api;

namespace Aide.Cli.Commands;

/// <summary>Plain-text formatting helpers (no colour, so output stays pipe-friendly).</summary>
internal static class Output
{
    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var allRows = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, allRows.Count == 0 ? 0 : allRows.Max(r => r[i].Length))).ToArray();
        var builder = new StringBuilder();
        AppendRow(builder, headers, widths);
        foreach (var row in allRows)
        {
            AppendRow(builder, row, widths);
        }

        return builder.ToString().TrimEnd('\n', '\r');
    }

    public static string KeyValues(IEnumerable<(string Key, string Value)> pairs)
    {
        var list = pairs.ToList();
        var width = list.Max(p => p.Key.Length);
        return string.Join(Environment.NewLine, list.Select(p => $"{(p.Key + ":").PadRight(width + 2)}{p.Value}"));
    }

    public static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset? value) => value is { } v ? Time(v) : "-";

    public static string Num(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    public static string Num(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    public static string Actor(ActorDto? actor) => actor?.Name ?? "-";

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells, int[] widths)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            builder.Append(i == cells.Count - 1 ? cells[i] : cells[i].PadRight(widths[i] + 2));
        }

        builder.Append('\n');
    }
}

/// <summary>Prints failures to stderr: ProblemDetails title, errorCode, detail and individual reasons.</summary>
internal static class ErrorReporter
{
    public static async Task ReportAsync(TextWriter error, CliException exception)
    {
        if (exception is not ApiException api)
        {
            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return;
        }

        var problem = api.Problem;
        var code = string.IsNullOrEmpty(api.ErrorCode) ? string.Empty : $" [{api.ErrorCode}]";
        await error.WriteLineAsync($"error: {api.Message}{code}").ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(problem?.Detail))
        {
            await error.WriteLineAsync($"  {problem.Detail}").ConfigureAwait(false);
        }

        foreach (var detail in problem?.Details ?? [])
        {
            await error.WriteLineAsync($"  - {detail}").ConfigureAwait(false);
        }

        foreach (var (field, messages) in problem?.Errors ?? new Dictionary<string, string[]>())
        {
            foreach (var message in messages)
            {
                await error.WriteLineAsync($"  - {field}: {message}").ConfigureAwait(false);
            }
        }

        if (api.RetryAfter is { } retryAfter)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            await error.WriteLineAsync($"  Retry after {seconds.ToString(CultureInfo.InvariantCulture)}s.").ConfigureAwait(false);
        }

        if (api.ExitCode == ExitCodes.Auth && api.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await error.WriteLineAsync("  The token is missing, revoked or expired. Run 'aide login --token <token>'.").ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(problem?.TraceId))
        {
            await error.WriteLineAsync($"  traceId: {problem.TraceId}").ConfigureAwait(false);
        }
    }
}
