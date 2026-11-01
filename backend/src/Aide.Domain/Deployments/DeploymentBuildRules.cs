using Aide.Domain.Entities;
using Aide.Domain.Stack;

namespace Aide.Domain.Deployments;

/// <summary>A target's derived build: the root directory (the layer path) and what the platform runs.</summary>
public sealed record DeploymentBuildPlan(string RootDirectory, DeploymentBuildConfig Config);

/// <summary>
/// FR-144, §4.20: the build configuration of a target, derived from the composition: the root directory (the layer path,
/// FR-119), build command, output directory, start command, Dockerfile, health check path (the backend pack's ready path)
/// and pre-deploy migrate command (the data pack's, for a host-mode database on a container host). Platform settings AIDE
/// sends when it creates the resource; the deploy-engineer writes the files they point at. Pure.
/// </summary>
public static class DeploymentBuildRules
{
    public const string NpmBuild = "npm run build";
    public const string NpmStart = "npm run start";

    /// <summary>
    /// The build plan of <paramref name="component"/> on <paramref name="provider"/>; <paramref name="migrateCommand"/> is the
    /// data pack's one-off migrate command (<c>null</c> without one). A component the composition doesn't have gets the
    /// repository root and an empty configuration.
    /// </summary>
    public static DeploymentBuildPlan Derive(StackComposition? composition, DeploymentComponent component, DeploymentProvider provider, string? migrateCommand)
    {
        var layer = component == DeploymentComponent.Frontend ? composition?.Frontend : composition?.Backend;
        if (layer is null)
        {
            return new DeploymentBuildPlan(DeploymentTarget.DefaultRootDirectory, new DeploymentBuildConfig());
        }

        var root = string.IsNullOrEmpty(layer.Path) ? DeploymentTarget.DefaultRootDirectory : layer.Path.TrimEnd('/');
        var config = component == DeploymentComponent.Frontend
            ? Frontend(layer.Pack, provider, root)
            : Backend(composition!, layer.Pack, provider, root, migrateCommand);
        return new DeploymentBuildPlan(root, config);
    }

    /// <summary>The repository path of the Dockerfile in <paramref name="root"/>.</summary>
    public static string DockerfilePath(string root) => root is "" or "." ? "Dockerfile" : $"{root}/Dockerfile";

    /// <summary>Whether the composition's data layer is migrated by the backend's pre-deploy (an ORM pack on a database host).</summary>
    public static bool BackendMigrates(StackComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return composition.Data is { } data && data.Pack != StackIds.Supabase && composition.Deploy.DatabaseHost is not null;
    }

    private static DeploymentBuildConfig Frontend(string pack, DeploymentProvider provider, string root)
    {
        var nextjs = pack == StackIds.NextJs;
        var angular = pack == "fe-angular";
        var framework = nextjs ? "nextjs" : angular ? "angular" : "vite";

        // Static exports: Next.js `out` (ui mode on GitHub Pages), Angular `dist/<project>/browser` (the project name isn't
        // known, so the platform's Angular preset decides on Vercel; static hosts get `dist`), Vite `dist`.
        var staticOutput = nextjs ? "out" : "dist";
        return provider switch
        {
            DeploymentProvider.Vercel or DeploymentProvider.Fake => new DeploymentBuildConfig
            {
                Framework = framework,
                BuildCommand = NpmBuild,
                OutputDirectory = nextjs || angular ? null : "dist",
            },
            DeploymentProvider.GithubPages => new DeploymentBuildConfig { Framework = framework, BuildCommand = NpmBuild, OutputDirectory = staticOutput },
            DeploymentProvider.Netlify or DeploymentProvider.Cloudflare => new DeploymentBuildConfig
            {
                Framework = framework,
                BuildCommand = NpmBuild,
                OutputDirectory = nextjs ? null : "dist",
            },

            // Render: a Node web service for Next.js, else a static site publishing the build output.
            DeploymentProvider.Render => nextjs
                ? new DeploymentBuildConfig { Framework = framework, BuildCommand = $"npm ci && {NpmBuild}", StartCommand = NpmStart }
                : new DeploymentBuildConfig { Framework = framework, BuildCommand = $"npm ci && {NpmBuild}", OutputDirectory = "dist" },

            // Railway: the frontend's own container (an nginx image for an SPA, §4.20, R6).
            DeploymentProvider.Railway => new DeploymentBuildConfig { Framework = framework, DockerfilePath = DockerfilePath(root) },
            _ => new DeploymentBuildConfig { Framework = framework },
        };
    }

    private static DeploymentBuildConfig Backend(StackComposition composition, string pack, DeploymentProvider provider, string root, string? migrateCommand)
    {
        var ready = StackHealth.Paths(pack).Ready;
        var preDeploy = BackendMigrates(composition) ? migrateCommand : null;
        return provider switch
        {
            // Containers (FR-144 example: an ASP.NET backend on Render is a Docker web service with the EF bundle pre-deploy).
            DeploymentProvider.Render or DeploymentProvider.Railway => new DeploymentBuildConfig
            {
                DockerfilePath = DockerfilePath(root),
                HealthCheckPath = ready,
                PreDeployCommand = preDeploy,
            },

            // Vercel serverless functions (FastAPI, Express, NestJS; R7 caveat): migrations run in the build through
            // AIDE_RUN_MIGRATIONS (FR-154), not a pre-deploy command.
            DeploymentProvider.Vercel or DeploymentProvider.Fake => new DeploymentBuildConfig
            {
                BuildCommand = pack is "be-express" or "be-nestjs" ? NpmBuild : null,
                HealthCheckPath = ready,
            },
            _ => new DeploymentBuildConfig { HealthCheckPath = ready },
        };
    }
}
