using System.Globalization;
using Aide.Infrastructure.Git;

namespace Aide.Infrastructure.GitLab;

/// <summary>FR-236: the trace (log) of a failed CI job.</summary>
public sealed partial class GitLabClient
{
    /// <summary>
    /// FR-236: <c>GET /projects/:id/jobs/:job_id/trace</c>, read up to <paramref name="maxBytes"/>; <c>null</c> when GitLab has
    /// no such job (404, e.g. an external commit status). Other failures throw <see cref="GitLabApiException"/>.
    /// </summary>
    public async Task<string?> GetJobTraceAsync(string token, string projectId, long jobId, int maxBytes, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, Url($"api/v4/projects/{Id(projectId)}/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}/trace"), token);
        request.Headers.Accept.Clear();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex) when (GitHostBlockedException.Find(ex) is { } blocked)
        {
            throw blocked;
        }
        catch (HttpRequestException ex)
        {
            throw new GitLabApiException(null, null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitLabApiException(null, "GitLab didn't answer in time.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 404)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new GitLabApiException(status, status is >= 300 and < 400 ? "GitLab answered with a redirect, which Tevrin doesn't follow." : null)
                {
                    RetryAfter = RetryAfterOf(response),
                };
            }

            return await CheckFailureLogs.ReadTextAsync(response.Content, maxBytes, cancellationToken);
        }
    }
}
