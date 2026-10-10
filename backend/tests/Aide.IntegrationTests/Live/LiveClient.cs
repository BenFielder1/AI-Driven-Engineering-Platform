using System.Text.Json;
using System.Threading.Channels;
using Aide.Api.Features.Live;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Aide.IntegrationTests.Live;

/// <summary>
/// A SignalR client of <see cref="ProjectsHub"/> over the test server: WebSockets, signed in with a session's cookies
/// (negotiate sends <c>X-XSRF-TOKEN</c> and every request the page's <c>Origin</c>, like the SPA in a browser) or with
/// a bearer token (no Origin, like the CLI). Received <c>projectEvent</c> messages are queued for <see cref="NextAsync"/>.
/// </summary>
public sealed class LiveClient : IAsyncDisposable
{
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();

    private LiveClient(HubConnection connection)
    {
        Connection = connection;
        Connection.On<JsonElement>(ProjectsHub.ProjectEventMethod, message => _messages.Writer.TryWrite(message));
    }

    /// <summary>The SPA's origin: the API's own (same-origin <c>/api</c>).</summary>
    public const string OwnOrigin = "https://localhost";

    public HubConnection Connection { get; }

    /// <summary>Connects with the session's cookies from <paramref name="origin"/> (<c>null</c>: no Origin header).</summary>
    public static async Task<LiveClient> ConnectAsync(WebApplicationFactory<Program> factory, ApiSession session, string? origin = OwnOrigin)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(session);
        var cookies = session.Cookies.Container.GetCookieHeader(AideApiFactory.BaseUri);
        return await ConnectAsync(factory, headers =>
        {
            headers["Cookie"] = cookies;
            if (origin is not null)
            {
                headers["Origin"] = origin;
            }
        }, session.XsrfToken);
    }

    public static Task<LiveClient> ConnectWithTokenAsync(WebApplicationFactory<Program> factory, string token) =>
        ConnectAsync(factory, headers => headers["Authorization"] = $"Bearer {token}", xsrfToken: null);

    private static async Task<LiveClient> ConnectAsync(WebApplicationFactory<Program> factory, Action<IDictionary<string, string>> authenticate, string? xsrfToken)
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(AideApiFactory.BaseUri, ProjectsHub.Path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                authenticate(options.Headers);
                if (xsrfToken is not null)
                {
                    options.Headers["X-XSRF-TOKEN"] = xsrfToken;
                }

                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        foreach (var header in options.Headers)
                        {
                            request.Headers[header.Key] = header.Value;
                        }
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        var live = new LiveClient(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return live;
    }

    public Task JoinAsync(Guid projectId) => Connection.InvokeAsync(nameof(ProjectsHub.Join), projectId, TestContext.Current.CancellationToken);

    public Task LeaveAsync(Guid projectId) => Connection.InvokeAsync(nameof(ProjectsHub.Leave), projectId, TestContext.Current.CancellationToken);

    /// <summary>The next message, or <c>null</c> if none arrives within <paramref name="timeout"/>.</summary>
    public async Task<JsonElement?> NextAsync(TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await _messages.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The next message of <paramref name="type"/> within <paramref name="timeout"/>, skipping others; <c>null</c> if none.</summary>
    public async Task<JsonElement?> NextOfTypeAsync(string type, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await NextAsync(deadline - DateTimeOffset.UtcNow) is not { } message)
            {
                return null;
            }

            if (message.GetProperty("type").GetString() == type)
            {
                return message;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
