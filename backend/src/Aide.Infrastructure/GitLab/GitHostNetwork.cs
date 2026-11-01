using System.Net;
using System.Net.Sockets;
using Aide.Domain.GitHosts;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.GitLab;

/// <summary>§8: a Git host request refused by the SSRF guard (the host resolves to an address AIDE must not reach).</summary>
public sealed class GitHostBlockedException : IOException
{
    public GitHostBlockedException()
    {
    }

    public GitHostBlockedException(string message)
        : base(message)
    {
    }

    public GitHostBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The guard's exception inside <paramref name="exception"/> (HttpClient wraps connect failures), if any.</summary>
    public static GitHostBlockedException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is GitHostBlockedException blocked)
            {
                return blocked;
            }
        }

        return null;
    }
}

/// <summary>Name resolution for Git hosts; replaceable in tests (no test resolves a real name).</summary>
public interface IGitHostDns
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemGitHostDns : IGitHostDns
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// §8 SSRF guard for GitLab instances, used as the <c>ConnectCallback</c> of the GitLab <c>SocketsHttpHandler</c>: on every
/// new connection (so DNS rebinding can't slip past an earlier check) the host name is classified
/// (<see cref="GitHostAddressRules.ClassifyHostName"/>), resolved once, every address is checked with
/// <see cref="GitHostAddressRules.CheckResolved"/> (honouring <c>GitLab:AllowPrivateNetworks</c>), and the socket connects to
/// a vetted address without resolving again. A name with any refused address is refused.
/// </summary>
public sealed class GitHostConnector(IGitHostDns dns, IOptionsMonitor<GitLabOptions> options)
{
    /// <summary>The addresses <paramref name="host"/> may be reached at; throws <see cref="GitHostBlockedException"/> otherwise.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var allowPrivate = options.CurrentValue.AllowPrivateNetworks;
        var name = host.Trim().TrimStart('[').TrimEnd(']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(name, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            var byName = GitHostAddressRules.ClassifyHostName(name, allowPrivate);
            if (!byName.Allowed)
            {
                throw new GitHostBlockedException(byName.Reason ?? "That address isn't allowed for a GitLab instance.");
            }

            try
            {
                addresses = await dns.ResolveAsync(name, cancellationToken);
            }
            catch (SocketException ex)
            {
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"The host {name} could not be resolved.", ex);
            }
        }

        var decision = GitHostAddressRules.CheckResolved(addresses, allowPrivate);
        if (!decision.Allowed)
        {
            throw new GitHostBlockedException(decision.Reason ?? "That address isn't allowed for a GitLab instance.");
        }

        return addresses;
    }

    /// <summary>The <c>SocketsHttpHandler.ConnectCallback</c>: connect only to a vetted address.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        var addresses = await ResolveAllowedAsync(endpoint.Host, cancellationToken);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(HttpRequestError.ConnectionError, $"Could not connect to {endpoint.Host}.", last);
    }

    /// <summary>
    /// The GitLab primary handler: the SSRF <see cref="ConnectAsync"/>, no redirects (cross-host redirects are never followed,
    /// and same-host ones aren't needed by the API), and no proxy (a proxy would bypass the address check).
    /// </summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectCallback = ConnectAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All,
    };
}
