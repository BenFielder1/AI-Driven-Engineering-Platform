using System.Security.Cryptography;
using System.Text;

namespace Aide.Domain.Artifacts;

/// <summary>§4.3: SHA-256 of the UTF-8 content with line endings normalised to <c>\n</c>, as lowercase hex.</summary>
public static class ContentHash
{
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalised = NormaliseLineEndings(content);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>FR-20 integrity check: does the stored content still match its stored hash?</summary>
    public static bool Matches(string content, string expectedHash) =>
        string.Equals(Compute(content), expectedHash, StringComparison.Ordinal);

    public static string NormaliseLineEndings(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
