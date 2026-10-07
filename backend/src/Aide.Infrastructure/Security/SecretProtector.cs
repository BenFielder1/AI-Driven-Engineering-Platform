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

internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    /// <summary>Changing the purpose makes every stored secret unreadable, so it is versioned and fixed.</summary>
    public const string Purpose = "Aide.Secrets.v1";

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
