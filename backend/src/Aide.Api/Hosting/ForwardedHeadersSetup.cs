using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Aide.Api.Hosting;

/// <summary>
/// FR-105: proxy hops trusted for <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> (section <c>ForwardedHeaders</c>).
/// Only takes effect when the forwarded-headers middleware is on (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>).
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// <c>ForwardedHeaders:ForwardLimit</c>: how many right-most <c>X-Forwarded-*</c> entries are trusted. 1 when one
    /// proxy sits in front of the API (Kubernetes: nginx/Next.js); 2 behind an edge plus a proxy (Railway edge → Next.js).
    /// </summary>
    [Range(1, 5)]
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// <c>ForwardedHeaders:KnownNetworks</c>: optional CIDR ranges (<c>10.0.0.0/8</c>, <c>fd00::/8</c>) the proxies
    /// connect from. Empty: every hop within <see cref="ForwardLimit"/> is trusted, as with the plain environment switch.
    /// </summary>
    public IList<string> KnownNetworks { get; init; } = [];
}

/// <summary>
/// FR-105: tunes the framework's forwarded-headers switch. <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> still adds the
/// middleware (first in the pipeline); this sets which headers it honours and how far it trusts them.
/// </summary>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddAideForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersSettings>()
            .BindConfiguration(ForwardedHeadersSettings.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                s => s.KnownNetworks.All(n => IPNetwork.TryParse(n.Trim(), out _)),
                "ForwardedHeaders:KnownNetworks entries must be CIDR ranges such as 10.0.0.0/8.")
            .ValidateOnStart();

        // PostConfigure: runs after the host's own Configure for the environment switch, whatever the registration order.
        services.AddOptions<ForwardedHeadersOptions>()
            .PostConfigure<IOptions<ForwardedHeadersSettings>>((options, settings) => Apply(options, settings.Value));
        return services;
    }

    /// <summary>Applies <paramref name="settings"/> to the middleware's options.</summary>
    public static void Apply(ForwardedHeadersOptions options, ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        // -Host too: the Live origin check compares the browser's Origin with the request's own host (FR-46).
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = settings.ForwardLimit;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in settings.KnownNetworks)
        {
            options.KnownIPNetworks.Add(IPNetwork.Parse(network.Trim()));
        }
    }
}
