namespace Tevrin.Engine.Runtime.Notifications;

/// <summary>App-wide settings (section <c>App</c>).</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>The browser-facing base URL, used for absolute links in channel messages (env <c>App__PublicBaseUrl</c>).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary><see cref="PublicBaseUrl"/> + <paramref name="path"/> (a path starting with <c>/</c>).</summary>
    public string Absolute(string path) => PublicBaseUrl.TrimEnd('/') + path;
}

/// <summary>
/// One-click approval links (section <c>Approvals</c>, FR-48). <c>SigningKey</c> (env <c>Approvals__SigningKey</c>, at least
/// 32 characters) signs the link tokens; without it no links are created (fail closed) and gate notifications link to the
/// project page only.
/// </summary>
public sealed class ApprovalOptions
{
    public const string SectionName = "Approvals";

    public string? SigningKey { get; set; }
}
