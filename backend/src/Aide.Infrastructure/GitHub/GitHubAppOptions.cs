using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.GitHub;

/// <summary>
/// FR-80, §9: the AIDE GitHub App from configuration (section <c>GitHub:App</c>; <c>GitHub__App__*</c> in
/// <c>secrets.env</c>). When all five values are set they take precedence over the App registered through the manifest
/// flow. <see cref="PrivateKey"/> is the App's PEM private key; <c>\n</c> escapes (one line in an env file) are accepted.
/// </summary>
public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHub:App";

    public long? AppId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? PrivateKey { get; set; }

    public string? Slug { get; set; }

    /// <summary>Whether any value is set (then all of them must be).</summary>
    public bool AnySet =>
        AppId is not null || !string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret)
        || !string.IsNullOrWhiteSpace(PrivateKey) || !string.IsNullOrWhiteSpace(Slug);

    /// <summary>Whether the App is fully configured here.</summary>
    public bool IsComplete =>
        AppId is > 0 && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(Slug);

    /// <summary><see cref="PrivateKey"/> with <c>\n</c> escapes turned into line breaks.</summary>
    public string? PrivateKeyPem => PrivateKey?.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
}

/// <summary>Validates <see cref="GitHubAppOptions"/> at start-up: all or nothing, and a readable RSA private key.</summary>
internal sealed class GitHubAppOptionsValidator : IValidateOptions<GitHubAppOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubAppOptions options)
    {
        if (!options.AnySet)
        {
            return ValidateOptionsResult.Success;
        }

        if (!options.IsComplete)
        {
            return ValidateOptionsResult.Fail(
                "GitHub:App needs all of AppId, ClientId, ClientSecret, PrivateKey and Slug (or none of them, to use the App registered from Admin → Integrations).");
        }

        if (!GitHubAppJwt.IsReadablePrivateKey(options.PrivateKeyPem!))
        {
            // The key itself is never echoed.
            return ValidateOptionsResult.Fail("GitHub:App:PrivateKey is not a PEM RSA private key.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Builds the RS256 App JWT GitHub expects (FR-82, §4.14) with the framework's RSA, no extra package.</summary>
public static class GitHubAppJwt
{
    /// <summary>
    /// A JWT for <paramref name="appId"/>: issued a minute in the past (clock drift), expiring after 9 minutes (GitHub's
    /// maximum is 10), signed with the App's private key.
    /// </summary>
    public static string Create(long appId, string privateKeyPem, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(privateKeyPem);
        var issuedAt = now - Domain.GitHub.GitHubRules.JwtBackdate;
        var expiresAt = now + Domain.GitHub.GitHubRules.JwtLifetime;
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8);
        var payload = Base64Url(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"iat":{{issuedAt.ToUnixTimeSeconds()}},"exp":{{expiresAt.ToUnixTimeSeconds()}},"iss":"{{appId.ToString(System.Globalization.CultureInfo.InvariantCulture)}}"}"""));
        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(System.Text.Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public static bool IsReadablePrivateKey(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
