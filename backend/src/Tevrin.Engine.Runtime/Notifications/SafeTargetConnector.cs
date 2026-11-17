using System.Net;
using System.Net.Sockets;
using Aide.Domain.Notifications;

namespace Aide.Infrastructure.Notifications;

/// <summary>A notification target resolved only to addresses the API must not reach (NFR-SEC, SSRF).</summary>
public sealed class BlockedTargetException : IOException
{
    public BlockedTargetException()
    {
    }

    public BlockedTargetException(string message)
        : base(message)
    {
    }

    public BlockedTargetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// NFR-SEC (SSRF) for Slack, Teams and webhook channels: the <c>ConnectCallback</c> of their <c>SocketsHttpHandler</c>.
/// At connect time (every new connection, so DNS changes can't slip past) the host is resolved, every address is vetted
/// with <see cref="NetworkRules.IsPublic"/>, and the socket connects to a vetted address — never re-resolving the name.
/// If any resolved address is refused the target is refused, so a name that mixes public and private answers can't be
/// used to reach the private one.
/// </summary>
public sealed class SafeTargetConnector(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Dns.GetHostAddressesAsync;

    /// <summary>The addresses <paramref name="host"/> may be reached at; throws <see cref="BlockedTargetException"/> if none.</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var name = host.Trim().TrimStart('[').TrimEnd(']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(name, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolve(name, cancellationToken);
            }
            catch (SocketException ex)
            {
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"The host {name} could not be resolved.", ex);
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"The host {name} has no addresses.");
        }

        if (addresses.Any(a => !NetworkRules.IsPublic(a)))
        {
            throw new BlockedTargetException($"The host {name} resolves to an address that notifications may not reach (private, loopback, link-local or reserved).");
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
}
