using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-118: what the stack detector suggests for an imported repo. A choice is either suggested (one match), in
/// <see cref="Conflicts"/> (matches that disagree: the choice is left empty and every candidate is shown), or absent
/// (nothing detected: left empty for the user).
/// </summary>
public sealed record StackDetection(IReadOnlyList<StackDetectedChoice> Suggestions, IReadOnlyList<StackDetectionConflict> Conflicts)
{
    /// <summary>The suggestion for <paramref name="choice"/> (one of <see cref="StackChoices"/>), or <c>null</c>.</summary>
    public StackDetectedChoice? Suggestion(string choice) => Suggestions.FirstOrDefault(s => s.Choice == choice);

    /// <summary>The conflict on <paramref name="choice"/>, or <c>null</c>.</summary>
    public StackDetectionConflict? Conflict(string choice) => Conflicts.FirstOrDefault(c => c.Choice == choice);
}

/// <summary>
/// A detected choice: the option id, the layer path for the frontend and backend (FR-119: the folder holding the manifest,
/// <c>.</c> for the root), the data mode (Supabase: <c>baas</c>) and the evidence (e.g. "`next` 16.1 in `package.json`").
/// </summary>
public sealed record StackDetectedChoice(string Choice, string Option, string? Path, string? Mode, IReadOnlyList<string> Evidence);

/// <summary>A choice whose matches conflict: it is left empty, and every candidate is shown with its evidence.</summary>
public sealed record StackDetectionConflict(string Choice, IReadOnlyList<StackDetectionCandidate> Candidates);

/// <summary>One of the conflicting matches of a choice.</summary>
public sealed record StackDetectionCandidate(string Option, string? Path, IReadOnlyList<string> Evidence);

/// <summary>
/// FR-118, §4.18 "Detection": the pure stack detector, over a repo's file list (relative paths, <c>/</c> separators) and
/// the contents of the manifests it asks for. Use it in two steps, so the caller can fetch files asynchronously:
/// <see cref="ManifestsToRead"/> lists the manifests to fetch, then <see cref="Detect"/> runs the rules. No I/O.
/// <para>
/// Manifests are looked for in the repo root and up to <see cref="MaxManifestDepth"/> folders down (<c>frontend/</c>,
/// <c>apps/web/</c>); .NET projects one more (<c>backend/src/Api/Api.csproj</c>). Folders in
/// <see cref="IgnoredFolders"/> are skipped, and at most <see cref="MaxManifests"/> manifests are read.
/// </para>
/// </summary>
public static partial class StackDetector
{
    /// <summary>The most manifests read, shallowest first, so huge repos stay fast.</summary>
    public const int MaxManifests = 50;

    /// <summary>The deepest folder (in segments below the root) whose manifests are read.</summary>
    public const int MaxManifestDepth = 2;

    /// <summary>Manifests longer than this (characters) are skipped.</summary>
    public const int MaxManifestLength = 512 * 1024;

    /// <summary>The most evidence strings kept per candidate.</summary>
    public const int MaxEvidence = 5;

    /// <summary>Folders never looked into (dependencies, build output, VCS internals).</summary>
    public static IReadOnlyList<string> IgnoredFolders { get; } =
        ["node_modules", "bin", "obj", "dist", ".git", ".next", "build", "out", "target", "vendor", ".venv", "venv", "__pycache__", "coverage"];

    /// <summary>The repo hosts <see cref="Detect"/> accepts (the <see cref="StackFacts.VcsHost"/> values).</summary>
    public static IReadOnlyList<string> VcsHosts { get; } = ["github", "gitlab", "gitlab-self-managed", "bitbucket"];

    /// <summary>The evidence of a legacy Railway config file.</summary>
    public const string LegacyRailwayEvidence = "legacy config, deprecated by Railway";

    // Folders whose manifests describe tests or samples, not the app.
    private static readonly HashSet<string> NonAppFolders =
        new(["test", "tests", "e2e", "__tests__", "examples", "example", "samples", "fixtures"], StringComparer.OrdinalIgnoreCase);

