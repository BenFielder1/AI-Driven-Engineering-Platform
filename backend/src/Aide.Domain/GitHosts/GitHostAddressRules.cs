using System.Net;
using System.Net.Sockets;
using Aide.Domain.Common;
using Aide.Domain.Notifications;

namespace Aide.Domain.GitHosts;

/// <summary>What an address (or host name) of a GitLab instance is, for the SSRF guard (§8).</summary>
public enum GitHostAddressClass
{
    /// <summary>A public unicast address: allowed.</summary>
    Public = 0,

    /// <summary>127.0.0.0/8, <c>::1</c>, <c>localhost</c>: always refused.</summary>
    Loopback = 1,

    /// <summary>169.254.0.0/16 and fe80::/10: always refused.</summary>
    LinkLocal = 2,

    /// <summary>Cloud metadata (169.254.169.254, <c>fd00:ec2::254</c>): always refused.</summary>
    Metadata = 3,

    /// <summary><c>0.0.0.0</c> (0.0.0.0/8) and <c>::</c>: always refused.</summary>
    Unspecified = 4,

    /// <summary>10/8, 172.16/12, 192.168/16, fc00::/7 and <c>*.railway.internal</c>: allowed only with
    /// <c>GitLab:AllowPrivateNetworks=true</c>.</summary>
    Private = 5,

    /// <summary>Any other non-public range (shared/CGNAT, multicast, documentation, reserved, IPv4 tunnelled in IPv6 to a
    /// non-public address): always refused.</summary>
    Reserved = 6,
}

/// <summary>The SSRF guard's verdict on one address or host name.</summary>
public sealed record GitHostAddressDecision(bool Allowed, GitHostAddressClass Class, string? Reason)
{
    public static GitHostAddressDecision Allow(GitHostAddressClass @class) => new(true, @class, null);
}

/// <summary>
/// FR-130, §8 (SSRF): the rules for an Admin-registered GitLab instance's base URL and the addresses AIDE may connect to.
/// <list type="bullet">
/// <item>Base URLs are <c>https</c> (or <c>http</c> only with <c>allow_insecure_http</c>), with a host, and without user
/// info, query or fragment. The path (a relative URL root such as <c>/gitlab</c>) is kept, without a trailing slash.</item>
/// <item>Always refused: loopback, link-local (169.254.0.0/16, fe80::/10), metadata (<c>fd00:ec2::254</c>) and
/// <c>0.0.0.0</c>, plus every other non-public range.</item>
/// <item>Private ranges (10/8, 172.16/12, 192.168/16, fc00::/7, <c>*.railway.internal</c>) only with
/// <c>allowPrivateNetworks</c>.</item>
/// <item>IPv4 addresses mapped into IPv6 are judged as IPv4. The resolved addresses are checked on every connection
/// (DNS rebinding), and cross-host redirects are never followed.</item>
/// </list>
/// Pure: resolution happens in Infrastructure, which passes the addresses here.
/// </summary>
public static class GitHostAddressRules
{
    /// <summary>The longest accepted base URL.</summary>
    public const int MaxBaseUrlLength = 2048;

    /// <summary>The Railway private-network domain (AIDE's own services on Railway).</summary>
    public const string RailwayInternalSuffix = ".railway.internal";

    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");

