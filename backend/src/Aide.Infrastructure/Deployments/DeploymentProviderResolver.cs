using System.Collections.Concurrent;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// §9, §4.20: the provider for a platform id. In Fake mode (<c>Deployments:Provider=Fake</c>), and for v2's explicit
/// <c>fake</c> provider, every platform is its <see cref="FakeDeploymentProvider"/>. Otherwise Vercel and GitHub Pages are
/// real (Netlify and Cloudflare since slice 3), and Render and Railway are <see cref="UnsupportedDeploymentProvider"/> until their slices.
/// </summary>
public sealed class DeploymentProviderResolver(
    IOptions<DeploymentOptions> options,
    VercelApiClient vercelApi,
    FakeVercelClient fakeVercel,
    VercelDeploymentProvider vercel,
    GitHubPagesDeploymentProvider pages,
    NetlifyDeploymentProvider netlify,
    CloudflareDeploymentProvider cloudflare,
    IWorkingCopies workingCopies) : IDeploymentProviderResolver
{
    /// <summary>Fake poll counts, shared by every Fake platform (deployment ids are unique).</summary>
    private readonly ConcurrentDictionary<string, int> _polls = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<DeploymentProvider, FakeDeploymentProvider> _fakes = new();

    public bool UsesFake => options.Value.UseFake;

    public IVercelClient Vercel => UsesFake ? fakeVercel : vercelApi;

    public IDeploymentProvider ForPlatform(DeploymentProvider platform)
    {
        if (UsesFake || platform == DeploymentProvider.Fake)
        {
            return _fakes.GetOrAdd(platform, p => new FakeDeploymentProvider(p, workingCopies, _polls));
        }

        return platform switch
        {
            DeploymentProvider.Vercel => vercel,
            DeploymentProvider.GithubPages => pages,
            DeploymentProvider.Netlify => netlify,
            DeploymentProvider.Cloudflare => cloudflare,
            _ => new UnsupportedDeploymentProvider(platform),
        };
    }
}
