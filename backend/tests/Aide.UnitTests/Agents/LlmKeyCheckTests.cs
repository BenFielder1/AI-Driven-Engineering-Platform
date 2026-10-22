using System.Net;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Llm;
using static Aide.UnitTests.Agents.LlmProviderTests;

namespace Aide.UnitTests.Agents;

/// <summary>FR-78: key validation with a minimal authenticated list-models call, against fake HTTP handlers.</summary>
public class LlmKeyCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Microsoft.Extensions.Options.IOptions<LlmOptions> Options() => Microsoft.Extensions.Options.Options.Create(new LlmOptions());

    [Fact]
    public async Task FR78_Anthropic_key_is_checked_with_GET_v1_models_and_the_key_header()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"data":[]}""");
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var check = await provider.CheckKeyAsync("sk-ant-check-key", Ct);

        Assert.Equal((LlmKeyCheckOutcome.Valid, (int?)200), (check.Outcome, check.StatusCode));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1"), (sent.Method, sent.Uri));
        Assert.Equal("sk-ant-check-key", sent.Headers["x-api-key"]);
        Assert.Equal(AnthropicLlmProvider.ApiVersion, sent.Headers["anthropic-version"]);
        Assert.Equal(string.Empty, sent.Body);
    }

    [Fact]
    public async Task FR78_OpenAI_key_is_checked_with_GET_models_and_a_bearer_key()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"data":[]}""");
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options());

        var check = await provider.CheckKeyAsync("sk-openai-check", Ct);

        Assert.Equal(LlmKeyCheckOutcome.Valid, check.Outcome);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, "https://api.openai.com/v1/models"), (sent.Method, sent.Uri));
        Assert.Equal("Bearer sk-openai-check", sent.Headers["Authorization"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmKeyCheckOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, LlmKeyCheckOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmKeyCheckOutcome.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, LlmKeyCheckOutcome.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, LlmKeyCheckOutcome.Unavailable)]
    public async Task FR78_Rejected_keys_and_provider_failures_are_told_apart(HttpStatusCode status, LlmKeyCheckOutcome expected)
    {
        // OpenAI echoes part of a rejected key in its error body; the check never reads or relays the body.
        var handler = new FakeHandler(status, """{"error":{"message":"Incorrect API key provided: sk-secret-echo"}}""");
        var provider = new OpenAiLlmProvider(new HttpClient(handler), Options());

        var check = await provider.CheckKeyAsync("sk-secret-echo", Ct);

        Assert.Equal((expected, (int?)(int)status), (check.Outcome, check.StatusCode));
        Assert.DoesNotContain("sk-secret-echo", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-echo", check.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR78_An_unreachable_provider_is_unavailable_not_rejected()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no dns"));
        var provider = new AnthropicLlmProvider(new HttpClient(handler), Options());

        var check = await provider.CheckKeyAsync("sk-ant-x", Ct);

        Assert.Equal((LlmKeyCheckOutcome.Unavailable, (int?)null), (check.Outcome, check.StatusCode));
    }

    [Fact]
    public async Task FR78_A_key_check_that_outlasts_Llm_KeyCheckTimeout_is_unavailable()
    {
        var handler = new DelayingHandler();
        var options = Microsoft.Extensions.Options.Options.Create(new LlmOptions { KeyCheckTimeout = TimeSpan.FromMilliseconds(100) });
        var provider = new OpenAiLlmProvider(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) }, options);

        var check = await provider.CheckKeyAsync("sk-slow", Ct);

        Assert.Equal((LlmKeyCheckOutcome.Unavailable, (int?)null), (check.Outcome, check.StatusCode));
        Assert.True(handler.Cancelled);
    }

    [Fact]
    public void FR78_KeyCheckTimeout_defaults_to_15_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), new LlmOptions().KeyCheckTimeout);

    private sealed class DelayingHandler : HttpMessageHandler
    {
        public bool Cancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void FR78_LlmKey_never_prints_its_key() =>
        Assert.DoesNotContain("sk-ant-print", new LlmKey("sk-ant-print", Guid.CreateVersion7()).ToString(), StringComparison.Ordinal);
}
