namespace Aide.Domain.Stack;

/// <summary>
/// §4.18, PROGRESS "M26 inputs": the layer-path placeholders of pack globs, commands and prompts. <c>{fe}</c> is the
/// frontend path, <c>{be}</c> the backend path and <c>{app}</c> the backend path, else the frontend path (the Next.js
/// full-stack app). The repository root <c>.</c> expands <c>{fe}/x</c> to <c>x</c>. A placeholder whose layer is None
/// drops the glob or command (<c>null</c>); in prose it becomes <c>(no frontend)</c>/<c>(no backend)</c>.
/// </summary>
public static class StackPlaceholders
{
    public const string Frontend = "{fe}";
    public const string Backend = "{be}";
    public const string App = "{app}";

    /// <summary>A glob (optionally <c>!</c>-negated) with its placeholders expanded, or <c>null</c> when a layer is missing.</summary>
    public static string? ExpandGlob(string glob, StackLayerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(glob);
        ArgumentNullException.ThrowIfNull(paths);
        var negated = glob.StartsWith('!');
        var body = negated ? glob[1..] : glob;
        foreach (var (placeholder, path) in Values(paths))
        {
            if (!body.Contains(placeholder, StringComparison.Ordinal))
            {
                continue;
            }

            if (path is null)
            {
                return null;
            }

            body = path == StackPaths.Root
                ? body.Replace(placeholder + "/", string.Empty, StringComparison.Ordinal).Replace(placeholder, StackPaths.Root, StringComparison.Ordinal)
                : body.Replace(placeholder, path, StringComparison.Ordinal);
        }

        return negated ? "!" + body : body;
    }

