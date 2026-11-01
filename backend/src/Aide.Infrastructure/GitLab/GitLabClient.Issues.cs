using System.Net.Http.Headers;
using System.Text.Json;

namespace Aide.Infrastructure.GitLab;

public sealed partial class GitLabClient
{
    /// <summary>FR-168: a GET under this instance, its JSON (the caller disposes it). The token goes only to this origin.</summary>
    public Task<JsonDocument> GetJsonDocumentAsync(string relative, string token, CancellationToken cancellationToken) =>
        GetJsonAsync(relative, token, cancellationToken);

    /// <summary>FR-168: one keyset page: its JSON and the next <c>cursor</c> from the <c>Link</c> header.</summary>
    public async Task<(JsonDocument Json, string? NextCursor)> GetJsonPageAsync(string relative, string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, Url(relative), token);
        using var response = await SendAsync(request, cancellationToken);
        return (await ReadJsonAsync(response, cancellationToken), NextPageCursor(response.Headers));
    }

    /// <summary>The <c>cursor</c> of the <c>Link: &lt;…&gt;; rel="next"</c> header, if any.</summary>
    public static string? NextPageCursor(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var part in links.SelectMany(l => l.Split(',')))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length < 2 || !segments.Skip(1).Any(s => s.Replace(" ", string.Empty, StringComparison.Ordinal) == "rel=\"next\"")
                || !Uri.TryCreate(segments[0].Trim('<', '>'), UriKind.Absolute, out var next))
            {
                continue;
            }

            foreach (var pair in next.Query.TrimStart('?').Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "cursor")
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
        }

        return null;
    }
}
