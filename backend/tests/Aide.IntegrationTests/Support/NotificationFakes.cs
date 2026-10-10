using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Aide.Api.Features.Notifications;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>A request a channel sent (Slack, Teams, webhook).</summary>
public sealed record CapturedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Stands in for every Slack, Teams and webhook endpoint: records requests and answers 200 unless a URL is scripted with
/// other statuses. No test reaches the network.
/// </summary>
public sealed class FakeChannelHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpStatusCode>> _scripts = new(StringComparer.Ordinal);

    public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

    public IReadOnlyList<CapturedRequest> To(string url) => [.. _requests.Where(r => r.Uri.AbsoluteUri == new Uri(url).AbsoluteUri)];

    /// <summary>The next requests to <paramref name="url"/> answer these statuses, in order; then 200.</summary>
    public void Script(string url, params HttpStatusCode[] statuses) =>
        _scripts[new Uri(url).AbsoluteUri] = new ConcurrentQueue<HttpStatusCode>(statuses);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _requests.Enqueue(new CapturedRequest(request.RequestUri!, headers, body));
        var status = _scripts.TryGetValue(request.RequestUri!.AbsoluteUri, out var queue) && queue.TryDequeue(out var scripted)
            ? scripted
            : HttpStatusCode.OK;
        return new HttpResponseMessage(status) { Content = new StringContent("ok") };
    }
}

/// <summary>A scriptable SMTP stand-in: records emails; recipients can be scripted to fail.</summary>
public sealed class FakeEmailTransport(bool configured) : IEmailTransport
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured { get; } = configured;

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    /// <summary>The next <paramref name="times"/> sends to <paramref name="to"/> fail transiently.</summary>
    public void FailNext(string to, int times) => _failures[to] = times;

    public Task<(EmailSendResult Result, string? Error)> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_failures.TryGetValue(message.To, out var left) && left > 0)
        {
            _failures[message.To] = left - 1;
            return Task.FromResult<(EmailSendResult, string?)>((EmailSendResult.TransientFailure, "SMTP error (451)."));
        }

        _sent.Enqueue(message);
        return Task.FromResult<(EmailSendResult, string?)>((EmailSendResult.Sent, null));
    }
}

/// <summary>The API with fake channel endpoints and a fake SMTP transport (one host per base factory and SMTP setting).</summary>
public sealed class NotificationTestApp
{
    private static readonly ConditionalWeakTable<AideApiFactory, NotificationTestApp> WithSmtp = [];
    private static readonly ConditionalWeakTable<AideApiFactory, NotificationTestApp> WithoutSmtp = [];

    private NotificationTestApp(AideApiFactory factory, bool smtpConfigured)
    {
        Email = new FakeEmailTransport(smtpConfigured);
        Factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddHttpClient(NotificationHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => Http);
            services.AddSingleton<IEmailTransport>(Email);
        }));
    }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeChannelHandler Http { get; } = new();

    public FakeEmailTransport Email { get; }

    public static NotificationTestApp For(AideApiFactory factory, bool smtpConfigured = true) =>
        (smtpConfigured ? WithSmtp : WithoutSmtp).GetValue(factory, f => new NotificationTestApp(f, smtpConfigured));
}

/// <summary>Drives notification jobs and the sweep in tests (the worker and the sweep loop are off).</summary>
public static class NotificationJobs
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs the project's queued notification jobs (each once); returns the jobs run.</summary>
    public static async Task<List<Job>> RunQueuedAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var queued = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.Notification && j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(Ct));
        foreach (var job in queued)
        {
            await AgentJobs.RunAsync(app, job);
        }

        return queued;
    }

    public static Task<int> RetryDueDeliveriesAsync(WebApplicationFactory<Program> app) =>
        app.Services.GetRequiredService<NotificationSweeper>().RetryDueDeliveriesAsync(Ct);

    public static Task<int> RunGateTimersAsync(WebApplicationFactory<Program> app) =>
        app.Services.GetRequiredService<NotificationSweeper>().RunGateTimersAsync(Ct);

    /// <summary>Makes the project's pending deliveries due now (instead of waiting out the backoff).</summary>
    public static Task MakeDueAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.NotificationDeliveries
            .Where(d => d.ProjectId == projectId && d.Status == NotificationDeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));

    public static Task<List<NotificationDelivery>> DeliveriesAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.ProjectId == projectId).OrderBy(d => d.Channel).ToListAsync(Ct));

    public static Task<List<Notification>> InAppAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Notifications.AsNoTracking()
            .Where(n => n.ProjectId == projectId).OrderBy(n => n.CreatedAt).ToListAsync(Ct));
}
