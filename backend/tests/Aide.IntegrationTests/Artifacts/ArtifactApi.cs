using System.Net.Http.Json;
using Aide.Api.Features.Artifacts;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Artifacts;

/// <summary>Helpers for the artifacts feature tests.</summary>
public static class ArtifactApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> CreateDraftAsync(HttpClient client, Guid projectId, string type, string content)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync($"/api/projects/{projectId}/artifacts/{type}", new { content }, Ct);
    }

    /// <summary>A draft edited from <paramref name="baseVersion"/> (FR-18 stale-edit check).</summary>
    public static Task<HttpResponseMessage> CreateDraftAsync(HttpClient client, Guid projectId, string type, string content, int baseVersion)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync($"/api/projects/{projectId}/artifacts/{type}", new { content, baseVersion }, Ct);
    }

    public static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid projectId, Guid artifactId, string action, string? comment = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync($"/api/projects/{projectId}/artifacts/{artifactId}/{action}", new { comment }, Ct);
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, Guid projectId, string path)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.GetAsync(new Uri($"/api/projects/{projectId}/artifacts{path}", UriKind.Relative), Ct);
    }

    public static async Task<ArtifactDto> ReadAsync(HttpResponseMessage response, System.Net.HttpStatusCode expected = System.Net.HttpStatusCode.OK) =>
        await ProjectApi.ReadAsync<ArtifactDto>(response, expected);

    public static Task<Artifact> RowAsync(WebApplicationFactory<Program> factory, Guid artifactId) =>
        ProjectApi.WithDbAsync(factory, db => db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId, Ct));

    /// <summary>Changes stored content behind the API's back (FR-20 corruption).</summary>
    public static Task TamperAsync(WebApplicationFactory<Program> factory, Guid artifactId, string content) =>
        ProjectApi.WithDbAsync(factory, db => db.Database.ExecuteSqlAsync($"UPDATE artifacts SET content = {content} WHERE id = {artifactId}", Ct));
}
