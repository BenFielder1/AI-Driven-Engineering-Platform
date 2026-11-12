using System.Globalization;
using System.Text.RegularExpressions;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>FR-236: the log of a failed Bitbucket Pipelines step.</summary>
public sealed partial class BitbucketClient
{
    /// <summary>The step results that count as failed.</summary>
    private static readonly string[] FailedResults = ["FAILED", "ERROR"];

    /// <summary>
    /// FR-236: the pipeline's build number from a Pipelines commit status URL of the repository <paramref name="fullName"/> on
    /// bitbucket.org (<c>https://bitbucket.org/{workspace}/{repo}/addon/pipelines/home#!/results/42</c>,
    /// <c>…/{workspace}/{repo}/pipelines/results/42</c>), or <c>null</c> for any other URL (another host or repository, an
    /// external CI), so nothing is fetched for it.
    /// </summary>
    public static int? PipelineBuildNumber(string? statusUrl, string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        if (string.IsNullOrEmpty(statusUrl) || !Uri.TryCreate(statusUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !string.Equals(url.Host, "bitbucket.org", StringComparison.OrdinalIgnoreCase)
            || !url.AbsolutePath.StartsWith("/" + fullName.Trim('/') + "/", StringComparison.OrdinalIgnoreCase)
            || !url.AbsolutePath.Contains("pipelines", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = PipelineResult().Match(url.AbsolutePath + url.Fragment);
        return match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>
    /// FR-236: the log of the first failed step of pipeline <paramref name="buildNumber"/>
    /// (<c>GET …/pipelines/{n}/steps/</c>, then <c>GET …/pipelines/{n}/steps/{step}/log</c>), read up to
    /// <paramref name="maxBytes"/>; <c>null</c> when the pipeline, a failed step or its log isn't there (404). Other failures
    /// (a redirect, which isn't followed, 403) throw <see cref="BitbucketApiException"/>.
    /// </summary>
    public async Task<string?> GetFailedStepLogAsync(BitbucketAuth auth, string fullName, int buildNumber, int maxBytes, CancellationToken cancellationToken)
    {
        var pipeline = $"{RepoPath(fullName)}/pipelines/{buildNumber.ToString(CultureInfo.InvariantCulture)}";
        string? stepId;
        try
        {
            using var json = await GetJsonAsync($"{pipeline}/steps/?pagelen=100", auth, cancellationToken);
            var steps = Values(json.RootElement);
            var failed = steps.FirstOrDefault(s => s.TryGetProperty("state", out var state)
                && state.ValueKind == System.Text.Json.JsonValueKind.Object
                && state.TryGetProperty("result", out var result)
                && result.ValueKind == System.Text.Json.JsonValueKind.Object
                && FailedResults.Contains(Str(result, "name"), StringComparer.Ordinal));
            stepId = failed.ValueKind == System.Text.Json.JsonValueKind.Object ? Str(failed, "uuid") : null;
        }
        catch (BitbucketApiException ex) when (ex.Status == 404)
        {
            return null;
        }

        if (stepId is null)
        {
            return null;
        }

        using var request = Authorized(HttpMethod.Get, $"{pipeline}/steps/{Segment(stepId)}/log", auth);
        request.Headers.Accept.Clear();
        using var response = await SendStreamingAsync(request, cancellationToken);
        if ((int)response.StatusCode == 404)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        return await CheckFailureLogs.ReadTextAsync(response.Content, maxBytes, cancellationToken);
    }

    /// <summary>As the JSON sends (budget, transport failures), but returns once the headers are read, for a capped download.</summary>
    private async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_limiterKey is { } key && _limiter.TryAcquire(key) is { } wait)
        {
            throw new BitbucketApiException(429, "Tevrin's Bitbucket request budget is used up for now.")
            {
                RetryAfter = wait,
                LocalBudget = true,
            };
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BitbucketApiException(null, null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BitbucketApiException(null, "Bitbucket didn't answer in time.", ex);
        }
    }

    [GeneratedRegex(@"results/(?<n>\d{1,9})(?:\D|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PipelineResult();
}
