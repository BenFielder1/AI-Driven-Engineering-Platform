using System.Security.Cryptography;

namespace Tevrin.Engine.Runtime.Security;

/// <summary>
/// Encrypts secrets at rest (§8 NFR-SEC, FR-9: Git tokens, LLM keys, webhook URLs). The host implements it (Tevrin: with
/// ASP.NET Data Protection, in Tevrin.Infrastructure), so the engine never references ASP.NET Core (FR-263, D-0.7.0-20).
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Decrypts a value from <see cref="Protect"/>. Throws <see cref="CryptographicException"/> if it was
    /// tampered with or the key ring no longer has its key.</summary>
    string Unprotect(string protectedValue);

    /// <summary>Like <see cref="Unprotect"/>, but returns <c>false</c> instead of throwing.</summary>
    bool TryUnprotect(string protectedValue, out string plaintext);
}