    /// <summary>A command (gate, CI check) with its placeholders expanded (<c>cd {be}</c> → <c>cd backend</c>), or <c>null</c> when a layer is missing.</summary>
    public static string? ExpandCommand(string command, StackLayerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(paths);
        var text = command;
        foreach (var (placeholder, path) in Values(paths))
        {
            if (!text.Contains(placeholder, StringComparison.Ordinal))
            {
                continue;
            }

            if (path is null)
            {
                return null;
            }

            text = text.Replace(placeholder, path, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Prose (agent prompts) with its placeholders expanded; a missing layer reads <c>(no frontend)</c> or <c>(no backend)</c>.</summary>
    public static string ExpandProse(string text, StackLayerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var (placeholder, path) in Values(paths))
        {
            var missing = placeholder == Frontend ? "(no frontend)" : "(no backend)";
            text = path switch
            {
                null => text.Replace(placeholder, missing, StringComparison.Ordinal),
                StackPaths.Root => text.Replace($"`{placeholder}/`", "`./`", StringComparison.Ordinal)
                    .Replace(placeholder + "/", string.Empty, StringComparison.Ordinal)
                    .Replace(placeholder, StackPaths.Root, StringComparison.Ordinal),
                _ => text.Replace(placeholder, path, StringComparison.Ordinal),
            };
        }

        return text;
    }

    private static (string Placeholder, string? Path)[] Values(StackLayerPaths paths) =>
        [(Frontend, paths.Frontend), (Backend, paths.Backend), (App, paths.App)];
}

/// <summary>
/// FR-120, §4.18: the specialist catalogue entries (name, one-line description for the plan generator) and the owned globs
/// that no pack declares (the scaffolder's <c>**</c>, the test-engineer's E2E globs, the docs-writer's README and docs).
/// </summary>
public static class StackSpecialistCatalogue
{
    public static string Name(string key) => key switch
    {
        StackAgents.Scaffolder => "Scaffolder",
        StackAgents.DataEngineer => "Data engineer",
        StackAgents.BackendEngineer => "Backend engineer",
        StackAgents.FrontendEngineer => "Frontend engineer",
        StackAgents.TestEngineer => "Test engineer",
        StackAgents.CiEngineer => "CI engineer",
        StackAgents.DeployEngineer => "Deploy engineer",
        StackAgents.DocsWriter => "Docs writer",
        _ => key,
    };

    public static string Description(string key) => key switch
    {
        StackAgents.Scaffolder => "Creates the skeleton of every layer (project files, configuration, folder layout) in step 1 of an empty repository or the first step of a stack change.",
        StackAgents.DataEngineer => "Owns the schema: entities or models, one migration per step, seed data and the generated database types.",
        StackAgents.BackendEngineer => "Implements the API: domain rules, endpoints, DTOs, validation, problem-details errors, auth, configuration, unit tests and the contract file.",
        StackAgents.FrontendEngineer => "Implements the UI: routes, pages, components, data access through the generated client, forms, error copy and unit tests.",
        StackAgents.TestEngineer => "Writes the end-to-end tests and the backend integration tests.",
        StackAgents.CiEngineer => "Writes the CI pipeline from the packs' gates, the freshness checks for generated files, and repository automation.",
        StackAgents.DeployEngineer => "Writes container, platform and local dev-loop configuration: Dockerfiles, platform config files, compose.yaml and .env.example.",
        StackAgents.DocsWriter => "Writes the README and the docs from the composition.",
        _ => string.Empty,
    };

    /// <summary>Owned globs that no pack declares (§4.18 ownership table).</summary>
    public static IReadOnlyList<string> CoreOwnership(string key) => key switch
    {
        StackAgents.Scaffolder => ["**"],
        StackAgents.TestEngineer => ["**/e2e/**", "**/playwright.config.*"],
        StackAgents.DocsWriter => ["README.md", "docs/**", "!docs/pr/**"],
        _ => [],
    };

    /// <summary>The specialist that owns a changed layer's work (FR-117 "a step owned by the new layer's specialist").</summary>
    public static string? ForLayer(string layer) => layer switch
    {
        StackLayers.Frontend => StackAgents.FrontendEngineer,
        StackLayers.Backend => StackAgents.BackendEngineer,
        StackLayers.Data => StackAgents.DataEngineer,
        StackLayers.Vcs => StackAgents.CiEngineer,
        StackLayers.Deploy => StackAgents.DeployEngineer,
        _ => null,
    };
}

/// <summary>
/// The adapted stack-core skills that aren't packs (FR-121 records them like packs). The catalog has no manifest for
/// <c>core/stack-api-contract.md</c>, so its version (<c>&lt;plugin version&gt;+aide.&lt;n&gt;</c>) is kept here; bump it with
/// the file's content.
/// </summary>
public static class StackCoreSkills
{
    public const string ApiContract = "stack-api-contract";

    public const string ApiContractVersion = "1.0.0+aide.1";
}

/// <summary>
/// FR-127: the backend packs' API surface (endpoint and DTO files, from each pack's <c>Conventions</c>). A step declaring
/// one adds or changes endpoints, so it (or a later step) must declare the contract file. Not a manifest field.
/// </summary>
public static class StackApiSurface
{
    public static IReadOnlyList<string> Globs(string backendPack) => backendPack switch
    {
        "be-aspnet" => ["{be}/src/*Api*/Features/**", "{be}/src/*Api*/**/*Endpoints.cs"],
        "be-spring-boot" => ["{be}/src/main/java/**/*Controller.java", "{be}/src/main/java/**/dto/**"],
        "be-fastapi" => ["{be}/app/api/**", "{be}/app/routers/**", "{be}/app/schemas/**"],
        "be-express" => ["{be}/src/**/routes/**", "{be}/src/**/*.routes.ts", "{be}/src/**/*.router.ts", "{be}/src/**/*.schemas.ts"],
        "be-nestjs" => ["{be}/src/**/*.controller.ts", "{be}/src/**/dto/**", "{be}/src/**/*.dto.ts"],
        _ => [],
    };
}

/// <summary>§4.18 review check inputs that aren't manifest fields: each backend pack's health paths.</summary>
public static class StackHealth
{
    /// <summary>The live and ready paths of <paramref name="backendPack"/> (Spring Boot: Actuator's probe paths).</summary>
    public static (string Live, string Ready) Paths(string backendPack) => backendPack == "be-spring-boot"
        ? ("/api/health/liveness", "/api/health/readiness")
        : ("/api/health/live", "/api/health/ready");
}

/// <summary>The spec sections no pack declares (§4.18 "Spec sections"), with their guidance.</summary>
public static class StackCoreSpecSections
{
    public const string Source = "core";

    public const string OriginModelTitle = "Origin Model (CORS, Cookies, Auth)";

    public const string OriginModelGuidance =
        "Name the origin model and why: which origin the browser loads the frontend from, and how it reaches the API (the frontend host's /api proxy or rewrite, or cross-origin calls to the API's own URL). For a proxy: the rewrite rule per environment (production and preview point at their own backends). For cross-origin: the CORS policy (CORS_ALLOWED_ORIGINS with the production URL and the preview URL pattern, allowed methods and headers, credentials), and the auth that works across origins (bearer tokens, or cookies with SameSite=None; Secure and CSRF protection). Say which environment variables carry the backend URL.";

    public const string StackMigrationTitle = "Stack Migration";

    public const string StackMigrationGuidance =
        "This version changes the stack. Say what moves (each changed layer: from which pack to which, and which files or folders are replaced, moved or deleted), the data migration (schema and data carried over, in which order, and how it is verified), compatibility during the change (what keeps working at each step, what the old and new code share, and which contracts stay stable), and the rollout (the order of the changes, what each preview deploy shows, and how to roll back).";

    /// <summary>"Data Model &amp; RLS Policies" replaces "Data Model &amp; Migrations" (§4.18).</summary>
    public const string RlsTitle = "Data Model & RLS Policies";

    public const string DataModelTitle = "Data Model & Migrations";
}

/// <summary>The CI file of each VCS pack (composition summary <c>CI_FILE</c>).</summary>
public static class StackVcsFiles
{
    public static string CiFile(string vcsPack) => vcsPack switch
    {
        "vcs-gitlab" => "`.gitlab-ci.yml`",
        "vcs-bitbucket" => "`bitbucket-pipelines.yml`",
        _ => "`.github/workflows/`",
    };
}
