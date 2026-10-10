using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Notifications;

/// <summary>
/// SMTP settings for the email channel (section <c>Smtp</c>; env <c>Smtp__Host</c>, <c>Smtp__Port</c>, <c>Smtp__Username</c>,
/// <c>Smtp__Password</c>, <c>Smtp__From</c>, <c>Smtp__EnableSsl</c>). A blank host disables email: email deliveries are
/// marked skipped with a logged warning and the other channels still send.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "aide@aide.local";

    public bool EnableSsl { get; set; } = true;

    /// <summary>
    /// Bound on one whole send (connect, TLS, auth, data). At most <see cref="Aide.Domain.Notifications.DeliveryPolicy.MaxSendTimeout"/>,
    /// so a send always ends well inside the delivery's claim lease.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>A plain-text email to one recipient.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>The outcome of an email send attempt that didn't throw.</summary>
public enum EmailSendResult
{
    Sent = 0,
    NotConfigured = 1,
    TransientFailure = 2,
    PermanentFailure = 3,
}

/// <summary>Sends notification email (FR-47). Faked in tests: no test talks to an SMTP server.</summary>
public interface IEmailTransport
{
    bool IsConfigured { get; }

    Task<(EmailSendResult Result, string? Error)> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary><see cref="IEmailTransport"/> over <see cref="SmtpClient"/> (no extra package). The SMTP host is system configuration only.</summary>
public sealed class SmtpEmailTransport(IOptions<SmtpOptions> options) : IEmailTransport
{
    private readonly SmtpOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<(EmailSendResult Result, string? Error)> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsConfigured)
        {
            return (EmailSendResult.NotConfigured, "SMTP is not configured.");
        }

        MailMessage mail;
        try
        {
            mail = new MailMessage(new MailAddress(_options.From), new MailAddress(message.To))
            {
                Subject = message.Subject.ReplaceLineEndings(" "),
                Body = message.TextBody,
                IsBodyHtml = false,
            };
        }
        catch (FormatException)
        {
            return (EmailSendResult.PermanentFailure, "The sender or recipient address is invalid.");
        }

        using (mail)
        using (var client = new SmtpClient(_options.Host!.Trim(), _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)_options.Timeout.TotalMilliseconds,
        })
        {
            if (!string.IsNullOrEmpty(_options.Username))
            {
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            }

            // SmtpClient.Timeout applies to synchronous sends only: the async send is bounded by this token instead, so an
            // attempt always ends well inside the delivery's claim lease (FR-49: a delivery is never re-sent while in flight).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            try
            {
                await client.SendMailAsync(mail, timeout.Token);
                return (EmailSendResult.Sent, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (EmailSendResult.TransientFailure, "SMTP timed out.");
            }
            catch (SmtpException ex) when (!cancellationToken.IsCancellationRequested && (timeout.IsCancellationRequested || IsTimeout(ex)))
            {
                // A cancelled or timed-out send can surface as a general SMTP failure wrapping the cancellation.
                return (EmailSendResult.TransientFailure, "SMTP timed out.");
            }
            catch (SmtpFailedRecipientException ex) when ((int)ex.StatusCode >= 500)
            {
                return (EmailSendResult.PermanentFailure, $"SMTP refused the recipient ({(int)ex.StatusCode}).");
            }
            catch (SmtpException ex)
            {
                // Never log or store the server's text: it can echo credentials in some misconfigurations.
                return (EmailSendResult.TransientFailure, $"SMTP error ({(int)ex.StatusCode}).");
            }
        }
    }

    private static bool IsTimeout(Exception ex)
    {
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