    /// <summary>
    /// FR-130: validates and normalises a base URL: lower-case scheme and host, the default port dropped, the trailing
    /// slash trimmed. Malformed input is <c>VALIDATION</c> (on <c>baseUrl</c>); a scheme or host the SSRF guard refuses is
    /// <c>GIT_HOST_NOT_ALLOWED</c>. An IP-literal host is classified here; a host name is classified by name
    /// (<c>localhost</c>, <c>*.railway.internal</c>) and its resolved addresses later with <see cref="CheckResolved"/>.
    /// </summary>
    public static Result<Uri> NormalizeBaseUrl(string? text, bool allowInsecureHttp, bool allowPrivateNetworks)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Invalid("The base URL is required.");
        }

        if (trimmed.Length > MaxBaseUrlLength)
        {
            return Invalid($"The base URL can't be longer than {MaxBaseUrlLength} characters.");
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl))
        {
            return Invalid("The base URL can't contain spaces, backslashes or control characters.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return Invalid("Enter an absolute URL such as https://gitlab.example.com.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return NotAllowed($"Only https:// base URLs are allowed (not {uri.Scheme}://).");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
        {
            return NotAllowed("Only https:// base URLs are allowed, unless \"allow insecure HTTP\" is ticked for the instance.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("The base URL can't contain a user name or password.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || trimmed.Contains('?', StringComparison.Ordinal))
        {
            return Invalid("The base URL can't contain a query string.");
        }

        if (!string.IsNullOrEmpty(uri.Fragment) || trimmed.Contains('#', StringComparison.Ordinal))
        {
            return Invalid("The base URL can't contain a fragment.");
        }

        // Uri removes dot segments itself; refusing them in the input keeps the stored URL what the Admin typed.
        var rawPath = RawPath(trimmed);
        if (rawPath.Split('/').Any(segment => segment is "." or ".." || segment.Equals("%2e", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase)))
        {
            return Invalid("The base URL's path can't contain . or .. segments.");
        }

        var hostDecision = uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal)
                ? Classify(literal, allowPrivateNetworks)
                : uri.HostNameType == UriHostNameType.Dns
                    ? ClassifyHostName(uri.IdnHost, allowPrivateNetworks)
                    : new GitHostAddressDecision(false, GitHostAddressClass.Reserved, "The host isn't a valid host name or IP address.");
        if (!hostDecision.Allowed)
        {
            return NotAllowed(hostDecision.Reason ?? "That address isn't allowed for a GitLab instance.");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
        return Result.Success(new Uri($"{uri.Scheme}://{host.ToLowerInvariant()}{port}{path}", UriKind.Absolute));
    }

    /// <summary>
    /// §8: a host name before resolution: <c>localhost</c> and <c>*.localhost</c> are loopback; <c>*.railway.internal</c> is
    /// private. Anything else is public as a name; its addresses decide (<see cref="CheckResolved"/>).
    /// </summary>
    public static GitHostAddressDecision ClassifyHostName(string host, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return Refuse(GitHostAddressClass.Loopback);
        }

        if (name == RailwayInternalSuffix[1..] || name.EndsWith(RailwayInternalSuffix, StringComparison.Ordinal))
        {
            return allowPrivateNetworks ? GitHostAddressDecision.Allow(GitHostAddressClass.Private) : Refuse(GitHostAddressClass.Private);
        }

        return GitHostAddressDecision.Allow(GitHostAddressClass.Public);
    }

    /// <summary>§8: one resolved (or literal) address.</summary>
    public static GitHostAddressDecision Classify(IPAddress address, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        var @class = ClassOf(address);
        return @class switch
        {
            GitHostAddressClass.Public => GitHostAddressDecision.Allow(@class),
            GitHostAddressClass.Private when allowPrivateNetworks => GitHostAddressDecision.Allow(@class),
            _ => Refuse(@class),
        };
    }

    /// <summary>
    /// §8 (DNS-rebinding guard, on every connection): the host's resolved addresses are allowed only when there is at least
    /// one and every one is allowed; the first refused address decides.
    /// </summary>
    public static GitHostAddressDecision CheckResolved(IEnumerable<IPAddress> addresses, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        GitHostAddressDecision? last = null;
        foreach (var address in addresses)
        {
            var decision = Classify(address, allowPrivateNetworks);
            if (!decision.Allowed)
            {
                return decision;
            }

            last = decision;
        }

        return last ?? new GitHostAddressDecision(false, GitHostAddressClass.Reserved, "The host name didn't resolve to any address.");
    }

    /// <summary>
    /// §8: a redirect is followed only to the same scheme, host and port (cross-host redirects never are, so tokens are
    /// never sent to another host).
    /// </summary>
    public static bool IsRedirectAllowed(Uri from, Uri to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return to.IsAbsoluteUri && SameOrigin(from, to);
    }

    /// <summary>§8: tokens are bound to the instance that issued them: a request may carry one only to that origin.</summary>
    public static bool SameOrigin(Uri instanceBaseUrl, Uri requestUrl)
    {
        ArgumentNullException.ThrowIfNull(instanceBaseUrl);
        ArgumentNullException.ThrowIfNull(requestUrl);
        return instanceBaseUrl.IsAbsoluteUri && requestUrl.IsAbsoluteUri
            && string.Equals(instanceBaseUrl.Scheme, requestUrl.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(instanceBaseUrl.IdnHost, requestUrl.IdnHost, StringComparison.OrdinalIgnoreCase)
            && instanceBaseUrl.Port == requestUrl.Port;
    }

    private static GitHostAddressClass ClassOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b switch
            {
                [0, ..] => GitHostAddressClass.Unspecified,
                [127, ..] => GitHostAddressClass.Loopback,
                [169, 254, 169, 254] => GitHostAddressClass.Metadata,
                [169, 254, ..] => GitHostAddressClass.LinkLocal,
                [10, ..] => GitHostAddressClass.Private,
                [172, >= 16 and <= 31, ..] => GitHostAddressClass.Private,
                [192, 168, ..] => GitHostAddressClass.Private,
                _ => NetworkRules.IsPublic(address) ? GitHostAddressClass.Public : GitHostAddressClass.Reserved,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return GitHostAddressClass.Reserved;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.All(x => x == 0))
        {
            return GitHostAddressClass.Unspecified;
        }

        if (bytes.AsSpan(0, 15).IndexOfAnyExcept((byte)0) < 0 && bytes[15] == 1)
        {
            return GitHostAddressClass.Loopback;
        }

        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return GitHostAddressClass.LinkLocal; // fe80::/10
        }

        if (bytes.AsSpan().SequenceEqual(AwsMetadataV6.GetAddressBytes()))
        {
            return GitHostAddressClass.Metadata;
        }

        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return GitHostAddressClass.Private; // fc00::/7 unique local
        }

        return NetworkRules.IsPublic(address) ? GitHostAddressClass.Public : GitHostAddressClass.Reserved;
    }

    private static GitHostAddressDecision Refuse(GitHostAddressClass @class) => new(false, @class, @class switch
    {
        GitHostAddressClass.Loopback => "Loopback addresses aren't allowed for a GitLab instance.",
        GitHostAddressClass.LinkLocal => "Link-local addresses aren't allowed for a GitLab instance.",
        GitHostAddressClass.Metadata => "Cloud metadata addresses aren't allowed for a GitLab instance.",
        GitHostAddressClass.Unspecified => "The unspecified address (0.0.0.0) isn't allowed for a GitLab instance.",
        GitHostAddressClass.Private => "Private network addresses aren't allowed for a GitLab instance on this server (GitLab:AllowPrivateNetworks is off).",
        _ => "That address isn't allowed for a GitLab instance.",
    });

    private static string RawPath(string url)
    {
        var afterScheme = url.IndexOf("://", StringComparison.Ordinal);
        var start = afterScheme < 0 ? 0 : afterScheme + 3;
        var slash = url.IndexOf('/', start);
        return slash < 0 ? string.Empty : url[slash..];
    }

    private static Result<Uri> Invalid(string detail) =>
        Result.Failure<Uri>(new DomainError(ErrorCodes.Validation, "The GitLab instance is invalid.") { Details = [$"baseUrl: {detail}"] });

    private static Result<Uri> NotAllowed(string detail) =>
        Result.Failure<Uri>(new DomainError(ErrorCodes.GitHostNotAllowed, "That address isn't allowed for a GitLab instance.") { Details = [$"baseUrl: {detail}"] });
}