    // Folders whose index.html is not a site of its own (served assets, docs, templates).
    private static readonly HashSet<string> NonSiteFolders =
        new(["public", "static", "wwwroot", "templates", "docs", "assets"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Ignored = new(IgnoredFolders, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] FrameworkDependencies = ["next", "react", "react-dom", "vue", "@angular/core", "svelte", "preact", "solid-js"];

    private static readonly string[] BackendCapableHosts = ["deploy-vercel", "deploy-render", "deploy-railway"];

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// The manifests <see cref="Detect"/> reads (normalised paths, shallowest first, at most <see cref="MaxManifests"/>):
    /// <c>package.json</c>, <c>*.csproj</c>, <c>pom.xml</c>, <c>build.gradle*</c>, <c>pyproject.toml</c> and
    /// <c>requirements*.txt</c>. Other evidence (<c>angular.json</c>, <c>supabase/config.toml</c>, host files) is the file's
    /// presence.
    /// </summary>
    public static IReadOnlyList<string> ManifestsToRead(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return SelectManifests(Normalise(files));
    }

    /// <summary>
    /// Runs the §4.18 detection rules. <paramref name="manifests"/> holds the contents of (some of) the paths from
    /// <see cref="ManifestsToRead"/>; a missing, oversized or unparsable manifest is skipped (detection never blocks).
    /// <paramref name="vcsHost"/> is one of <see cref="VcsHosts"/>: the VCS layer is fixed to it.
    /// </summary>
    public static StackDetection Detect(IEnumerable<string> files, IReadOnlyDictionary<string, string> manifests, string vcsHost)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(vcsHost);
        var vcs = VcsSuggestion(vcsHost);

        var repo = new Repo(Normalise(files));
        var folders = ReadFolders(repo, manifests);

        var frontend = DetectFrontends(repo, folders);
        var backend = DetectBackends(repo, folders);
        var data = DetectData(folders);
        var databaseHost = new List<Hit>();
        DetectSupabase(repo, data, databaseHost);
        var (frontendHost, backendHost) = DetectHosts(repo, frontend, backend);

        var suggestions = new List<StackDetectedChoice>();
        var conflicts = new List<StackDetectionConflict>();

        Resolve(StackChoices.Frontend, frontend, suggestions, conflicts);
        Resolve(StackChoices.Backend, backend, suggestions, conflicts);
        if (frontend.Count > 0 && backend.Count == 0)
        {
            suggestions.Add(new StackDetectedChoice(StackChoices.Backend, StackIds.None, null, null, ["no backend manifest found"]));
        }
        else if (backend.Count > 0 && frontend.Count == 0)
        {
            suggestions.Add(new StackDetectedChoice(StackChoices.Frontend, StackIds.None, null, null, ["no frontend manifest found"]));
        }

        Resolve(StackChoices.Data, data, suggestions, conflicts);
        suggestions.Add(vcs);
        Resolve(StackChoices.FrontendHost, frontendHost, suggestions, conflicts);
        Resolve(StackChoices.BackendHost, backendHost, suggestions, conflicts);
        Resolve(StackChoices.DatabaseHost, databaseHost, suggestions, conflicts);

        return new StackDetection(
            [.. suggestions.OrderBy(s => StackChoices.IndexOf(s.Choice))],
            [.. conflicts.OrderBy(c => StackChoices.IndexOf(c.Choice))]);
    }

    // ── VCS ──────────────────────────────────────────────────────────────

    private static StackDetectedChoice VcsSuggestion(string vcsHost)
    {
        var (option, name) = vcsHost switch
        {
            "github" => ("vcs-github", "GitHub"),
            "gitlab" => ("vcs-gitlab", "GitLab"),
            "gitlab-self-managed" => ("vcs-gitlab", "self-managed GitLab"),
            "bitbucket" => ("vcs-bitbucket", "Bitbucket"),
            _ => throw new ArgumentOutOfRangeException(nameof(vcsHost), vcsHost, "Unknown repo host."),
        };
        return new StackDetectedChoice(StackChoices.Vcs, option, null, null, [$"the repo is on {name}"]);
    }

    // ── Frontend ─────────────────────────────────────────────────────────

    private static List<Hit> DetectFrontends(Repo repo, IReadOnlyList<Folder> folders)
    {
        var hits = new List<Hit>();
        var nextConfigDirs = repo.Files
            .Where(f => f.Depth <= MaxManifestDepth && NextConfig().IsMatch(f.Name) && !InNonAppFolder(f.Dir))
            .GroupBy(f => f.Dir, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var path = PathOf(folder.Dir);
            var npm = folder.NpmDependencies;
            var evidence = new List<string>();
            if (npm.TryGetValue("next", out var next))
            {
                evidence.Add(Evidence("next", next.Version, next.File));
            }

            if (nextConfigDirs.TryGetValue(folder.Dir, out var config))
            {
                evidence.Add($"`{config.Path}`");
            }

            if (evidence.Count > 0)
            {
                hits.Add(new Hit("fe-nextjs", path, evidence));
                continue;
            }

            var angularJson = Join(folder.Dir, "angular.json");
            if (npm.TryGetValue("@angular/core", out var angular) && repo.HasFile(angularJson))
            {
                hits.Add(new Hit("fe-angular", path, [Evidence("@angular/core", angular.Version, angular.File), $"`{angularJson}`"]));
            }

            if (npm.TryGetValue("vite", out var vite))
            {
                if (npm.TryGetValue("vue", out var vue))
                {
                    hits.Add(new Hit("fe-vue", path, [Evidence("vue", vue.Version, vue.File), Evidence("vite", vite.Version, vite.File)]));
                }

                if (npm.TryGetValue("react", out var react))
                {
                    hits.Add(new Hit("fe-react-vite", path, [Evidence("react", react.Version, react.File), Evidence("vite", vite.Version, vite.File)]));
                }
            }
        }

        // A Next.js config without a package.json read (e.g. past the manifest cap).
        foreach (var (dir, config) in nextConfigDirs)
        {
            if (!folders.Any(f => f.Dir == dir))
            {
                hits.Add(new Hit("fe-nextjs", PathOf(dir), [$"`{config.Path}`"]));
            }
        }

        if (hits.Count > 0)
        {
            return hits;
        }

        // fe-vanilla: an index.html (root or one folder down) with no framework dependency next to it.
        foreach (var index in repo.Files.Where(f => f.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase) && f.Depth <= 1))
        {
            if (index.Depth == 1 && (NonSiteFolders.Contains(index.Dir) || NonAppFolders.Contains(index.Dir)))
            {
                continue;
            }

            var folder = folders.FirstOrDefault(f => f.Dir == index.Dir);
            if (folder is not null && FrameworkDependencies.Any(folder.NpmDependencies.ContainsKey))
            {
                continue;
            }

            hits.Add(new Hit("fe-vanilla", PathOf(index.Dir), [$"`{index.Path}` with no framework dependency"]));
        }

        return hits;
    }

