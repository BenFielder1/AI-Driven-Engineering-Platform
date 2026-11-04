using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §4.28, §11 Q5: the seam that places a Render service or Postgres in a project's environment. Render's public API reference
/// (read 2026-11-01, not exercised against a live account) has <c>GET /v1/projects/{id}</c> with <c>environmentIds</c>,
/// <c>GET /v1/environments/{id}</c> with <c>name</c>, and <c>POST /v1/environments/{id}/resources {resourceIds}</c>. Any
/// failure, including a missing environment on a linked project, means "not placed": it never blocks a group, which still
/// shares workspace, region and name, and the caller surfaces it as the group's status reason.
/// </summary>
internal static class RenderProjectPlacement
{
    public const string ProductionEnvironment = "production";
    public const string PreviewEnvironment = "preview";

    /// <summary>Whether <paramref name="resourceId"/> is now in the project's <paramref name="environmentName"/> environment.</summary>
    public static async Task<bool> PlaceAsync(PlatformHttp http, string token, string projectId, string environmentName, string resourceId, CancellationToken cancellationToken)
    {
        try
        {
            using var project = await http.SendAsync(
                PlatformProvider.Render, PlatformHosts.RenderClient, HttpMethod.Get, $"projects/{Uri.EscapeDataString(projectId)}", token, null, cancellationToken);
            foreach (var environmentId in Items(project.RootElement, "environmentIds").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!))
            {
                using var environment = await http.SendAsync(
                    PlatformProvider.Render, PlatformHosts.RenderClient, HttpMethod.Get, $"environments/{Uri.EscapeDataString(environmentId)}", token, null, cancellationToken);
                if (!string.Equals(Text(environment.RootElement, "name"), environmentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var placed = await http.SendAsync(
                    PlatformProvider.Render, PlatformHosts.RenderClient, HttpMethod.Post, $"environments/{Uri.EscapeDataString(environmentId)}/resources", token, new { resourceIds = new[] { resourceId } }, cancellationToken);
                return true;
            }

            return false;
        }
        catch (PlatformApiException)
        {
            return false;
        }
    }
}
