namespace Aide.Domain.Notifications;

/// <summary>
/// FR-198: the product-facing text the email, Slack and Teams channels share. Notifications already sent are not rewritten.
/// </summary>
public static class ChannelText
{
    /// <summary>The product name people see (§4.29: identifiers such as cookies and headers keep <c>aide</c>).</summary>
    public const string ProductName = "Tevrin";

    /// <summary>The label of the link back to the app in every channel.</summary>
    public const string OpenLinkLabel = "Open in " + ProductName;

    /// <summary>An email's subject: <c>[Tevrin] &lt;title&gt;</c>.</summary>
    public static string EmailSubject(string title) => $"[{ProductName}] {title}";
}