    // ── Backend ──────────────────────────────────────────────────────────

    private static List<Hit> DetectBackends(Repo repo, IReadOnlyList<Folder> folders)
    {
        var hits = new List<Hit>();
        foreach (var folder in folders)
        {
            var path = PathOf(folder.Dir);
            var npm = folder.NpmDependencies;
            if (npm.TryGetValue("@nestjs/core", out var nest))
            {
                hits.Add(new Hit("be-nestjs", path, [Evidence("@nestjs/core", nest.Version, nest.File)]));
            }
            else if (npm.TryGetValue("express", out var express))
            {
                hits.Add(new Hit("be-express", path, [Evidence("express", express.Version, express.File)]));
            }

            if (folder.PythonDependencies.TryGetValue("fastapi", out var fastapi))
            {
                hits.Add(new Hit("be-fastapi", path, [Evidence("fastapi", fastapi.Version, fastapi.File)]));
            }

            foreach (var (file, text) in folder.Java)
            {
                var web = SpringWeb().Match(text);
                if (web.Success)
                {
                    hits.Add(new Hit("be-spring-boot", path, [$"`{web.Value}` in `{file}`"]));
                }
            }

            foreach (var project in folder.Projects)
            {
                var evidence = new List<string>();
                if (project.WebSdk)
                {
                    evidence.Add($"`Microsoft.NET.Sdk.Web` in `{project.File}`");
                }

                var aspnet = project.References.FirstOrDefault(r => r.Name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
                if (aspnet is not null)
                {
                    evidence.Add(Evidence(aspnet.Name, aspnet.Version, project.File));
                }

                if (evidence.Count > 0)
                {
                    hits.Add(new Hit("be-aspnet", DotnetRoot(repo, folder.Dir), evidence));
                }
            }
        }

        return hits;
    }

    /// <summary>A .NET backend's path: its solution folder (<c>backend/</c> for <c>backend/src/Api/Api.csproj</c>).</summary>
    private static string DotnetRoot(Repo repo, string projectDir)
    {
        if (repo.HasSolution(projectDir))
        {
            return PathOf(projectDir);
        }

        var parent = Parent(projectDir);
        if (parent is not null && Name(parent).Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            return PathOf(Parent(parent) ?? string.Empty);
        }

        for (var dir = parent; dir is not null; dir = Parent(dir))
        {
            if (repo.HasSolution(dir))
            {
                return PathOf(dir);
            }
        }

        return PathOf(projectDir);
    }

    // ── Data ─────────────────────────────────────────────────────────────

    private static List<Hit> DetectData(IReadOnlyList<Folder> folders)
    {
        var hits = new List<Hit>();
        foreach (var folder in folders)
        {
            foreach (var project in folder.Projects)
            {
                foreach (var (name, option) in new[]
                         {
                             ("Npgsql.EntityFrameworkCore.PostgreSQL", "data-efcore-postgres"),
                             ("Microsoft.EntityFrameworkCore.SqlServer", "data-efcore-sqlserver"),
                         })
                {
                    var reference = project.References.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (reference is not null)
                    {
                        hits.Add(new Hit(option, null, [Evidence(reference.Name, reference.Version, project.File)]));
                    }
                }
            }

            foreach (var (file, text) in folder.Java)
            {
                var flyway = Flyway().Match(text);
                var jpa = Jpa().Match(text);
                if (flyway.Success && jpa.Success)
                {
                    hits.Add(new Hit("data-jpa-flyway", null, [$"`{flyway.Value}` in `{file}`", $"`{jpa.Value}` in `{file}`"]));
                }
            }

            var python = folder.PythonDependencies;
            if (python.TryGetValue("sqlalchemy", out var sqlalchemy))
            {
                var alembic = python.TryGetValue("alembic", out var dep) ? Evidence("alembic", dep.Version, dep.File) : folder.AlembicEvidence;
                if (alembic is not null)
                {
                    hits.Add(new Hit("data-sqlalchemy", null, [alembic, Evidence("sqlalchemy", sqlalchemy.Version, sqlalchemy.File)]));
                }
            }

            var npm = folder.NpmDependencies;
            foreach (var name in new[] { "prisma", "@prisma/client" })
            {
                if (npm.TryGetValue(name, out var prisma))
                {
                    hits.Add(new Hit("data-prisma", null, [Evidence(name, prisma.Version, prisma.File)]));
                }
            }

            if (npm.TryGetValue("drizzle-orm", out var drizzle))
            {
                hits.Add(new Hit("data-drizzle", null, [Evidence("drizzle-orm", drizzle.Version, drizzle.File)]));
            }
        }

        return hits;
    }

    /// <summary>
    /// <c>supabase/config.toml</c>: with no other data pack, Data is Supabase (BaaS) and so is its host (R8). With another
    /// data pack, Supabase is that pack's database host (host mode).
    /// </summary>
    private static void DetectSupabase(Repo repo, List<Hit> data, List<Hit> databaseHost)
    {
        var config = repo.Files.FirstOrDefault(f =>
            f.Name.Equals("config.toml", StringComparison.OrdinalIgnoreCase)
            && Name(f.Dir).Equals("supabase", StringComparison.OrdinalIgnoreCase)
            && f.Depth <= MaxManifestDepth + 1);
        if (config is null)
        {
            return;
        }

        var evidence = new List<string> { $"`{config.Path}`" };
        var migrations = Join(config.Dir, "migrations");
        if (repo.HasFolder(migrations))
        {
            evidence.Add($"`{migrations}/`");
        }

        if (data.Count == 0)
        {
            data.Add(new Hit(StackIds.Supabase, null, evidence, StackIds.BaasMode));
            databaseHost.Add(new Hit(StackIds.SupabaseHost, null, [.. evidence, "Supabase (BaaS) is its own database host"]));
        }
        else
        {
            databaseHost.Add(new Hit(StackIds.SupabaseHost, null, [.. evidence, "with another data pack: Supabase in host mode"]));
        }
    }

    // ── Hosts ────────────────────────────────────────────────────────────

    private static (List<Hit> Frontend, List<Hit> Backend) DetectHosts(Repo repo, List<Hit> frontends, List<Hit> backends)
    {
        var found = new List<(string Option, string Dir, string Evidence)>();
        foreach (var file in repo.Files)
        {
            if (file.Depth == 0 && file.Name == "render.yaml")
            {
                found.Add(("deploy-render", file.Dir, $"`{file.Path}`"));
            }
            else if (file.Path == ".github/workflows/pages.yml")
            {
                found.Add(("deploy-github-pages", string.Empty, $"`{file.Path}`"));
            }
            else if (file.Path.StartsWith(".railway/", StringComparison.Ordinal))
            {
                if (!found.Any(h => h.Evidence == "`.railway/`"))
                {
                    found.Add(("deploy-railway", string.Empty, "`.railway/`"));
                }
            }
            else if (file.Depth <= MaxManifestDepth && !InNonAppFolder(file.Dir))
            {
                var option = file.Name switch
                {
                    "vercel.json" => "deploy-vercel",
                    "netlify.toml" => "deploy-netlify",
                    "wrangler.jsonc" => "deploy-cloudflare",
                    "railway.json" or "railway.toml" => "deploy-railway",
                    _ => null,
                };
                if (option is not null)
                {
                    var legacy = file.Name.StartsWith("railway.", StringComparison.Ordinal);
                    found.Add((option, file.Dir, legacy ? $"`{file.Path}` ({LegacyRailwayEvidence})" : $"`{file.Path}`"));
                }
            }
        }

        var fePath = frontends.Select(h => h.Path).Distinct().Count() == 1 ? frontends[0].Path : null;
        var bePath = backends.Select(h => h.Path).Distinct().Count() == 1 ? backends[0].Path : null;
        bool Under(string dir, string? path) => path is not null && path != "." && (dir == path || dir.StartsWith(path + "/", StringComparison.Ordinal));

        var frontendHosts = new List<(Hit Hit, bool InFolder)>();
        var backendHosts = new List<(Hit Hit, bool InFolder)>();
        foreach (var (option, dir, evidence) in found)
        {
            var inFe = Under(dir, fePath);
            var inBe = Under(dir, bePath);
            if (frontends.Count > 0 && !(inBe && !inFe))
            {
                frontendHosts.Add((new Hit(option, null, [evidence]), inFe));
            }

            if (backends.Count > 0 && BackendCapableHosts.Contains(option) && !(inFe && !inBe))
            {
                backendHosts.Add((new Hit(option, null, [evidence]), inBe));
            }
        }

        // With a backend, a root file of a platform that can run backends (render.yaml, .railway/) is the backend's
        // when the frontend has a host of its own.
        if (backends.Count > 0 && frontendHosts.Select(h => h.Hit.Option).Distinct().Count() > 1)
        {
            var own = frontendHosts
                .GroupBy(h => h.Hit.Option)
                .Where(g => !BackendCapableHosts.Contains(g.Key) || g.Any(h => h.InFolder))
                .SelectMany(g => g)
                .ToList();
            if (own.Count > 0)
            {
                frontendHosts = own;
            }
        }

        return ([.. frontendHosts.Select(h => h.Hit)], [.. backendHosts.Select(h => h.Hit)]);
    }

    // ── Resolution ───────────────────────────────────────────────────────

    /// <summary>Merges hits by option and path; one candidate is the suggestion, several are a conflict.</summary>
    private static void Resolve(string choice, List<Hit> hits, List<StackDetectedChoice> suggestions, List<StackDetectionConflict> conflicts)
    {
        var candidates = hits
            .GroupBy(h => (h.Option, h.Path))
            .Select(g => (g.Key.Option, g.Key.Path, g.First().Mode,
                Evidence: (IReadOnlyList<string>)[.. g.SelectMany(h => h.Evidence).Distinct(StringComparer.Ordinal).Take(MaxEvidence)]))
            .ToList();
        if (candidates.Count == 1)
        {
            var only = candidates[0];
            suggestions.Add(new StackDetectedChoice(choice, only.Option, only.Path, only.Mode, only.Evidence));
        }
        else if (candidates.Count > 1)
        {
            conflicts.Add(new StackDetectionConflict(choice, [.. candidates.Select(c => new StackDetectionCandidate(c.Option, c.Path, c.Evidence))]));
        }
    }

    // ── Manifests ────────────────────────────────────────────────────────

    private static List<string> SelectManifests(IReadOnlyList<RepoFile> files) =>
        [.. files
            .Where(IsManifest)
            .OrderBy(f => f.Depth)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(MaxManifests)
            .Select(f => f.Path)];

    private static bool IsManifest(RepoFile file)
    {
        if (InNonAppFolder(file.Dir))
        {
            return false;
        }

        var name = file.Name;
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return file.Depth <= MaxManifestDepth + 1
                && !name.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("Test.csproj", StringComparison.OrdinalIgnoreCase);
        }

        return file.Depth <= MaxManifestDepth
            && (name is "package.json" or "pom.xml" or "build.gradle" or "build.gradle.kts" or "pyproject.toml"
                || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<Folder> ReadFolders(Repo repo, IReadOnlyDictionary<string, string> manifests)
    {
        var folders = new Dictionary<string, Folder>(StringComparer.Ordinal);
        foreach (var path in SelectManifests(repo.Files))
        {
            if (!manifests.TryGetValue(path, out var text) || text is null || text.Length > MaxManifestLength)
            {
                continue;
            }

            var file = new RepoFile(path);
            if (!folders.TryGetValue(file.Dir, out var folder))
            {
                var alembicIni = Join(file.Dir, "alembic.ini");
                var alembicDir = Join(file.Dir, "alembic");
                folder = new Folder(file.Dir, repo.HasFile(alembicIni) ? $"`{alembicIni}`" : repo.HasFolder(alembicDir) ? $"`{alembicDir}/`" : null);
                folders.Add(file.Dir, folder);
            }

            if (file.Name == "package.json")
            {
                ReadPackageJson(path, text, folder.NpmDependencies);
            }
            else if (file.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                folder.Projects.Add(ReadProject(path, text));
            }
            else if (file.Name is "pom.xml" or "build.gradle" or "build.gradle.kts")
            {
                folder.Java.Add((path, text));
            }
            else if (file.Name == "pyproject.toml")
            {
                ReadPython(path, text, PyprojectString(), folder.PythonDependencies);
                ReadPython(path, text, PyprojectKey(), folder.PythonDependencies);
            }
            else
            {
                ReadPython(path, text, RequirementLine(), folder.PythonDependencies);
            }
        }

        return [.. folders.Values.OrderBy(f => f.Dir.Length == 0 ? 0 : f.Dir.Count(ch => ch == '/') + 1).ThenBy(f => f.Dir, StringComparer.Ordinal)];
    }

    private static void ReadPackageJson(string path, string text, Dictionary<string, Dependency> into)
    {
        try
        {
            using var document = JsonDocument.Parse(text, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (!document.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var dep in deps.EnumerateObject())
                {
                    var version = dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() : null;
                    into.TryAdd(dep.Name, new Dependency(ShortVersion(version), path));
                }
            }
        }
        catch (JsonException)
        {
            // Unparsable: skipped (detection never blocks).
        }
    }

    private static Project ReadProject(string path, string text)
    {
        var references = new List<Reference>();
        foreach (Match element in ProjectReference().Matches(text))
        {
            var attributes = element.Groups["attrs"].Value;
            var include = Attribute(IncludeAttribute(), attributes);
            if (include is not null)
            {
                references.Add(new Reference(include, ShortVersion(Attribute(VersionAttribute(), attributes))));
            }
        }

        return new Project(path, WebSdk().IsMatch(text), references);
    }

    private static string? Attribute(Regex attribute, string attributes)
    {
        var match = attribute.Match(attributes);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static void ReadPython(string path, string text, Regex pattern, Dictionary<string, Dependency> into)
    {
        foreach (Match match in pattern.Matches(text))
        {
            var name = match.Groups["name"].Value.ToLowerInvariant().Replace('_', '-');
            var version = match.Groups["ver"].Success ? match.Groups["ver"].Value : null;
            into.TryAdd(name, new Dependency(ShortVersion(version), path));
        }
    }

    /// <summary>"^16.1.3" → "16.1"; no number (<c>workspace:*</c>, <c>latest</c>) → <c>null</c>.</summary>
    private static string? ShortVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = VersionNumber().Match(version);
        return match.Success ? match.Value : null;
    }

    private static string Evidence(string package, string? version, string file) =>
        version is null ? $"`{package}` in `{file}`" : $"`{package}` {version} in `{file}`";

    // ── Paths ────────────────────────────────────────────────────────────

    private static List<RepoFile> Normalise(IEnumerable<string> files)
    {
        var result = new List<RepoFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in files)
        {
            if (raw is null)
            {
                continue;
            }

            var path = raw.Replace('\\', '/').Trim();
            while (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            path = path.Trim('/');
            if (path.Length == 0 || path.Split('/').Any(s => s is "" or "." or ".." || Ignored.Contains(s)) || !seen.Add(path))
            {
                continue;
            }

            result.Add(new RepoFile(path));
        }

        return result;
    }

    private static bool InNonAppFolder(string dir) => dir.Length > 0 && dir.Split('/').Any(NonAppFolders.Contains);

    private static string PathOf(string dir) => dir.Length == 0 ? "." : dir;

    private static string Join(string dir, string name) => dir.Length == 0 ? name : $"{dir}/{name}";

    private static string? Parent(string dir)
    {
        if (dir.Length == 0)
        {
            return null;
        }

        var slash = dir.LastIndexOf('/');
        return slash < 0 ? string.Empty : dir[..slash];
    }

    private static string Name(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private sealed record Hit(string Option, string? Path, IReadOnlyList<string> Evidence, string? Mode = null);

    private sealed record Dependency(string? Version, string File);

    private sealed record Reference(string Name, string? Version);

    private sealed record Project(string File, bool WebSdk, IReadOnlyList<Reference> References);

    private sealed class Folder(string dir, string? alembicEvidence)
    {
        public string Dir { get; } = dir;

        /// <summary>An <c>alembic.ini</c> or <c>alembic/</c> folder next to the manifest, as evidence.</summary>
        public string? AlembicEvidence { get; } = alembicEvidence;

        public Dictionary<string, Dependency> NpmDependencies { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Dependency> PythonDependencies { get; } = new(StringComparer.Ordinal);

        public List<(string File, string Text)> Java { get; } = [];

        public List<Project> Projects { get; } = [];
    }

    private sealed class RepoFile
    {
        public RepoFile(string path)
        {
            Path = path;
            var slash = path.LastIndexOf('/');
            Dir = slash < 0 ? string.Empty : path[..slash];
            Name = slash < 0 ? path : path[(slash + 1)..];
            Depth = Dir.Length == 0 ? 0 : Dir.Count(c => c == '/') + 1;
        }

        public string Path { get; }

        /// <summary>The folder holding the file ("" for the root).</summary>
        public string Dir { get; }

        public string Name { get; }

        /// <summary>The folder's depth below the root (0 for the root).</summary>
        public int Depth { get; }
    }

    private sealed class Repo
    {
        private readonly HashSet<string> paths = new(StringComparer.Ordinal);
        private readonly HashSet<string> folders = new(StringComparer.Ordinal);
        private readonly HashSet<string> solutionFolders = new(StringComparer.Ordinal);

        public Repo(IReadOnlyList<RepoFile> files)
        {
            Files = files;
            foreach (var file in files)
            {
                paths.Add(file.Path);
                for (var dir = file.Dir; dir.Length > 0; dir = Parent(dir) ?? string.Empty)
                {
                    if (!folders.Add(dir))
                    {
                        break;
                    }
                }

                if (file.Name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    solutionFolders.Add(file.Dir);
                }
            }

            // An entry may itself be a folder (a listing with "supabase/migrations/").
            foreach (var file in files)
            {
                folders.Add(file.Path);
            }
        }

        public IReadOnlyList<RepoFile> Files { get; }

        public bool HasFile(string path) => paths.Contains(path);

        public bool HasFolder(string path) => folders.Contains(path);

        public bool HasSolution(string dir) => solutionFolders.Contains(dir);
    }

    [GeneratedRegex(@"^next\.config\.(js|mjs|cjs|ts|mts)$", RegexOptions.CultureInvariant)]
    private static partial Regex NextConfig();

    [GeneratedRegex(@"<(?:PackageReference|FrameworkReference)\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReference();

    [GeneratedRegex(@"\bInclude\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex IncludeAttribute();

    [GeneratedRegex(@"\bVersion\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex VersionAttribute();

    [GeneratedRegex(@"<Project\b[^>]*\bSdk\s*=\s*""Microsoft\.NET\.Sdk\.Web""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WebSdk();

    [GeneratedRegex(@"spring-boot-starter-web(?:flux)?(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex SpringWeb();

    [GeneratedRegex(@"org\.flywaydb|flyway-core", RegexOptions.CultureInvariant)]
    private static partial Regex Flyway();

    [GeneratedRegex(@"spring-boot-starter-data-jpa|jakarta\.persistence", RegexOptions.CultureInvariant)]
    private static partial Regex Jpa();

    // requirements*.txt: "fastapi[standard]==0.115.0", "sqlalchemy>=2.0", "alembic".
    [GeneratedRegex(@"^[ \t]*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]\r\n]*\])?[ \t]*(?:(?:===|==|>=|~=|<=|!=|>|<)[ \t]*(?<ver>[0-9][^\s,;#]*))?", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex RequirementLine();

    // pyproject.toml (PEP 621): dependencies = ["fastapi>=0.115", "sqlalchemy[asyncio]"].
    [GeneratedRegex(@"[""'](?<name>[A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]""']*\])?[ \t]*(?:(?:===|==|>=|~=|<=|!=|>|<)[ \t]*(?<ver>[0-9][^""',;\s]*))?[^""'\r\n]*[""']", RegexOptions.CultureInvariant)]
    private static partial Regex PyprojectString();

    // pyproject.toml (Poetry): fastapi = "^0.115" or fastapi = {version = "^0.115", extras = [...]}.
    [GeneratedRegex(@"^[ \t]*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)[ \t]*=[ \t]*(?:[""'][\^~>=<! ]*(?<ver>[0-9][^""']*)?[^""']*[""']|\{[^}\r\n]*?version[ \t]*=[ \t]*[""'][\^~>=<! ]*(?<ver>[0-9][^""']*)?[^""']*[""'])", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PyprojectKey();

    [GeneratedRegex(@"\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();
}
