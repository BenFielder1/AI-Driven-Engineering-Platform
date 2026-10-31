using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Stack;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// FR-120–FR-128 (D13, §8 composition E2E): what the Fake responders read from a composed prompt. Every composed block
/// starts with the composition summary (<see cref="StackGuidance.Heading"/>): its Packs table gives the packs and layer
/// paths, its Specialists list the agents in play; the plan guidance says whether the repository is empty and which
/// layers a stack change changed, and lists the contract files.
/// </summary>
public sealed partial record FakeComposition(
    string? FrontendPack,
    string? FrontendPath,
    string? BackendPack,
    string? BackendPath,
    string? DataPack,
    string VcsPack,
    string? FrontendHost,
    string? BackendHost,
    bool ComposeLoop,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> LayersChanged,
    IReadOnlyList<string> ContractFiles)
{
    /// <summary>The composition of a composed system prompt, or <c>null</c> when the prompt has none.</summary>
    public static FakeComposition? Read(string system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!HeadingPattern().IsMatch(system))
        {
            return null;
        }

        (string? Pack, string? Path) Row(string layer)
        {
            var m = Regex.Match(system, $@"^\| {Regex.Escape(layer)} \| (?:`(?<pack>[a-z0-9-]+)@[^`]*`[^|]*|(?<none>[^|]*))\| (?:`(?<path>[^`]*)`)?", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!m.Success || !m.Groups["pack"].Success)
            {
                return (null, null);
            }

            var path = m.Groups["path"].Success ? m.Groups["path"].Value.TrimEnd('/') : null;
            return (m.Groups["pack"].Value, path is { Length: 0 } ? StackPaths.Root : path);
        }

        var fe = Row("Frontend");
        var be = Row("Backend");
        var data = Row("Data");
        var vcs = Row("VCS / CI");
        var feHost = Row("Frontend host");
        var beHost = Row("Backend host");
        var local = Row("Local dev loop");
        var agents = SpecialistPattern().Matches(system).Select(m => m.Groups["key"].Value).Distinct(StringComparer.Ordinal).ToList();
        var changed = new List<string>();
        foreach (var line in system.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith(StackGuidance.LayersChangedPrefix, StringComparison.Ordinal)))
        {
            changed.AddRange(LayerPattern().Matches(line).Select(m => m.Groups["layer"].Value));
        }

        var contracts = new List<string>();
        var index = system.IndexOf(StackGuidance.ContractFilesHeading, StringComparison.Ordinal);
        if (index >= 0)
        {
            foreach (var line in system[index..].ReplaceLineEndings("\n").Split('\n').Skip(1))
            {
                if (line.StartsWith('#'))
                {
                    break;
                }

                if (ContractPattern().Match(line) is { Success: true } c)
                {
                    contracts.Add(c.Groups["path"].Value);
                }
            }
        }

        return new FakeComposition(
            fe.Pack, fe.Pack is null ? null : fe.Path ?? "frontend",
            be.Pack, be.Pack is null ? null : be.Path ?? "backend",
            data.Pack, vcs.Pack ?? "vcs-github", feHost.Pack, beHost.Pack, local.Pack is not null,
            agents, changed, contracts);
    }

    public bool Has(string agent) => Agents.Contains(agent, StringComparer.Ordinal);

    [GeneratedRegex(@"^## Stack composition\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^- `(?<key>[a-z]+(?:-[a-z]+)*)` — ", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpecialistPattern();

    [GeneratedRegex(@"(?<layer>[a-z]+) → `", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LayerPattern();

    [GeneratedRegex(@"^- `(?<path>[^`]+)`\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContractPattern();
}

/// <summary>
/// The Fake provider's scripted composition outputs (D13, §8): a plan valid for the composition's specialists and composed
/// rules (scaffolder first on an empty repository, migrations before endpoints, the contract file with the endpoints,
/// CI and platform files by their specialists, a step per changed layer), and the files a specialist step writes (health
/// endpoints, routes naming the spec's endpoints, a non-root Dockerfile, platform configs that stop AIDE-branch builds,
/// Supabase migrations with RLS and grants), so the composed deterministic review checks pass.
/// </summary>
public static class FakeStackScripts
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The plan steps (as the plan generator's JSON objects) for <paramref name="c"/>.</summary>
    public static IReadOnlyList<object> Plan(FakeComposition c, string resource, string resources, IReadOnlyList<string> scope, bool repoHasCode)
    {
        ArgumentNullException.ThrowIfNull(c);
        var r = resource;
        var rs = resources;
        var Rs = Capitalise(rs);
        string[] all = scope.Count > 0 ? [.. scope] : ["FR-1", "FR-2", "FR-3", "FR-4"];
        string[] model = scope.Count > 0 ? [] : ["FR-1", "FR-4"];
        var suffix = repoHasCode && scope.Count > 0 ? "-" + scope[0].ToLowerInvariant() : string.Empty;
        var tag = suffix.Replace("-", string.Empty, StringComparison.Ordinal);
        var app = c.BackendPath ?? c.FrontendPath ?? StackPaths.Root;
        var steps = new List<(string Agent, string Title, string Task, string[] Covers, (string Path, string Action)[] Files)>();
        var fresh = !repoHasCode;
        bool Changed(string layer) => c.LayersChanged.Contains(layer, StringComparer.Ordinal);
        var action = fresh ? "create" : "modify";

        if (fresh && c.Has(StackAgents.Scaffolder))
        {
            var files = new List<(string, string)> { (".editorconfig", "create") };
            if (c.FrontendPack is not null)
            {
                files.Add((P(c.FrontendPath!, "package.json"), "create"));
            }

            if (c.BackendPack is { } bePack && BackendProjectFile(bePack) is { } project && !files.Contains((P(c.BackendPath!, project), "create")))
            {
                files.Add((P(c.BackendPath!, project), "create"));
            }

            steps.Add((StackAgents.Scaffolder, "Project skeleton", "Scaffold every layer of the composition: project files, configuration and folder layout.", [], [.. files]));
        }

        if (c.DataPack is { } dataPack && c.Has(StackAgents.DataEngineer) && DataFiles(dataPack, c, app, r, rs, tag, fresh) is { Length: > 0 } dataFiles)
        {
            steps.Add((StackAgents.DataEngineer, $"{Capitalise(rs)} schema and migration", $"Model the {r} entity and add one migration for its table (FR-1, FR-4).", model, dataFiles));
        }

        if (c.BackendPack is { } backend && c.Has(StackAgents.BackendEngineer))
        {
            var files = BackendFiles(backend, c.BackendPath!, r, rs, suffix, fresh).ToList();
            files.AddRange(c.ContractFiles.Where(f => !f.Contains('*', StringComparison.Ordinal)).Select(f => (f, action)));
            steps.Add((StackAgents.BackendEngineer, $"{Rs} API", $"Add the /api/{rs} endpoints (FR-1–FR-4) with validation, problem-details errors and the health endpoints, and update the contract file.", all, [.. files]));
        }

        if (c.FrontendPack is { } frontend && c.Has(StackAgents.FrontendEngineer))
        {
            var files = FrontendFiles(frontend, c.FrontendPath!, r, rs, suffix).ToList();

            // FR-120: Next.js full-stack has no backend layer, so its route handlers carry the spec's endpoints.
            if (frontend == "fe-nextjs" && c.BackendPack is null)
            {
                files.Add((P(c.FrontendPath!, $"src/app/api/{rs}{suffix}/route.ts"), "create"));
            }

            if (c.BackendPack is not null && c.ContractFiles.Count > 0 && frontend is "fe-react-vite" or "fe-vue" or "fe-vanilla" or "fe-nextjs")
            {
                files.Add((P(c.FrontendPath!, "src/api/generated/schema.d.ts"), action));
            }

            steps.Add((StackAgents.FrontendEngineer, $"{Rs} pages", $"Build the {rs} list page with loading, empty and error states (FR-2).", c.BackendPack is null ? all : scope.Count > 0 ? [] : ["FR-2"], [.. files]));
        }

        if (c.Has(StackAgents.TestEngineer))
        {
            steps.Add((StackAgents.TestEngineer, "End-to-end tests", $"Cover creating, listing and validating {rs} end to end (FR-1–FR-4).", [], [($"e2e/{rs}{suffix}.spec.ts", "create")]));
        }

        if (c.Has(StackAgents.CiEngineer) && (fresh || Changed(StackLayers.Vcs)))
        {
            steps.Add((StackAgents.CiEngineer, "CI pipeline", "Compose the CI pipeline from the packs' gates and the generated files' freshness checks.", [], [(CiFile(c.VcsPack), action)]));
        }

        if (c.Has(StackAgents.DeployEngineer) && (fresh || Changed(StackLayers.Deploy)))
        {
            var files = new List<(string, string)>();
            if (c.BackendPath is { } be)
            {
                files.Add((P(be, "Dockerfile"), action));
            }

            if (c.ComposeLoop)
            {
                files.Add(("compose.yaml", action));
            }

            foreach (var (host, root) in new[] { (c.FrontendHost, c.FrontendPath), (c.BackendHost, c.BackendPath) })
            {
                var config = host switch
                {
                    "deploy-vercel" => P(root ?? StackPaths.Root, "vercel.json"),
                    "deploy-netlify" => P(root ?? StackPaths.Root, "netlify.toml"),
                    "deploy-render" => "render.yaml",
                    _ => null,
                };
                if (config is not null && !files.Any(f => f.Item1 == config))
                {
                    files.Add((config, action));
                }
            }

            files.Add((".env.example", action));
            steps.Add((StackAgents.DeployEngineer, "Containers, platforms and environment", "Write the container, platform and local dev-loop configuration and the environment variable inventory.", [], [.. files]));
        }

        if (c.Has(StackAgents.DocsWriter))
        {
            steps.Add((StackAgents.DocsWriter, "Documentation", $"Document the {rs} API and the stack in the README and docs.", [], fresh ? [("README.md", "create"), ($"docs/{rs}.md", "create")] : [($"docs/{rs}{suffix}.md", "create")]));
        }

        return
        [
            .. steps.Select((s, i) => (object)new
            {
                number = i + 1,
                title = s.Title,
                task = s.Task,
                files = s.Files.Select(f => new { path = f.Path, action = f.Action }),
                dependencies = i == 0 ? Array.Empty<int>() : [i],
                covers = s.Covers,
                agent = s.Agent,
                userInstructions = (string?)null,
            }),
        ];
    }

    /// <summary>The content a composed specialist writes for <paramref name="path"/>, or <c>null</c> to use the generic Fake content.</summary>
    public static string? Content(string specialist, string path, string project, int step, string title)
    {
        ArgumentNullException.ThrowIfNull(path);
        var fileName = path.Split('/')[^1];
        var header = $"{title}: written by the Fake {specialist} in step {step} of the {project} plan.";
        var segments = path.Split('/');
        if (fileName.Equals("Dockerfile", StringComparison.Ordinal))
        {
            return $"# {header}\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /app\nCOPY . .\nUSER $APP_UID\nEXPOSE 8080\n";
        }

        switch (fileName)
        {
            case "compose.yaml":
                return $"# {header}\nservices:\n  db:\n    image: postgres:17\n    env_file: .env\n";
            case "vercel.json":
                return "{\n  \"git\": {\n    \"deploymentEnabled\": {\n      \"aide/**\": false\n    }\n  }\n}\n";
            case "netlify.toml":
                return $"# {header}\n[build]\n  ignore = 'case \"$BRANCH\" in aide/*) exit 0 ;; *) exit 1 ;; esac'\n";
            case ".env.example":
                return "# Environment variable inventory (no values are ever committed).\nDATABASE_URL=\n";
            case "api.json" when path.EndsWith("openapi/api.json", StringComparison.Ordinal):
                return JsonSerializer.Serialize(new { openapi = "3.1.0", info = new { title = project, version = "1.0.0" }, paths = new Dictionary<string, object>() }, Indented) + "\n";
            case "Program.cs" or "Application.java" or "main.py" or "server.ts" or "main.ts":
                var (live, ready) = StackHealth.Paths(fileName == "Application.java" ? "be-spring-boot" : "be-aspnet");
                return $"// {header}\n// Health: {live}, {ready}\n";
            default:
                break;
        }

        // A Next.js route handler is named by its folder (src/app/api/recipes/route.ts).
        if (fileName.Equals("route.ts", StringComparison.Ordinal) && segments.Length >= 3 && segments[^3] == "api")
        {
            var resources = ResourceOf(segments[^2]);
            return $"// {header}\n// Routes: GET/POST /api/{resources}, GET/PUT/DELETE /api/{resources}/:id\n";
        }

        // Endpoint files name the spec's endpoints, so the review's endpoint check finds them.
        var endpointFile = fileName.EndsWith("Endpoints.cs", StringComparison.Ordinal) || fileName.EndsWith("Controller.java", StringComparison.Ordinal)
            || fileName.EndsWith(".controller.ts", StringComparison.Ordinal) || (segments.Length >= 2 && segments[^2] is "routers" or "routes");
        if (endpointFile)
        {
            var resources = ResourceOf(fileName);
            return $"// {header}\n// Routes: GET/POST /api/{resources}, GET/PUT/DELETE /api/{resources}/:id\n";
        }

        if (path.StartsWith("supabase/migrations/", StringComparison.Ordinal) && fileName.EndsWith(".sql", StringComparison.Ordinal))
        {
            var table = new string([.. Path.GetFileNameWithoutExtension(fileName).SkipWhile(ch => char.IsAsciiDigit(ch) || ch == '_').Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')]);
            table = table.Length == 0 ? "items" : table;
            return $"-- {header}\ncreate table if not exists public.{table} (\n  id uuid primary key default gen_random_uuid(),\n  owner_id uuid not null references auth.users (id) on delete cascade,\n  title text not null,\n  created_at timestamptz not null default now()\n);\n\nalter table public.{table} enable row level security;\n\ncreate policy \"{table}_owner_all\" on public.{table}\n  for all using (owner_id = auth.uid()) with check (owner_id = auth.uid());\n\ngrant select, insert, update, delete on public.{table} to authenticated;\n";
        }

        return null;
    }

    private static string ResourceOf(string fileName)
    {
        var stem = fileName.Split('.')[0];
        foreach (var end in new[] { "Endpoints", "Controller" })
        {
            if (stem.EndsWith(end, StringComparison.Ordinal))
            {
                stem = stem[..^end.Length];
            }
        }

        var cut = stem.IndexOf('-', StringComparison.Ordinal);
        stem = cut > 0 ? stem[..cut] : stem;
        return stem.ToLowerInvariant();
    }

    private static string? BackendProjectFile(string pack) => pack switch
    {
        "be-aspnet" => "App.slnx",
        "be-spring-boot" => "pom.xml",
        "be-fastapi" => "pyproject.toml",
        "be-express" or "be-nestjs" => "package.json",
        _ => null,
    };

    private static (string, string)[] DataFiles(string pack, FakeComposition c, string app, string r, string rs, string tag, bool fresh)
    {
        var be = c.BackendPath ?? app;
        var stamp = fresh ? "20260101000000" : "20260201000000";
        var name = fresh ? "Initial" : $"Add{Capitalise(rs)}{Capitalise(tag)}";
        var modify = fresh ? "create" : "modify";
        return pack switch
        {
            "data-efcore-postgres" or "data-efcore-sqlserver" =>
                [(P(be, "src/App.Infrastructure/Persistence/AppDbContext.cs"), modify), (P(be, $"src/App.Infrastructure/Migrations/{stamp}_{name}.cs"), "create")],
            "data-jpa-flyway" => [(P(be, $"src/main/resources/db/migration/V{(fresh ? 1 : 2)}__{rs}{tag}.sql"), "create")],
            "data-sqlalchemy" => [(P(be, $"app/models/{r}{tag}.py"), "create"), (P(be, $"alembic/versions/{stamp}_{rs}{tag}.py"), "create")],
            "data-prisma" => [(P(app, "prisma/schema.prisma"), modify), (P(app, $"prisma/migrations/{stamp}_{rs}{tag}/migration.sql"), "create")],
            "data-drizzle" => [(P(app, $"src/db/{rs}{tag}.ts"), "create"), (P(app, $"drizzle/{stamp}_{rs}{tag}.sql"), "create")],
            "data-supabase" => [($"supabase/migrations/{stamp}_{rs}{tag}.sql", "create")],
            _ => [],
        };
    }

    private static IEnumerable<(string, string)> BackendFiles(string pack, string be, string r, string rs, string suffix, bool fresh)
    {
        var Rs = Capitalise(rs);
        var entry = pack switch
        {
            "be-aspnet" => "src/App.Api/Program.cs",
            "be-spring-boot" => "src/main/java/app/Application.java",
            "be-fastapi" => "app/main.py",
            "be-express" => "src/server.ts",
            _ => "src/main.ts",
        };
        if (fresh)
        {
            yield return (P(be, entry), "create");
        }

        yield return pack switch
        {
            "be-aspnet" => (P(be, $"src/App.Api/Features/{Rs}/{Rs}{suffix}Endpoints.cs"), "create"),
            "be-spring-boot" => (P(be, $"src/main/java/app/{Capitalise(r)}{suffix.Replace("-", string.Empty, StringComparison.Ordinal)}Controller.java"), "create"),
            "be-fastapi" => (P(be, $"app/routers/{rs}{suffix.Replace('-', '_')}.py"), "create"),
            "be-express" => (P(be, $"src/routes/{rs}{suffix}.ts"), "create"),
            _ => (P(be, $"src/{rs}/{rs}{suffix}.controller.ts"), "create"),
        };
    }

    private static IEnumerable<(string, string)> FrontendFiles(string pack, string fe, string r, string rs, string suffix)
    {
        var Rs = Capitalise(rs);
        yield return pack switch
        {
            "fe-nextjs" => (P(fe, $"src/app/{rs}{suffix}/page.tsx"), "create"),
            "fe-angular" => (P(fe, $"src/app/{rs}{suffix}/{rs}.component.ts"), "create"),
            "fe-vue" => (P(fe, $"src/views/{Rs}{suffix}View.vue"), "create"),
            "fe-vanilla" => (P(fe, $"src/{rs}{suffix}.ts"), "create"),
            _ => (P(fe, $"src/features/{rs}{suffix}/{Rs}Page.tsx"), "create"),
        };
        _ = r;
    }

    private static string CiFile(string vcsPack) => vcsPack switch
    {
        "vcs-gitlab" => ".gitlab-ci.yml",
        "vcs-bitbucket" => "bitbucket-pipelines.yml",
        _ => ".github/workflows/ci.yml",
    };

    private static string P(string root, string relative) => root == StackPaths.Root ? relative : $"{root}/{relative}";

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
