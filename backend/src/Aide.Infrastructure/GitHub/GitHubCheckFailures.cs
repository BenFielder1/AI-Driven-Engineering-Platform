using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.GitHub;

/// <summary>FR-236: what GitHub told about a failed check, and why any part of it is missing (token-free warnings).</summary>
public sealed record GitHubCheckFailure(CheckFailureDetail Detail, IReadOnlyList<string> Warnings);

/// <summary>
/// FR-236: the details of a failed required check run: its output (<c>GET /check-runs/{id}</c>: <c>output.title</c>,
/// <c>output.summary</c>), up to 50 annotations (<c>GET /check-runs/{id}/annotations</c>) and, for a GitHub Actions check run
/// (whose id is the job id), the job log (<c>GET /actions/jobs/{id}/logs</c>, a redirect to the log file, read up to 5 MB).
/// Each part is read on its own: a 403 (e.g. the App lacks <c>actions: read</c>, SPEC Q1), 404 or other failure leaves that
/// part out with a warning and never fails the review. A commit status (no check-run id) has no detail.
/// </summary>
public sealed partial class GitHubApi
{
    /// <summary>The most annotations read per check run (FR-236).</summary>
    public const int MaxCheckAnnotations = 50;

    /// <summary>The GitHub App slug of GitHub Actions check runs.</summary>
    public const string ActionsAppSlug = "github-actions";

    /// <param name="token">The project's GitHub token.</param>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="check">The failed check run.</param>
    /// <param name="cancellationToken">The caller's cancellation (not a timeout).</param>
    /// <param name="partTimeout">Each part's own timeout (<see cref="CheckFailureLogs.Timeout"/> by default): the parts are read in
    /// parallel, and a slow part (the log) doesn't lose the parts already read.</param>
    public async Task<GitHubCheckFailure> GetCheckFailureAsync(
        string token, string owner, string repo, CheckObservation check, CancellationToken cancellationToken, TimeSpan? partTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!long.TryParse(check.RunId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
        {
            return new GitHubCheckFailure(CheckFailureDetail.Empty, []);
        }

        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var id = runId.ToString(CultureInfo.InvariantCulture);
        var path = RepoPath(owner, repo);
        var limit = partTimeout ?? CheckFailureLogs.Timeout;

        var output = PartAsync<(string? Title, string? Summary)>("check run output", async ct =>
        {
            using var json = await SendAsync(HttpMethod.Get, $"{path}/check-runs/{id}", token, null, ct);
            return json.RootElement.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object ? (Text(o, "title"), Text(o, "summary")) : (null, null);
        });

        var annotations = PartAsync<IReadOnlyList<FailureAnnotation>>("annotations", async ct =>
        {
            using var json = await SendAsync(
                HttpMethod.Get, $"{path}/check-runs/{id}/annotations?per_page={MaxCheckAnnotations.ToString(CultureInfo.InvariantCulture)}", token, null, ct);
            return json.RootElement.ValueKind != JsonValueKind.Array
                ? []
                :
                [
                    .. json.RootElement.EnumerateArray()
                        .Where(a => a.ValueKind == JsonValueKind.Object && Text(a, "path") is { Length: > 0 } && Text(a, "message") is { Length: > 0 })
                        .Take(MaxCheckAnnotations)
                        .Select(a => new FailureAnnotation(
                            Text(a, "path")!,
                            a.TryGetProperty("start_line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var number) ? number : null,
                            Text(a, "annotation_level"),
                            Text(a, "title") is { Length: > 0 } t && !Text(a, "message")!.Contains(t, StringComparison.Ordinal) ? $"{t}: {Text(a, "message")}" : Text(a, "message")!)),
                ];
        });

        var log = string.Equals(check.App, ActionsAppSlug, StringComparison.Ordinal)
            ? PartAsync<string?>("job log", ct => GetLogAsync($"{path}/actions/jobs/{id}/logs", token, ct)!)
            : Task.FromResult<string?>(null);

        await Task.WhenAll(output, annotations, log);
        var (title, summary) = await output;
        return new GitHubCheckFailure(new CheckFailureDetail(title, summary, await annotations ?? [], await log), [.. warnings]);

        async Task<T?> PartAsync<T>(string part, Func<CancellationToken, Task<T>> read)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limit);
            try
            {
                return await read(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Enqueue($"GitHub didn't give the {part} of check '{check.Name}' within {limit.TotalSeconds:0} seconds.");
            }
            catch (GitHubApiException ex)
            {
                warnings.Enqueue(ex.Kind == GitHubFailureKind.Forbidden && part == "job log"
                    ? $"GitHub refused the job log of check '{check.Name}' (the App needs the actions: read permission): {ex.Message}"
                    : $"GitHub didn't give the {part} of check '{check.Name}': {ex.Message}");
            }
            catch (System.Text.Json.JsonException ex)
            {
                warnings.Enqueue($"GitHub gave an unreadable {part} for check '{check.Name}': {ex.Message}");
            }
            catch (GitHubRateLimitedException)
            {
                warnings.Enqueue($"GitHub's rate limit stopped reading the {part} of check '{check.Name}'.");
            }

            return default;
        }
    }

    /// <summary>A text GET (following GitHub's redirect to the log file), read up to <see cref="CheckFailureLogs.MaxBytes"/>.</summary>
    private async Task<string> GetLogAsync(string relative, string token, CancellationToken cancellationToken)
    {
        var client = Clients.CreateClient(Git.GitHubGitProvider.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relative, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "Could not reach GitHub.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "Could not reach GitHub: the request timed out.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(response, cancellationToken);
            }

            return await CheckFailureLogs.ReadTextAsync(response.Content, CheckFailureLogs.MaxBytes, cancellationToken);
        }
    }
}
