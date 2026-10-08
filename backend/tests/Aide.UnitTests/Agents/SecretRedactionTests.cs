using Aide.Domain.Agents;

namespace Aide.UnitTests.Agents;

/// <summary>NFR-SEC: provider error text is stored, pushed live and logged only after credentials are masked.</summary>
public class SecretRedactionTests
{
    [Theory]
    [InlineData("Incorrect API key provided: sk-proj-****abcd.", "Incorrect API key provided: [redacted].")]
    [InlineData("key sk-ant-api03-AbC_123-xyz is invalid", "key [redacted] is invalid")]
    [InlineData("header was Bearer abc.def.ghi, refused", "header was Bearer [redacted], refused")]
    [InlineData("x-api-key: abcdef123", "x-api-key: [redacted]")]
    [InlineData("{\"api_key\":\"abcdef123\"}", "{\"api_key\":\"[redacted]\"}")]
    [InlineData("model 'qwen2.5-coder' not found", "model 'qwen2.5-coder' not found")]
    public void NFR_SEC_Key_like_tokens_are_masked(string text, string expected) =>
        Assert.Equal(expected, SecretRedaction.Redact(text));

    [Fact]
    public void NFR_SEC_Known_secrets_and_their_parts_are_masked()
    {
        const string key = "my-project-secret\nsecond-half-of-it";

        var redacted = SecretRedaction.Redact($"The format of value '{key}' is invalid. Also my-project-secret and second-half-of-it.", key);

        Assert.DoesNotContain("my-project-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("second-half-of-it", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void NFR_SEC_Provider_errors_are_redacted_kept_on_one_line_and_capped()
    {
        var text = "line one sk-abcdef123\nline two " + new string('x', 1000);

        var sanitized = SecretRedaction.SanitizeProviderError(text);

        Assert.StartsWith("line one [redacted] line two ", sanitized, StringComparison.Ordinal);
        Assert.Equal(SecretRedaction.MaxProviderErrorLength + 1, sanitized.Length);
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void NFR_SEC_Whitespace_and_control_characters_are_detected()
    {
        Assert.True(SecretRedaction.HasWhitespaceOrControl("a\nb"));
        Assert.True(SecretRedaction.HasWhitespaceOrControl("a\u0007b"));
        Assert.False(SecretRedaction.HasWhitespaceOrControl("sk-ant-api03_AbC-123"));
    }
}
