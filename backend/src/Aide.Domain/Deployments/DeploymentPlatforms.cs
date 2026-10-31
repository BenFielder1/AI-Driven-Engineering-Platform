using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Stack;

namespace Aide.Domain.Deployments;

/// <summary>
/// §4.20: what a provider can do. <see cref="ShaPinned"/>: a deployment builds exactly the requested commit (else the
/// branch head, checked afterwards). <see cref="Previews"/>: preview deployments exist. <see cref="Rollback"/>: an earlier
/// deployment can be made current (Vercel promote, Render rollback …). <see cref="PerEnvironmentVariables"/>: env vars
/// differ between preview and production. <see cref="RuntimeSecrets"/>: secrets reach the running app (not only the build).
/// </summary>
public sealed record DeploymentCapabilities(bool ShaPinned, bool Previews, bool Rollback, bool PerEnvironmentVariables, bool RuntimeSecrets);

/// <summary>
/// §4.20, FR-144: the deployment providers: which composition host each one is, its capabilities, its display name, the
/// platform connection it binds, and whether AIDE calls it for real (Vercel and GitHub Pages in M29; Netlify and
/// Cloudflare in M29 slice 3; Render and Railway in M30). Pure.
/// </summary>
public static class DeploymentPlatforms
{
    /// <summary>The providers a composition host can be, in catalog order.</summary>
    public static IReadOnlyList<DeploymentProvider> Hosted { get; } =
    [
        DeploymentProvider.Vercel, DeploymentProvider.Netlify, DeploymentProvider.Cloudflare, DeploymentProvider.GithubPages,
        DeploymentProvider.Render, DeploymentProvider.Railway,
    ];

    /// <summary>The provider of a composition host option (<c>deploy-vercel</c> → <c>vercel</c>); <c>null</c> for None or unknown.</summary>
    public static DeploymentProvider? FromHostOption(string? hostOption) => hostOption switch
    {
        "deploy-vercel" => DeploymentProvider.Vercel,
        "deploy-netlify" => DeploymentProvider.Netlify,
        "deploy-cloudflare" => DeploymentProvider.Cloudflare,
        "deploy-github-pages" => DeploymentProvider.GithubPages,
        "deploy-render" => DeploymentProvider.Render,
        "deploy-railway" => DeploymentProvider.Railway,
        _ => null,
    };

    /// <summary>The composition host option of a provider (<c>null</c> for <c>fake</c>).</summary>
    public static string? HostOption(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Vercel => "deploy-vercel",
        DeploymentProvider.Netlify => "deploy-netlify",
        DeploymentProvider.Cloudflare => "deploy-cloudflare",
        DeploymentProvider.GithubPages => "deploy-github-pages",
        DeploymentProvider.Render => "deploy-render",
        DeploymentProvider.Railway => "deploy-railway",
        _ => null,
    };

    /// <summary>The composition's host option for <paramref name="component"/> (<c>null</c>: None, or no such layer).</summary>
    public static string? HostOf(StackComposition? composition, DeploymentComponent component)
    {
        if (composition is null)
        {
            return null;
        }

        return component == DeploymentComponent.Frontend
            ? composition.Frontend is null ? null : composition.Deploy.FrontendHost
            : composition.Backend is null ? null : composition.Deploy.BackendHost;
    }

    /// <summary>The composition's provider for <paramref name="component"/>, or <c>null</c> (no host: no target card).</summary>
    public static DeploymentProvider? ProviderOf(StackComposition? composition, DeploymentComponent component) =>
        FromHostOption(HostOf(composition, component));

