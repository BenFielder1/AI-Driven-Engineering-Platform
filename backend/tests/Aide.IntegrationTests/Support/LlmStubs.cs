using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aide.Api.Features.Connections;
using Aide.Infrastructure.Agents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>A request an <see cref="LlmStubHandler"/> received.</summary>
public sealed record LlmStubRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string Body);

/// <summary>
/// A scripted LLM provider endpoint (CLAUDE.md rule 11: no test reaches a real provider). Answers queued responses in
/// order, then <see cref="DefaultStatus"/>, and records every request.
/// </summary>
public sealed class LlmStubHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<(HttpStatusCode Status, string Body)> _responses = new();
    private readonly ConcurrentQueue<LlmStubRequest> _requests = new();

    /// <summary>The answer once the queue is empty (200 by default, e.g. for list-models key checks).</summary>
    public HttpStatusCode DefaultStatus { get; set; } = HttpStatusCode.OK;

    public IReadOnlyList<LlmStubRequest> Requests => [.. _requests];

    /// <summary>How long each answer takes (honours the request's cancellation, like a slow provider).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public void Enqueue(HttpStatusCode status, string body = "{}") => _responses.Enqueue((status, body));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new LlmStubRequest(request.Method, request.RequestUri!.ToString(), headers, body));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        var (status, text) = _responses.TryDequeue(out var next) ? next : (DefaultStatus, """{"data":[]}""");
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Helpers for the LLM connection tests (FR-78, FR-79).</summary>
public static class LlmStubs
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app with Anthropic and OpenAI answered by the stubs (Ollama keeps refusing to send).</summary>
    public static WebApplicationFactory<Program> App(
        WebApplicationFactory<Program> factory, LlmStubHandler anthropic, LlmStubHandler openai, params (string Key, string Value)[] settings)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(services =>
            {
                services.AddHttpClient<AnthropicLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => anthropic);
                services.AddHttpClient<OpenAiLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => openai);
            });
        });
    }

    /// <summary>Adds a connection through the API (the stub must accept the key) and returns it.</summary>
    public static async Task<LlmCredentialDto> CreateAsync(HttpClient client, string provider, string apiKey, string label = "Work")
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.PostAsJsonAsync("/api/me/connections/llm", new { provider, label, apiKey }, Ct);
        return await ProjectApi.ReadAsync<LlmCredentialDto>(response, HttpStatusCode.Created);
    }
}
