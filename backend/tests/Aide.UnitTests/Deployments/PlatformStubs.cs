using System.Net;
using System.Text;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;

namespace Aide.UnitTests.Deployments;

/// <summary>One request a platform stub saw: method, path and query (after the API base; Railway's keeps its leading slash, <c>/graphql/v2</c>), and body.</summary>
internal sealed record PlatformCall(string Method, string Path, string Body);

/// <summary>
/// A scripted platform API: each request is answered by the first route whose method and path prefix match (routes are
/// tried in order; a route may answer several times). Unmatched requests are 404. No network.
/// </summary>
internal sealed class PlatformStub : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Func<PlatformCall, HttpResponseMessage> Respond)> _routes = [];

    public List<PlatformCall> Calls { get; } = [];

    public PlatformStub On(string method, string pathPrefix, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((method, pathPrefix, _ => Json(json, status)));
        return this;
    }

    public PlatformStub On(string method, string pathPrefix, Func<PlatformCall, HttpResponseMessage> respond)
    {
        _routes.Add((method, pathPrefix, respond));
        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public IEnumerable<PlatformCall> Where(string method, string pathPrefix) =>
        Calls.Where(c => c.Method == method && c.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        foreach (var prefix in new[] { "/api/v1/", "/client/v4/", "/v1/" })
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                path = path[prefix.Length..];
            }
        }

        var call = new PlatformCall(request.Method.Method, Uri.UnescapeDataString(path + uri.Query), body);
        Calls.Add(call);
        foreach (var route in _routes)
        {
            if (route.Method == call.Method && call.Path.StartsWith(route.Path, StringComparison.Ordinal))
            {
                return route.Respond(call);
            }
        }

        return Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound);
    }
}

/// <summary>HttpClients on the platforms' real base URLs, all served by one stub.</summary>
internal sealed class PlatformStubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri(PlatformHosts.All.First(h => h.Name == name).BaseUrl),
    };
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class PlatformFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public static DeploymentRepoRef GitHubRepo { get; } = new("github", "acme", "todo", 42, null, "acme/todo", "main");

    public static DeploymentTargetRef Target(DeploymentProvider platform, string? resourceId, string? previewResourceId = null, DeploymentRepoRef? repo = null) => new(
        Guid.CreateVersion7(),
        platform,
        DeploymentComponent.Frontend,
        resourceId,
        previewResourceId,
        resourceId,
        ".",
        new DeploymentBuildConfig { BuildCommand = "npm run build", OutputDirectory = "dist" },
        repo ?? GitHubRepo);
}