    /// <summary>§4.20 capabilities per provider. The v2 <c>fake</c> provider behaves like Vercel.</summary>
    public static DeploymentCapabilities Capabilities(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Vercel or DeploymentProvider.Fake => new(ShaPinned: true, Previews: true, Rollback: true, PerEnvironmentVariables: true, RuntimeSecrets: true),
        DeploymentProvider.Netlify => new(ShaPinned: false, Previews: true, Rollback: true, PerEnvironmentVariables: true, RuntimeSecrets: false),
        DeploymentProvider.Cloudflare => new(ShaPinned: true, Previews: true, Rollback: true, PerEnvironmentVariables: true, RuntimeSecrets: true),
        DeploymentProvider.Render => new(ShaPinned: true, Previews: true, Rollback: true, PerEnvironmentVariables: true, RuntimeSecrets: true),
        DeploymentProvider.Railway => new(ShaPinned: true, Previews: true, Rollback: true, PerEnvironmentVariables: true, RuntimeSecrets: true),
        DeploymentProvider.GithubPages => new(ShaPinned: false, Previews: false, Rollback: false, PerEnvironmentVariables: false, RuntimeSecrets: false),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>The name shown in copy (<c>{platform}</c> in the §6 error table).</summary>
    public static string DisplayName(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Vercel => "Vercel",
        DeploymentProvider.Netlify => "Netlify",
        DeploymentProvider.Cloudflare => "Cloudflare",
        DeploymentProvider.Render => "Render",
        DeploymentProvider.Railway => "Railway",
        DeploymentProvider.GithubPages => "GitHub Pages",
        DeploymentProvider.Fake => "Fake",
        _ => provider.ToString(),
    };

    /// <summary>
    /// A DNS label for the provider (Fake URLs, <c>https://fake-&lt;sha8&gt;-&lt;component&gt;.&lt;label&gt;.test</c>):
    /// the wire value with <c>_</c> as <c>-</c> (<c>github-pages</c>).
    /// </summary>
    public static string HostLabel(DeploymentProvider provider) => provider.ToWire().Replace('_', '-');

    /// <summary>
    /// FR-142, FR-143: the platform connection a target of <paramref name="provider"/> binds; <c>null</c> for GitHub Pages,
    /// which uses the project's GitHub App installation or PAT. The v2 <c>fake</c> provider binds Vercel connections.
    /// </summary>
    public static PlatformProvider? ConnectionProvider(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Vercel or DeploymentProvider.Fake => PlatformProvider.Vercel,
        DeploymentProvider.Netlify => PlatformProvider.Netlify,
        DeploymentProvider.Cloudflare => PlatformProvider.Cloudflare,
        DeploymentProvider.Render => PlatformProvider.Render,
        DeploymentProvider.Railway => PlatformProvider.Railway,
        _ => null,
    };

    /// <summary>Whether a target of <paramref name="provider"/> needs a bound platform connection.</summary>
    public static bool NeedsConnection(DeploymentProvider provider) => ConnectionProvider(provider) is not null;

    /// <summary>
    /// §4.20 build seam: whether AIDE calls the provider's real API in this release (every provider since M30: Render and
    /// Railway joined Vercel, Netlify, Cloudflare and GitHub Pages). A provider without one works only in Fake mode
    /// (<c>Deployments:Provider=Fake</c>); a real call is <c>PLATFORM_ERROR</c> "not supported yet".
    /// </summary>
    public static bool ImplementedForReal(DeploymentProvider provider) => provider is DeploymentProvider.Vercel or DeploymentProvider.Netlify
        or DeploymentProvider.Cloudflare or DeploymentProvider.GithubPages or DeploymentProvider.Render or DeploymentProvider.Railway or DeploymentProvider.Fake;

    /// <summary>
    /// FR-149: whether the platform deploys the default branch by itself, so a release's production deployment first adopts
    /// the platform's deployment of the merge commit (GitHub Pages: the Pages workflow run). The v2 Fake never does.
    /// </summary>
    public static bool AutoDeploysDefaultBranch(DeploymentProvider provider) => provider != DeploymentProvider.Fake;

    /// <summary>The platform's link to connect its Git integration to <paramref name="vcsHost"/> (<c>github</c>, <c>gitlab</c>, <c>bitbucket</c>).</summary>
    public static string? GitConnectUrl(DeploymentProvider provider, string? vcsHost) => (provider, vcsHost) switch
    {
        (DeploymentProvider.Vercel or DeploymentProvider.Fake, "gitlab" or "bitbucket") => "https://vercel.com/account/login-connections",
        (DeploymentProvider.Vercel or DeploymentProvider.Fake, _) => VercelErrorRules.DefaultGitHubInstallUrl,
        (DeploymentProvider.Netlify, "github") => "https://github.com/apps/netlify/installations/new",
        (DeploymentProvider.Netlify, _) => "https://app.netlify.com/user/applications",
        (DeploymentProvider.Cloudflare, _) => "https://dash.cloudflare.com/?to=/:account/workers-and-pages",
        (DeploymentProvider.Render, _) => "https://dashboard.render.com/account",
        (DeploymentProvider.Railway, _) => "https://github.com/apps/railway-app/installations/new",
        _ => null,
    };

    /// <summary>The <c>vcs</c> pack's host (<c>github</c>, <c>gitlab</c>, <c>bitbucket</c>).</summary>
    public static string? VcsHost(StackComposition? composition) => composition?.Vcs.Pack switch
    {
        "vcs-github" => "github",
        "vcs-gitlab" => "gitlab",
        "vcs-bitbucket" => "bitbucket",
        _ => null,
    };

    /// <summary>§4.20 plan and cost notes surfaced in the readiness checklist (never enforced).</summary>
    public static IReadOnlyList<string> PlanNotes(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Vercel => ["Vercel Hobby can't deploy repositories owned by an organisation; that needs Pro."],
        DeploymentProvider.Netlify =>
        [
            "Production deploys cost 15 credits on credit plans.",
            "The Free plan pauses all sites when its credits run out.",
        ],
        DeploymentProvider.Cloudflare => ["Workers Builds stops a build after 20 minutes."],
        DeploymentProvider.Render =>
        [
            "Free web services sleep after 15 minutes without traffic.",
            "Free Render Postgres expires after 30 days.",
        ],
        DeploymentProvider.Railway => ["The Free and Trial plans limit usage and allow 100 API requests an hour."],
        _ => [],
    };

    /// <summary>FR-144: the component of a route value (<c>frontend</c>, <c>backend</c>).</summary>
    public static bool TryParseComponent(string? value, out DeploymentComponent component) => EnumStrings.TryParse(value, out component);
}

/// <summary>
/// FR-146, §4.17 R10: the origin model. AIDE has three (<c>same-origin</c>, <c>proxy</c>, <c>cross-origin</c>); the
/// stack-packs vocabulary has two, so AIDE <c>same-origin</c> and <c>proxy</c> are pack "same-origin" and <c>cross-origin</c>
/// matches. Pure.
/// </summary>
public static class OriginModels
{
    public const string SameOrigin = "same-origin";
    public const string Proxy = "proxy";
    public const string CrossOrigin = "cross-origin";

    /// <summary>The pack vocabulary's model for an AIDE model; <c>null</c> without one (API only).</summary>
    public static string? ToPackVocabulary(string? model) => model switch
    {
        SameOrigin or Proxy => SameOrigin,
        CrossOrigin => CrossOrigin,
        _ => null,
    };

    /// <summary>The one-line explanation shown with the model (FR-115, FR-146).</summary>
    public static string? Explanation(string? model) => model switch
    {
        SameOrigin => "One origin serves the app and its API (Next.js full-stack, an SPA container proxying /api, or local only).",
        Proxy => "The frontend platform rewrites /api/* to the backend (API_ORIGIN), so the browser sees one origin.",
        CrossOrigin => "The frontend calls the backend's own URL (API_BASE_URL); the backend allows the frontend's origins (CORS_ALLOWED_ORIGINS).",
        _ => null,
    };
}
