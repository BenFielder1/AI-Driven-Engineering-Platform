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
}

/// <summary>Protects with <see cref="SecretPurposes.V1Shared"/> (which is also <see cref="SecretPurposes.LlmApiKey"/>).</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    /// <summary>Changing the purpose makes every stored secret unreadable, so it is versioned and fixed.</summary>
    public const string Purpose = SecretPurposes.V1Shared;

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

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
