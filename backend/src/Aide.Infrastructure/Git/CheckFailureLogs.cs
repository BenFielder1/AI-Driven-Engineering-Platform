using System.Globalization;
using System.Text;
using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.Infrastructure.Git;

/// <summary>FR-236: a failed check's excerpt (§4.38, built right after its fetch), or <c>null</c>, and why parts are missing.</summary>
public sealed record CheckFailureOutcome(CheckObservation Check, FailureExcerptResult? Excerpt, IReadOnlyList<string> Warnings);

/// <summary>
/// FR-236, SPEC §8 performance: the limits of a failed check's log download. Each fetch has its own 10-second timeout and
/// keeps the <b>last</b> 5 MB of the log (the end, where the error and the summary are); raw logs are never stored, only
/// the excerpt built from them right after the fetch (§4.38).
/// </summary>
public static class CheckFailureLogs
{
    /// <summary>The most bytes kept from one log (its end).</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>The timeout of one failed check's fetch (one part of it, for GitHub).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The note that starts a log cut to its last <see cref="MaxBytes"/>.</summary>
    public static string CutNote(long omittedBytes) => string.Create(CultureInfo.InvariantCulture, $"… (log cut: the first {omittedBytes} bytes omitted)");

    /// <summary>
    /// Reads <paramref name="content"/> as UTF-8 text, keeping only its last <paramref name="maxBytes"/> bytes while streaming
    /// (a ring of chunks). A cut log starts with <see cref="CutNote"/> and drops its first, partial line.
    /// </summary>
    public static async Task<string> ReadTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await ReadTailAsync(stream, maxBytes, cancellationToken);
    }

    /// <summary>The last <paramref name="maxBytes"/> bytes of <paramref name="stream"/> as text (see <see cref="ReadTextAsync"/>).</summary>
    public static async Task<string> ReadTailAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var chunks = new LinkedList<byte[]>();
        long kept = 0;
        long omitted = 0;
        while (true)
        {
            var chunk = new byte[Math.Min(81_920, maxBytes)];
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            chunks.AddLast(read == chunk.Length ? chunk : chunk[..read]);
            kept += read;
            while (kept - chunks.First!.Value.Length >= maxBytes)
            {
                kept -= chunks.First.Value.Length;
                omitted += chunks.First.Value.Length;
                chunks.RemoveFirst();
            }
        }

        var bytes = new byte[kept];
        var at = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(bytes, at);
            at += chunk.Length;
        }

        var skip = (int)Math.Max(0, kept - maxBytes);
        omitted += skip;
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes, skip, bytes.Length - skip);
        if (omitted == 0)
        {
            return text;
        }

        var firstBreak = text.IndexOf('\n', StringComparison.Ordinal);
        return CutNote(omitted) + "\n" + (firstBreak < 0 ? text : text[(firstBreak + 1)..]);
    }

    /// <summary>
    /// FR-236: fetches each failed check in parallel and builds its excerpt right after its fetch (so whole logs aren't kept).
    /// With <paramref name="timeout"/>, each check's fetch is cancelled after it; a timeout or any failure becomes a warning
    /// and that check keeps its name and URL evidence. Only the caller's own cancellation is thrown.
    /// </summary>
    public static async Task<IReadOnlyList<CheckFailureOutcome>> FanOutAsync(
        IReadOnlyList<CheckObservation> checks,
        Func<CheckObservation, CancellationToken, Task<(CheckFailureDetail Detail, IReadOnlyList<string> Warnings)>> fetch,
        Func<CheckObservation, CheckFailureDetail, FailureExcerptResult?> excerpt,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(excerpt);

        async Task<CheckFailureOutcome> OneAsync(CheckObservation check)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } after)
            {
                limit.CancelAfter(after);
            }

            try
            {
                var (detail, warnings) = await fetch(check, limit.Token);
                return new CheckFailureOutcome(check, excerpt(check, detail), warnings);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CheckFailureOutcome(check, null, [string.Create(CultureInfo.InvariantCulture, $"the fetch timed out after {(timeout ?? Timeout).TotalSeconds:0} seconds")]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new CheckFailureOutcome(check, null, [ex.GetType().Name]);
            }
        }

        return await Task.WhenAll(checks.Select(OneAsync));
    }
}
