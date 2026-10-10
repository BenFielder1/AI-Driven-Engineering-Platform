using System.Net;
using System.Text;
using System.Text.Json;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Llm;
using Microsoft.Extensions.Options;

namespace Aide.UnitTests.Agents;

/// <summary>FR-22, FR-25: each provider's request/response mapping and failure classification, against fake HTTP handlers
/// (no test reaches a real provider).</summary>
public class LlmProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LlmRequest Request(string model = "m-1", string? apiKey = "sk-test-key", AgentKind? agent = AgentKind.SpecGenerator) => new()
    {
        Model = model,
        System = "You are a test.",
        Messages = [new LlmMessage(LlmRole.User, "Hello")],
        MaxTokens = 1234,
        Agent = agent,
        ApiKey = apiKey,
    };

    private static IOptions<LlmOptions> Options(Action<LlmOptions>? configure = null)
    {
        var options = new LlmOptions();
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public async Task FR22_Anthropic_posts_to_v1_messages_with_key_version_and_maps_the_reply()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":"Hello "},{"type":"thinking","thinking":"hidden"},{"type":"text","text":"world"}],
             "stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":34}}
            """);
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var completion = await provider.CompleteAsync(Request("claude-opus-5-5"), Ct);

        Assert.Equal(new LlmCompletion("Hello world", 12, 34, "end_turn"), completion);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://api.anthropic.com/v1/messages"), (sent.Method, sent.Uri));
        Assert.Equal("sk-test-key", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.Equal(1234, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("You are a test.", body.GetProperty("system").GetString());
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal(("user", "Hello"), (message.GetProperty("role").GetString(), message.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task FR22_Anthropic_uses_the_configured_base_url()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"content":[],"usage":{}}""");
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options(o => o.Providers.Anthropic.BaseUrl = "http://proxy.local/anthropic"));

        await provider.CompleteAsync(Request(), Ct);

        Assert.Equal("http://proxy.local/anthropic/v1/messages", Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData((HttpStatusCode)529, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task FR25_Anthropic_errors_are_classified_by_status(HttpStatusCode status, bool transient)
    {
        var handler = new FakeHandler(status, """{"type":"error","error":{"type":"some_error","message":"Something happened"}}""");
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request(), Ct));

        Assert.Equal((transient, (int?)(int)status, ErrorCodes.ProviderError), (ex.Transient, ex.StatusCode, ex.ErrorCode));
        Assert.Contains("Something happened", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR22_Anthropic_without_a_key_is_not_configured_and_never_called()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request(apiKey: null), Ct));

        Assert.Equal((false, ErrorCodes.ProviderNotConfigured), (ex.Transient, ex.ErrorCode));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FR25_Timeouts_and_connection_failures_are_transient()
    {
        var slow = new FakeHandler(_ => throw new TaskCanceledException("timeout"));
        var down = new FakeHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var timeout = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(slow), Options()).CompleteAsync(Request(), Ct));
        var unreachable = await Assert.ThrowsAsync<LlmProviderException>(() => new OllamaLlmProvider(new HttpClient(down), Options()).CompleteAsync(Request(), Ct));

        Assert.True(timeout.Transient);
        Assert.Null(timeout.StatusCode);
        Assert.True(unreachable.Transient);
    }

    [Fact]
    public async Task FR25_An_unreadable_success_response_is_permanent()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "<html>not json</html>");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.False(ex.Transient);
    }

    [Fact]
    public async Task FR25_Caller_cancellation_is_not_a_provider_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), cts.Token));
    }

    // ------------------------------------------------------------------ OpenAI

    [Fact]
    public async Task FR22_OpenAI_posts_chat_completions_with_bearer_key_and_maps_the_reply()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":21,"completion_tokens":5,"total_tokens":26}}
            """);
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options());

        var completion = await provider.CompleteAsync(Request("gpt-6-astra"), Ct);

        Assert.Equal(new LlmCompletion("Hi there", 21, 5, "stop"), completion);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", sent.Uri);
        Assert.Equal("Bearer sk-test-key", sent.Headers["Authorization"]);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(("gpt-6-astra", 1234), (body.GetProperty("model").GetString(), body.GetProperty("max_completion_tokens").GetInt32()));
        var messages = body.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString())).ToList();
        Assert.Equal([("system", "You are a test."), ("user", "Hello")], messages);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    public async Task FR25_OpenAI_errors_are_classified_by_status(HttpStatusCode status, bool transient)
    {
        var handler = new FakeHandler(status, """{"error":{"message":"Rate limit reached","type":"requests","code":"rate_limit_exceeded"}}""");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.Equal(transient, ex.Transient);
        Assert.Contains("Rate limit reached", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task NFR_SEC_A_rejected_key_gets_a_fixed_permanent_message_without_the_providers_echo(HttpStatusCode status)
    {
        var body = """{"error":{"message":"Incorrect API key provided: sk-proj-****************************-key. You can find your API key at https://platform.openai.com/account/api-keys.","type":"invalid_request_error","code":"invalid_api_key"}}""";

        var openAi = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(new FakeHandler(status, body)), Options()).CompleteAsync(Request(), Ct));
        var anthropic = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(new FakeHandler(status, body)), Options()).CompleteAsync(Request(), Ct));

        Assert.Equal(($"OpenAI rejected the API key (HTTP {(int)status}).", false), (openAi.Message, openAi.Transient));
        Assert.Equal($"Anthropic rejected the API key (HTTP {(int)status}).", anthropic.Message);
    }

    [Fact]
    public async Task NFR_SEC_Key_like_tokens_in_other_provider_errors_are_redacted()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"error":{"message":"Bad request for key sk-ant-api03-abcdef123456 with header Authorization: Bearer abc.def.ghi"}}""");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new AnthropicLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(), Ct));

        Assert.DoesNotContain("sk-ant-api03", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Bad request for key [redacted]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR22_OpenAI_refusal_is_a_permanent_failure_and_a_missing_key_is_never_sent()
    {
        var refusal = new FakeHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":null,"refusal":"I can't help with that."},"finish_reason":"stop"}]}""");
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(refusal), Options()).CompleteAsync(Request(), Ct));
        Assert.False(ex.Transient);
        Assert.Contains("I can't help with that.", ex.Message, StringComparison.Ordinal);

        var unused = new FakeHandler(HttpStatusCode.OK, "{}");
        var missing = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiLlmProvider(new HttpClient(unused), Options()).CompleteAsync(Request(apiKey: " "), Ct));
        Assert.Equal(ErrorCodes.ProviderNotConfigured, missing.ErrorCode);
        Assert.Empty(unused.Requests);
    }

    // ------------------------------------------------------------------ Ollama

    [Fact]
    public async Task FR22_Ollama_example_posts_api_chat_with_the_model_and_stream_false()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"model":"qwen2.5-coder","message":{"role":"assistant","content":"done"},"done":true,"done_reason":"stop",
             "prompt_eval_count":40,"eval_count":7}
            """);
        var provider = new OllamaLlmProvider(new HttpClient(handler), Options(o => o.Providers.Ollama.BaseUrl = "http://ollama.test:11434"));

        var completion = await provider.CompleteAsync(Request("qwen2.5-coder", apiKey: null), Ct);

        Assert.Equal(new LlmCompletion("done", 40, 7, "stop"), completion);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://ollama.test:11434/api/chat", sent.Uri);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("qwen2.5-coder", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal(1234, body.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task FR25_Ollama_errors_are_classified_by_status(HttpStatusCode status, bool transient)
    {
        var handler = new FakeHandler(status, """{"error":"model 'nope' not found"}""");

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => new OllamaLlmProvider(new HttpClient(handler), Options()).CompleteAsync(Request(apiKey: null), Ct));

        Assert.Equal(transient, ex.Transient);
        Assert.Contains("model 'nope' not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR22_Ollama_defaults_to_localhost_in_the_dev_loop()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            Assert.Equal(new Uri("http://host.docker.internal:11434"), new LlmOptions().ResolveOllamaBaseUrl());
        }
        else
        {
            Assert.Equal(new Uri("http://localhost:11434"), new LlmOptions().ResolveOllamaBaseUrl());
        }
    }

    // ------------------------------------------------------------------ Fake

    [Fact]
    public async Task FR22_Fake_is_deterministic_offline_and_estimates_tokens()
    {
        var provider = new FakeLlmProvider([]);

        var first = await provider.CompleteAsync(Request("fake-1"), Ct);
        var second = await provider.CompleteAsync(Request("fake-1"), Ct);

        Assert.Equal(first, second);
        Assert.Contains("spec_generator", first.Text, StringComparison.Ordinal);
        Assert.Contains("fake-1", first.Text, StringComparison.Ordinal);
        Assert.Equal(FakeLlmProvider.EstimateTokens("You are a test.\nHello"), first.InputTokens);
        Assert.Equal(FakeLlmProvider.EstimateTokens(first.Text), first.OutputTokens);
        Assert.Equal(FakeLlmProvider.StopReason, first.StopReason);
    }

    [Fact]
    public async Task FR22_Fake_uses_the_last_registered_responder_for_the_agent()
    {
        var provider = new FakeLlmProvider([new FixedResponder(AgentKind.SpecGenerator, "first"), new FixedResponder(AgentKind.SpecGenerator, "## 1. Overview")]);

        Assert.Equal("## 1. Overview", (await provider.CompleteAsync(Request(), Ct)).Text);
        Assert.StartsWith("# Fake plan_generator output", (await provider.CompleteAsync(Request(agent: AgentKind.PlanGenerator), Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FR22_Requests_never_print_their_key()
    {
        Assert.DoesNotContain("sk-test-key", Request().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-key", new ResolvedLlm(LlmProvider.Anthropic, "m", "sk-test-key").ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LlmProvider.Anthropic, null, false)]
    [InlineData(LlmProvider.Anthropic, "k", true)]
    [InlineData(LlmProvider.Openai, " ", false)]
    [InlineData(LlmProvider.Ollama, null, true)]
    [InlineData(LlmProvider.Fake, null, true)]
    public void FR22_Anthropic_and_OpenAI_need_a_key(LlmProvider provider, string? key, bool configured) =>
        Assert.Equal(configured, new ResolvedLlm(provider, "m", key).IsConfigured);

    private sealed class FixedResponder(AgentKind agent, string text) : IFakeAgentResponder
    {
        public AgentKind Agent => agent;

        public string Respond(LlmRequest request) => text;
    }

    public sealed record SentRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string Body);

    /// <summary>Records requests and answers with a fixed status and body (or runs <c>respond</c>).</summary>
    public sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(HttpStatusCode status, string body)
            : this(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
        }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(request.Method, request.RequestUri!.ToString(), headers, body));
            return _respond(request);
        }
    }
}
