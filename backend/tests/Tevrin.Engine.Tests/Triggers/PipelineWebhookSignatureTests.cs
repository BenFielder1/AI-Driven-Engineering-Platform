using System.Text;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Triggers;

namespace Tevrin.Engine.Tests.Triggers;

/// <summary>FR-337, §4.72: the webhook signature, the rotation overlap and the replay rule.</summary>
public class PipelineWebhookSignatureTests
{
    /// <summary>§4.72's worked example: secret <c>whsec_test</c>, t = 1764590400, body <c>{"a":1}</c>.</summary>
    public const string VectorSecret = "whsec_test";
    public const long VectorTimestamp = 1764590400;
    public const string VectorBody = "{\"a\":1}";

    /// <summary>HMAC-SHA256("whsec_test", <c>1764590400.{"a":1}</c>), computed once and committed.</summary>
    public const string VectorV1 = "54fe8ee64e5499df7c6cee87b1b22c2800b3fbfed80e088b818a25b927713fb0";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(VectorBody);
    private static readonly DateTimeOffset VectorTime = DateTimeOffset.FromUnixTimeSeconds(VectorTimestamp);

    [Fact]
    public void FR337_spec_test_vector_computes_and_verifies()
    {
        Assert.Equal(VectorV1, PipelineWebhookSignature.Compute(VectorSecret, VectorTimestamp, Body));
        Assert.True(PipelineWebhookSignature.Verify($"t={VectorTimestamp},v1={VectorV1}", Body, [VectorSecret], VectorTime));
        Assert.Equal($"t={VectorTimestamp},v1={VectorV1}", PipelineWebhookSignature.Header(VectorSecret, VectorTimestamp, Body));
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(-300, true)]
    [InlineData(301, false)]
    [InlineData(-301, false)]
    public void FR337_the_timestamp_must_be_within_300_seconds(int skewSeconds, bool valid) =>
        Assert.Equal(valid, PipelineWebhookSignature.Verify($"t={VectorTimestamp},v1={VectorV1}", Body, [VectorSecret], VectorTime.AddSeconds(skewSeconds)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=" + VectorV1)]
    [InlineData("t=1764590400")]
    [InlineData("t=1764590400,v1=zz")]
    [InlineData("t=abc,v1=" + VectorV1)]
    [InlineData("t=1764590400,t=1764590400,v1=" + VectorV1)]
    [InlineData("t=1764590400,v1=" + "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("garbage")]
    public void FR337_malformed_or_wrong_signatures_fail(string? header) =>
        Assert.False(PipelineWebhookSignature.Verify(header, Body, [VectorSecret], VectorTime));

    [Fact]
    public void FR337_a_changed_body_or_wrong_secret_fails()
    {
        var header = $"t={VectorTimestamp},v1={VectorV1}";
        Assert.False(PipelineWebhookSignature.Verify(header, Encoding.UTF8.GetBytes("{\"a\":2}"), [VectorSecret], VectorTime));
        Assert.False(PipelineWebhookSignature.Verify(header, Body, ["whsec_other"], VectorTime));
        Assert.False(PipelineWebhookSignature.Verify(header, Body, [], VectorTime));
    }

    [Fact]
    public void FR337_any_of_several_v1_entries_may_match()
    {
        var header = $"t={VectorTimestamp},v1={new string('a', 64)},v1={VectorV1}";
        Assert.True(PipelineWebhookSignature.Verify(header, Body, [VectorSecret], VectorTime));
    }

    [Fact]
    public void FR337_the_previous_secret_is_accepted_until_it_expires()
    {
        var now = VectorTime;
        var expires = now.AddHours(24);
        Assert.Equal(["new", VectorSecret], PipelineWebhookSignature.AcceptedSecrets("new", VectorSecret, expires, now));
        Assert.True(PipelineWebhookSignature.Verify(
            $"t={VectorTimestamp},v1={VectorV1}", Body, PipelineWebhookSignature.AcceptedSecrets("new", VectorSecret, expires, now), now));

        Assert.Equal(["new"], PipelineWebhookSignature.AcceptedSecrets("new", VectorSecret, expires, expires));
        Assert.Equal(["new"], PipelineWebhookSignature.AcceptedSecrets("new", null, null, now));
    }

    [Fact]
    public void FR337_a_delivery_seen_within_24_hours_is_a_replay()
    {
        var now = VectorTime;
        Assert.False(WebhookDeliveryRules.IsReplay(null, now));
        Assert.True(WebhookDeliveryRules.IsReplay(now, now));
        Assert.True(WebhookDeliveryRules.IsReplay(now.AddHours(-24).AddSeconds(1), now));
        Assert.False(WebhookDeliveryRules.IsReplay(now.AddHours(-24), now));
    }
}
