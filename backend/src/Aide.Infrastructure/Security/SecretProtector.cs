using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Aide.Infrastructure.Security;

/// <summary>Encrypts secrets at rest (§8 NFR-SEC, FR-9: Git tokens, LLM keys, webhook URLs) with ASP.NET Data Protection.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Decrypts a value from <see cref="Protect"/>. Throws <see cref="CryptographicException"/> if it was
    /// tampered with or the key ring no longer has its key.</summary>
    string Unprotect(string protectedValue);

    /// <summary>Like <see cref="Unprotect"/>, but returns <c>false</c> instead of throwing.</summary>
    bool TryUnprotect(string protectedValue, out string plaintext);
}

/// <summary>Data Protection purpose strings (§8). Changing a purpose makes every secret stored under it unreadable, so
/// they are versioned and fixed.</summary>
public static class SecretPurposes
{
    /// <summary>The v1 purpose, shared by every v1 secret kind (Git tokens, project LLM keys, webhook secrets).</summary>
    public const string V1Shared = "Aide.Secrets.v1";

    /// <summary>LLM API keys: v1 per-project keys and v2 user connections (<c>llm_credentials.secret_encrypted</c>).
    /// It must stay equal to <see cref="V1Shared"/>: the <c>LlmCredentials</c> migration copies v1 project-key
    /// ciphertext into <c>llm_credentials</c> unchanged (FR-79, D-V2-5).</summary>
    public const string LlmApiKey = V1Shared;

    /// <summary>The GitHub App's PEM private key (<c>github_apps.private_key_encrypted</c>, FR-80).</summary>
    public const string GitHubAppPrivateKey = "Aide.Secrets.GitHubAppPrivateKey";

    /// <summary>The GitHub App's OAuth client secret (<c>github_apps.client_secret_encrypted</c>, FR-80).</summary>
    public const string GitHubAppClientSecret = "Aide.Secrets.GitHubAppClientSecret";

    /// <summary>GitHub user-to-server access and refresh tokens (<c>github_connections.*_token_encrypted</c>, FR-81).</summary>
    public const string GitHubUserToken = "Aide.Secrets.GitHubUserToken";

    /// <summary>Vercel access tokens (<c>vercel_connections.token_encrypted</c>, FR-87).</summary>
    public const string VercelToken = "Aide.Secrets.VercelToken";

    /// <summary>Platform connection tokens of every provider (<c>platform_connections.secret_encrypted</c>, FR-142). It must
    /// stay equal to <see cref="VercelToken"/>: the PlatformsAndTargets migration copies v2 Vercel ciphertext unchanged.</summary>
    public const string PlatformToken = VercelToken;

    /// <summary>Deployment environment variable values (<c>deployment_env_vars.value_encrypted</c>, FR-88).</summary>
    public const string DeploymentEnvValue = "Aide.Secrets.DeploymentEnvValue";

    /// <summary>FR-152: a managed database's connection JSON (URIs, keys, pooler) and its password.</summary>
    public const string ManagedDatabase = "Aide.Secrets.ManagedDatabase";

    /// <summary>GitLab OAuth application secrets and the Bitbucket consumer secret
    /// (<c>git_host_instances.oauth_client_secret_encrypted</c>, FR-130, FR-133).</summary>
    public const string GitHostClientSecret = "Aide.Secrets.GitHostClientSecret";

    /// <summary>GitLab and Bitbucket user tokens: OAuth access and refresh tokens and pasted access/API tokens
    /// (<c>git_connections.access_token_encrypted</c>, <c>refresh_token_encrypted</c>, FR-131, FR-134).</summary>
    public const string GitHostUserToken = "Aide.Secrets.GitHostUserToken";

    /// <summary>The Atlassian account email that goes with a Bitbucket API token (<c>git_connections.token_email_encrypted</c>, FR-134).</summary>
    public const string GitHostTokenEmail = "Aide.Secrets.GitHostTokenEmail";
}

/// <summary>§8: a <see cref="ISecretProtector"/> per secret kind (purpose), e.g. <see cref="SecretPurposes.GitHubUserToken"/>.</summary>
public interface ISecretProtectors
{
    /// <summary>The protector for <paramref name="purpose"/> (one of <see cref="SecretPurposes"/>).</summary>
    ISecretProtector ForPurpose(string purpose);
}

internal sealed class DataProtectionSecretProtectors(IDataProtectionProvider provider) : ISecretProtectors
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ISecretProtector> _protectors = new(StringComparer.Ordinal);

    public ISecretProtector ForPurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        return _protectors.GetOrAdd(purpose, p => new DataProtectionSecretProtector(provider, p));
    }
}

/// <summary>Protects with <see cref="SecretPurposes.V1Shared"/> (which is also <see cref="SecretPurposes.LlmApiKey"/>),
/// or with the purpose given by <see cref="ISecretProtectors"/>.</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider, string purpose) : ISecretProtector
{
    /// <summary>Changing the purpose makes every stored secret unreadable, so it is versioned and fixed.</summary>
    public const string Purpose = SecretPurposes.V1Shared;

    private readonly IDataProtector _protector = provider.CreateProtector(purpose);

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        : this(provider, Purpose)
    {
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        return _protector.Unprotect(protectedValue);
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        try
        {
            plaintext = Unprotect(protectedValue);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = string.Empty;
            return false;
        }
    }
}
