using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace Aide.UnitTests.Notifications;

/// <summary>FR-49: a send always ends well inside the delivery's claim lease, so a delivery is never re-sent while in flight.</summary>
public class DeliveryTimeoutTests
{
    [Fact]
    public void FR49_The_claim_lease_is_comfortably_longer_than_the_longest_allowed_send()
    {
        Assert.True(DeliveryPolicy.ClaimLease >= DeliveryPolicy.MaxSendTimeout * 2);
        Assert.True(DeliveryPolicy.IsValidSendTimeout(TimeSpan.FromSeconds(15)));
        Assert.True(DeliveryPolicy.IsValidSendTimeout(DeliveryPolicy.MaxSendTimeout));
        Assert.False(DeliveryPolicy.IsValidSendTimeout(DeliveryPolicy.MaxSendTimeout + TimeSpan.FromSeconds(1)));
        Assert.False(DeliveryPolicy.IsValidSendTimeout(TimeSpan.Zero));
        Assert.True(new SmtpOptions().Timeout <= DeliveryPolicy.MaxSendTimeout);
    }

    [Fact]
    public async Task FR49_An_SMTP_server_that_never_answers_times_out_as_a_transient_failure()
    {
        // A local socket that accepts and then stays silent (no SMTP greeting): no real mail server is involved.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var transport = new SmtpEmailTransport(Options.Create(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            EnableSsl = false,
            Timeout = TimeSpan.FromMilliseconds(500),
        }));

        var watch = Stopwatch.StartNew();
        var (result, error) = await transport.SendAsync(new EmailMessage("ops@example.com", "Gate", "Body"), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal((EmailSendResult.TransientFailure, "SMTP timed out."), (result, error));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
        using var client = await accepted;
    }
}
