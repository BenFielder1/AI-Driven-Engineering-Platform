using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Deployments;
using Aide.Domain.Execution;
using Aide.Domain.Review;

namespace Aide.Domain.Stack;

/// <summary>
/// What the deterministic stack checks read: the reviewed files (path and content), and optionally the files on the base
/// commit (<see cref="BaseFiles"/>: a migration is new when it isn't there; unknown → every migration given is new) and the
/// branch's full file list (<see cref="Tree"/>: a platform config file missing from it is reported; unknown → only the
/// config files given are checked).
/// </summary>
public sealed record StackReviewInput(IReadOnlyList<SourceFile> Files, IReadOnlySet<string>? BaseFiles = null, IReadOnlyCollection<string>? Tree = null);

/// <summary>
/// FR-126, §4.18 "Review checks": the deterministic, language-independent text checks the selected packs register
/// (<see cref="StackResolution.ReviewChecks"/>), with v1 §4.7 categories and severities; every finding is
/// <see cref="FindingSource.Deterministic"/>, so it carries the §4.7 penalty. Mirrors v2's deterministic preset rules.
/// </summary>
public static partial class StackReviewChecks
{
    public const string RlsOnEveryTable = "rls_on_every_table";
    public const string GrantsForExposedTables = "grants_for_exposed_tables";
    public const string NoSecretInClient = "no_secret_in_client";
    public const string NoCommittedSecrets = "no_committed_secrets";
    public const string NoEnsureCreated = "no_ensure_created";
    public const string NoDdlAutoUpdate = "no_ddl_auto_update";
    public const string NoCreateAll = "no_create_all";
    public const string NoSchemaPush = "no_schema_push";
    public const string MigrationBackwardCompatible = "migration_backward_compatible";
    public const string ContainerNonRoot = "container_non_root";
    public const string DeployBranchControl = "deploy_branch_control";
    public const string HealthEndpoints = "health_endpoints";

    /// <summary>Where Supabase migrations live (§4.12, the data-supabase pack).</summary>
    public const string MigrationsFolder = "supabase/migrations/";

    /// <summary>The public env prefixes the browser sees (§4.18 <c>no_secret_in_client</c>).</summary>
    public static IReadOnlyList<string> PublicPrefixes { get; } = ["NEXT_PUBLIC_", "VITE_"];

    /// <summary>The findings of every check <paramref name="resolution"/> registers.</summary>
    public static IReadOnlyList<ReviewFinding> Run(StackResolution resolution, StackReviewInput input)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(input);
        var files = input.Files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).ToList();
        var composition = resolution.Composition;
        var frontendPrefix = PublicPrefixOf(resolution);
        var findings = new List<ReviewFinding>();
        foreach (var id in resolution.ReviewChecks)
        {
            findings.AddRange(id switch
            {
                RlsOnEveryTable => RlsOnEveryTableFindings(files),
                GrantsForExposedTables => GrantsForExposed(files),
                NoSecretInClient => SecretInClient(files, resolution.Paths.Frontend, frontendPrefix),
                NoCommittedSecrets => CommittedSecrets(files),
                NoEnsureCreated => EnsureCreated(files),
                NoDdlAutoUpdate => DdlAutoUpdate(files),
                NoCreateAll => CreateAll(files),
                NoSchemaPush => SchemaPush(files),
                MigrationBackwardCompatible => BackwardCompatible(files, resolution.IsMigration, p => input.BaseFiles is null || !input.BaseFiles.Contains(p)),
                ContainerNonRoot when resolution.Paths.Backend is { } be => NonRootContainer(files, be),
                DeployBranchControl => BranchControl(files, BranchControlTargets(resolution), input.Tree),
                HealthEndpoints when composition.Backend is { } backend => Health(files, backend.Pack, backend.Path),
                _ => [],
            });
        }

        return findings;
    }

    // ---------- Supabase ----------

    /// <summary>
    /// <c>rls_on_every_table</c> (FR-77, FR-126): one critical security finding per table created in
    /// <c>supabase/migrations/**</c> without a matching <c>alter table [public.]&lt;name&gt; enable row level security</c> in
    /// some migration (case-insensitive; SQL comments are ignored; temporary tables are not tables of the schema).
    /// </summary>
    public static IReadOnlyList<ReviewFinding> RlsOnEveryTableFindings(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var migrations = files
            .Select(f => f with { Path = PathRules.Normalize(f.Path) })
            .Where(f => f.Path.StartsWith(MigrationsFolder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => (f.Path, Sql: StripSqlComments(f.Content)))
            .ToList();

        var enabled = migrations
            .SelectMany(m => EnableRlsPattern().Matches(m.Sql).Select(x => TableName(x.Groups["name"].Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = new List<ReviewFinding>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, sql) in migrations)
        {
            foreach (Match created in CreateTablePattern().Matches(sql))
            {
                var name = TableName(created.Groups["name"].Value);
                if (!enabled.Contains(name) && reported.Add(name))
                {
                    findings.Add(new ReviewFinding(
                        ReviewCategory.Security,
                        Severity.Critical,
                        $"Table '{name}' is created without enabling row level security (add `alter table {name} enable row level security;` and its policies).",
                        path));
                }
            }
        }

        return findings;
    }

    /// <summary>Whether the file's first statement (after whitespace, a BOM and comments) is the <c>"use client"</c> directive.</summary>
    public static bool IsClientFile(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.TrimStart('﻿');
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (string.CompareOrdinal(text, i, "//", 0, 2) == 0)
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        var rest = text[i..];
        return rest.StartsWith("\"use client\"", StringComparison.Ordinal) || rest.StartsWith("'use client'", StringComparison.Ordinal);
    }

    /// <summary><c>grants_for_exposed_tables</c>: a new <c>public</c> table in <c>supabase/migrations/</c> without a grant to <c>authenticated</c> or <c>anon</c> (major api_contracts).</summary>
    public static IReadOnlyList<ReviewFinding> GrantsForExposed(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var migrations = files
            .Select(f => f with { Path = PathRules.Normalize(f.Path) })
            .Where(f => f.Path.StartsWith(MigrationsFolder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => (f.Path, Sql: StripSqlComments(f.Content)))
            .ToList();

        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allGranted = false;
        foreach (var (_, sql) in migrations)
        {
            foreach (Match grant in GrantPattern().Matches(sql))
            {
                if (!ApiRolePattern().IsMatch(grant.Groups["roles"].Value))
                {
                    continue;
                }

                var target = grant.Groups["target"].Value.Trim();
                if (AllTablesInPublicPattern().IsMatch(target))
                {
                    allGranted = true;
                    continue;
                }

                foreach (var name in TableKeywordPattern().Replace(target, string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    granted.Add(TableName(name));
                }
            }
        }

        var findings = new List<ReviewFinding>();
        if (allGranted)
        {
            return findings;
        }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, sql) in migrations)
        {
            foreach (Match created in CreateTablePattern().Matches(sql))
            {
                var name = TableName(created.Groups["name"].Value);
                if (name.Contains('.', StringComparison.Ordinal) || granted.Contains(name) || !reported.Add(name))
                {
                    continue; // another schema isn't exposed to the Data API
                }

                findings.Add(new ReviewFinding(
                    ReviewCategory.ApiContracts,
                    Severity.Major,
                    $"Table 'public.{name}' is not exposed to the Data API: new tables get no grants by default, so add `grant select, insert, update, delete on public.{name} to authenticated;` (and `anon` where the policies allow it).",
                    path));
            }
        }

        return findings;
    }

    // ---------- secrets ----------

    /// <summary>
    /// <c>no_secret_in_client</c> (critical security) over the frontend's files (<paramref name="frontendPath"/>; <c>null</c>: no
    /// frontend, no findings): (1) a public-prefixed name (<c>NEXT_PUBLIC_</c>/<c>VITE_</c>) or an Angular
    /// <c>environment*.ts</c> key that looks secret (<c>SECRET</c>, <c>SERVICE_ROLE</c>, <c>PRIVATE</c>, <c>PASSWORD</c>) or holds
    /// an <c>sb_secret_</c>/<c>sk_live</c> value; (2) with a public prefix (<paramref name="publicPrefix"/>), a non-public env
    /// name read in a <c>"use client"</c> file.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> SecretInClient(IEnumerable<SourceFile> files, string? frontendPath, string? publicPrefix)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<ReviewFinding>();
        if (frontendPath is null)
        {
            return findings;
        }

        foreach (var file in files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!IsUnder(file.Path, frontendPath) || file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            void Report(string name, string message)
            {
                if (reported.Add(name))
                {
                    findings.Add(new ReviewFinding(ReviewCategory.Security, Severity.Critical, message, file.Path));
                }
            }

            foreach (Match match in PublicNamePattern().Matches(file.Content))
            {
                var name = match.Groups["name"].Value;
                if (SecretWordPattern().IsMatch(name) || (match.Groups["value"].Success && SecretValuePattern().IsMatch(match.Groups["value"].Value)))
                {
                    Report(name, $"Secret-like value exposed to the browser: {name} is a public ({PrefixOf(name)}) variable, so it is bundled into client code. Keep secrets server-side.");
                }
            }

            if (AngularEnvironmentPattern().IsMatch(file.Path))
            {
                foreach (Match match in ObjectKeyPattern().Matches(file.Content))
                {
                    var key = match.Groups["key"].Value;
                    if (SecretWordPattern().IsMatch(key) || SecretValuePattern().IsMatch(match.Groups["value"].Value))
                    {
                        Report(key, $"Secret-like value exposed to the browser: the Angular environment key '{key}' is compiled into the bundle. Keep secrets server-side.");
                    }
                }
            }

            if (publicPrefix is not null && IsClientFile(file.Content))
            {
                foreach (Match match in EnvReadPattern().Matches(file.Content))
                {
                    var name = match.Groups["name"].Value;
                    if (!name.StartsWith(publicPrefix, StringComparison.Ordinal) && !BuiltInEnv.Contains(name, StringComparer.Ordinal))
                    {
                        Report(name, $"The server-only environment variable {name} is read in a \"use client\" file; only {publicPrefix}* values reach the browser, and a secret there would be exposed to it.");
                    }
                }
            }
        }

        return findings;
    }

    /// <summary><c>no_committed_secrets</c>: the vcs packs' secret-scan patterns outside <c>*.example</c> files (critical security, once per file and pattern).</summary>
    public static IReadOnlyList<ReviewFinding> CommittedSecrets(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<ReviewFinding>();
        foreach (var file in files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (file.Path.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isSql = file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
            var lines = file.Content.ReplaceLineEndings("\n").Split('\n');
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, label) in SecretPatterns())
                {
                    if (label == "service_role" && isSql)
                    {
                        continue; // Supabase SQL grants and policies name the service_role role
                    }

                    if (pattern.IsMatch(lines[i]) && seen.Add(label))
                    {
                        findings.Add(new ReviewFinding(
                            ReviewCategory.Security,
                            Severity.Critical,
                            string.Create(CultureInfo.InvariantCulture, $"Possible committed secret ({label}) at line {i + 1}. Secrets never go in the repository; only *.example files hold placeholders."),
                            file.Path));
                    }
                }
            }
        }

        return findings;
    }

    // ---------- ORM rules ----------

    /// <summary><c>no_ensure_created</c>: <c>EnsureCreated(</c> or <c>Database.Migrate(</c> in non-test C# (major architecture).</summary>
    public static IReadOnlyList<ReviewFinding> EnsureCreated(IEnumerable<SourceFile> files) =>
        LineFindings(
            files,
            p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsTestPath(p),
            EnsureCreatedPattern(),
            "//",
            m => $"{m.Value.TrimEnd('(').Trim()}() creates or migrates the schema at app start; migrations run as the one-off migration bundle step instead.");

    /// <summary><c>no_ddl_auto_update</c>: <c>ddl-auto</c> set to <c>update</c>, <c>create</c> or <c>create-drop</c> outside test config (major architecture).</summary>
    public static IReadOnlyList<ReviewFinding> DdlAutoUpdate(IEnumerable<SourceFile> files) =>
        LineFindings(
            files,
            p => (p.EndsWith(".properties", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                && !IsTestPath(p) && !p.Split('/')[^1].Contains("test", StringComparison.OrdinalIgnoreCase),
            DdlAutoPattern(),
            "#",
            m => $"Hibernate ddl-auto is '{m.Groups["value"].Value}': the schema is owned by Flyway migrations, so use 'validate' (or 'none').");

    /// <summary><c>no_create_all</c>: <c>create_all(</c> in non-test Python (major architecture).</summary>
    public static IReadOnlyList<ReviewFinding> CreateAll(IEnumerable<SourceFile> files) =>
        LineFindings(
            files,
            p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && !IsTestPath(p),
            CreateAllPattern(),
            "#",
            _ => "metadata.create_all() creates the schema outside Alembic; migrations own the schema (alembic upgrade head runs as the one-off step).");

    /// <summary><c>no_schema_push</c>: <c>prisma db push</c> or <c>drizzle-kit push</c> in a <c>package.json</c> script (major architecture).</summary>
    public static IReadOnlyList<ReviewFinding> SchemaPush(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<ReviewFinding>();
        foreach (var file in files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!file.Path.Split('/')[^1].Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (name, command) in Scripts(file.Content))
            {
                if (SchemaPushPattern().Match(command) is { Success: true } push)
                {
                    findings.Add(new ReviewFinding(
                        ReviewCategory.Architecture,
                        Severity.Major,
                        $"The '{name}' script runs `{push.Value}`, which changes the database without a migration; create a migration and apply it with the migrate command instead.",
                        file.Path));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// <c>migration_backward_compatible</c>: a new migration (<paramref name="isMigration"/> and <paramref name="isNew"/>) that drops
    /// a table or column, renames a column or changes a column's type (major <c>data_model</c>: expand/contract required,
    /// because preview and production deploys aren't atomic). C# and Python migrations are read in their <c>Up</c>/<c>upgrade</c> only.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> BackwardCompatible(IEnumerable<SourceFile> files, Func<string, bool> isMigration, Func<string, bool> isNew)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(isMigration);
        ArgumentNullException.ThrowIfNull(isNew);
        var findings = new List<ReviewFinding>();
        foreach (var file in files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!isMigration(file.Path) || !isNew(file.Path) || file.Path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                || file.Path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var operations = file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? EfOperations(UpOnly(file.Content, "Up(", "Down("))
                : file.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? AlembicOperations(UpOnly(file.Content, "def upgrade(", "def downgrade("))
                : SqlOperations(StripSqlComments(file.Content));
            foreach (var operation in operations.Distinct(StringComparer.Ordinal))
            {
                findings.Add(new ReviewFinding(
                    ReviewCategory.DataModel,
                    Severity.Major,
                    $"The new migration {operation}: expand/contract required, because preview and production deploys aren't atomic (the previous version still runs against the new schema). Add the new shape first and remove the old one in a later version.",
                    file.Path));
            }
        }

        return findings;
    }

    // ---------- containers, deploys, health ----------

    /// <summary><c>container_non_root</c>: a backend Dockerfile (under <paramref name="backendPath"/>) whose final stage has no non-root <c>USER</c> (minor security).</summary>
    public static IReadOnlyList<ReviewFinding> NonRootContainer(IEnumerable<SourceFile> files, string backendPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(backendPath);
        var findings = new List<ReviewFinding>();
        foreach (var file in files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var name = file.Path.Split('/')[^1];
            var isDockerfile = name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase);
            if (!isDockerfile || !IsUnder(file.Path, backendPath))
            {
                continue;
            }

            string? user = null;
            foreach (var line in file.Content.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()))
            {
                if (FromPattern().IsMatch(line))
                {
                    user = null; // a new stage starts as root
                }
                else if (UserPattern().Match(line) is { Success: true } match)
                {
                    user = match.Groups["user"].Value;
                }
            }

            if (user is null || user is "root" or "0" || user.StartsWith("root:", StringComparison.Ordinal) || user.StartsWith("0:", StringComparison.Ordinal))
            {
                findings.Add(new ReviewFinding(
                    ReviewCategory.Security,
                    Severity.Minor,
                    "The backend container runs as root: add a non-root USER to the final stage (e.g. `USER $APP_UID`, `USER node`, or a created user).",
                    file.Path));
            }
        }

        return findings;
    }

    /// <summary>
    /// <c>deploy_branch_control</c> (major architecture, generalises v2 FR-89): each target's config file must stop the platform
    /// building AIDE branches on its own: <c>vercel.json</c> sets <c>git.deploymentEnabled["aide/**"]</c> to <c>false</c>
    /// (<see cref="VercelConfigRules"/>); <c>netlify.toml</c> has a <c>[build] ignore</c> that skips <c>aide/*</c>. Cloudflare's
    /// branch control is the Workers preview trigger AIDE configures, so it has no file to check. A config file missing
    /// from <paramref name="tree"/> is reported; without a tree only the files given are checked.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> BranchControl(IEnumerable<SourceFile> files, IEnumerable<(string Provider, string ConfigPath)> targets, IReadOnlyCollection<string>? tree)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(targets);
        var byPath = files.GroupBy(f => PathRules.Normalize(f.Path), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Content, StringComparer.Ordinal);
        var findings = new List<ReviewFinding>();
        foreach (var (provider, path) in targets.Distinct())
        {
            if (!byPath.TryGetValue(path, out var content))
            {
                if (tree is not null && !tree.Select(PathRules.Normalize).Contains(path, StringComparer.Ordinal))
                {
                    findings.Add(BranchFinding(path, $"{path} is missing, so {provider} builds every AIDE branch on its own: {BranchRule(provider)}."));
                }

                continue;
            }

            var ok = provider == "netlify" ? NetlifyIgnoresAide(content) : VercelConfigRules.DisablesAideBranches(content);
            if (!ok)
            {
                findings.Add(BranchFinding(path, $"{path} doesn't stop {provider} building AIDE branches on its own: {BranchRule(provider)}. AIDE deploys them explicitly."));
            }
        }

        return findings;
    }

    /// <summary>Whether a <c>netlify.toml</c> has an <c>ignore</c> command that skips <c>aide/*</c> branches.</summary>
    public static bool NetlifyIgnoresAide(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Any(l => !l.StartsWith('#') && NetlifyIgnorePattern().IsMatch(l) && l.Contains("aide/", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>health_endpoints</c> (minor feature_coverage): the backend pack's live and ready paths (<see cref="StackHealth"/>)
    /// appear nowhere in the backend's files. Without backend files in the input nothing is reported.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> Health(IEnumerable<SourceFile> files, string backendPack, string backendPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(backendPack);
        ArgumentNullException.ThrowIfNull(backendPath);
        var source = files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).Where(f => IsUnder(f.Path, backendPath) && !IsTestPath(f.Path)).ToList();
        if (source.Count == 0)
        {
            return [];
        }

        var (live, ready) = StackHealth.Paths(backendPack);
        // The full path, the path without the /api prefix (a mounted router), or a "health" group with the quoted leaf
        // (MapGroup("/api/health").MapGet("/live", …)).
        bool Mentions(string path)
        {
            var leaf = path[(path.LastIndexOf('/') + 1)..];
            return source.Any(f => f.Content.Contains(path, StringComparison.Ordinal)
                || f.Content.Contains(path["/api".Length..], StringComparison.Ordinal)
                || (f.Content.Contains("/health", StringComparison.Ordinal)
                    && (f.Content.Contains($"\"/{leaf}\"", StringComparison.Ordinal) || f.Content.Contains($"'/{leaf}'", StringComparison.Ordinal))));
        }

        var found = backendPack == "be-spring-boot"
            ? source.Any(f => ActuatorProbesPattern().IsMatch(f.Content)) || (Mentions(live) && Mentions(ready))
            : Mentions(live) && Mentions(ready);
        return found
            ? []
            : [new ReviewFinding(ReviewCategory.FeatureCoverage, Severity.Minor, $"The backend's health endpoints ({live}, {ready}) weren't found: the platforms' health checks and the readiness gate use them.")];
    }

    // ---------- helpers ----------

    /// <summary>Whether <paramref name="path"/> is a test file or under a test folder (<c>tests/</c>, <c>src/test/</c>, <c>*.Tests/</c>, <c>__tests__/</c>, <c>e2e/</c>, <c>test_*.py</c>, <c>*Tests.cs</c>, <c>*.test.ts</c>, …).</summary>
    public static bool IsTestPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = PathRules.Normalize(path).ToLowerInvariant().Split('/');
        var folders = segments[..^1];
        var name = segments[^1];
        return folders.Any(s => s is "test" or "tests" or "__tests__" or "e2e" or "spec" || s.EndsWith("tests", StringComparison.Ordinal) || s.EndsWith(".test", StringComparison.Ordinal))
            || name.StartsWith("test_", StringComparison.Ordinal) || name.StartsWith("conftest", StringComparison.Ordinal)
            || TestFileNamePattern().IsMatch(name);
    }

    private static string? PublicPrefixOf(StackResolution resolution) =>
        resolution.Composition.Frontend is { } fe ? resolution.Packs.FirstOrDefault(p => p.Id == fe.Pack)?.Pack.Manifest.PublicEnvPrefix : null;

    private static IEnumerable<(string Provider, string ConfigPath)> BranchControlTargets(StackResolution resolution)
    {
        var deploy = resolution.Composition.Deploy;
        var paths = resolution.Paths;
        foreach (var (host, root) in new[] { (deploy.FrontendHost, paths.Frontend), (deploy.BackendHost, paths.Backend) })
        {
            if (host == "deploy-vercel")
            {
                yield return ("vercel", VercelConfigRules.ConfigPath(root));
            }
            else if (host == "deploy-netlify")
            {
                yield return ("netlify", root is null or StackPaths.Root ? "netlify.toml" : $"{root}/netlify.toml");
            }
        }
    }

    private static string BranchRule(string provider) => provider == "netlify"
        ? "add `[build] ignore = 'case \"$BRANCH\" in aide/*) exit 0 ;; *) exit 1 ;; esac'`"
        : $"set git.deploymentEnabled {{ \"{VercelConfigRules.AideBranches}\": false }}";

    private static ReviewFinding BranchFinding(string path, string message) => new(ReviewCategory.Architecture, Severity.Major, message, path);

    private static bool IsUnder(string path, string folder) =>
        folder == StackPaths.Root || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static string PrefixOf(string name) => PublicPrefixes.First(p => name.StartsWith(p, StringComparison.Ordinal)) + "*";

    private static IReadOnlyList<string> BuiltInEnv { get; } = ["NODE_ENV", "MODE", "DEV", "PROD", "SSR", "BASE_URL"];

    private static List<ReviewFinding> LineFindings(IEnumerable<SourceFile> files, Func<string, bool> applies, Regex pattern, string comment, Func<Match, string> message)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<ReviewFinding>();
        foreach (var file in files.Select(f => f with { Path = PathRules.Normalize(f.Path) }).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!applies(file.Path))
            {
                continue;
            }

            var lines = file.Content.ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith(comment, StringComparison.Ordinal) || pattern.Match(lines[i]) is not { Success: true } match)
                {
                    continue;
                }

                findings.Add(new ReviewFinding(
                    ReviewCategory.Architecture,
                    Severity.Major,
                    string.Create(CultureInfo.InvariantCulture, $"{message(match)} (line {i + 1})"),
                    file.Path));
                break; // once per file
            }
        }

        return findings;
    }

    private static List<(string Name, string Command)> Scripts(string packageJson)
    {
        var scripts = new List<(string Name, string Command)>();
        try
        {
            using var doc = JsonDocument.Parse(packageJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("scripts", out var node) && node.ValueKind == JsonValueKind.Object)
            {
                scripts.AddRange(node.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => (p.Name, p.Value.GetString() ?? string.Empty)));
            }
        }
        catch (JsonException)
        {
            scripts.Add(("(unreadable package.json)", packageJson));
        }

        return scripts;
    }

    private static string UpOnly(string content, string up, string down)
    {
        var start = content.IndexOf(up, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf(down, start, StringComparison.Ordinal);
        return end < 0 ? content[start..] : content[start..end];
    }

    private static IEnumerable<string> EfOperations(string up)
    {
        foreach (Match match in EfDestructivePattern().Matches(up))
        {
            yield return match.Groups["op"].Value switch
            {
                "DropTable" => "drops a table (DropTable)",
                "DropColumn" => "drops a column (DropColumn)",
                _ => "renames a column (RenameColumn)",
            };
        }

        foreach (Match match in EfAlterColumnPattern().Matches(up))
        {
            var call = match.Value;
            var type = EfTypePattern().Match(call);
            var oldType = EfOldTypePattern().Match(call);
            var clr = match.Groups["clr"].Value.TrimEnd('?');
            var oldClr = EfOldClrPattern().Match(call);
            if ((type.Success && oldType.Success && !type.Groups["v"].Value.Equals(oldType.Groups["v"].Value, StringComparison.OrdinalIgnoreCase))
                || (oldClr.Success && !oldClr.Groups["v"].Value.TrimEnd('?').Equals(clr, StringComparison.Ordinal)))
            {
                yield return "changes a column's type (AlterColumn)";
            }
        }
    }

    private static IEnumerable<string> AlembicOperations(string upgrade)
    {
        foreach (Match match in AlembicPattern().Matches(upgrade))
        {
            var op = match.Groups["op"].Value;
            if (op == "drop_table")
            {
                yield return "drops a table (op.drop_table)";
            }
            else if (op == "drop_column")
            {
                yield return "drops a column (op.drop_column)";
            }
            else
            {
                var args = match.Groups["args"].Value;
                if (args.Contains("new_column_name", StringComparison.Ordinal))
                {
                    yield return "renames a column (op.alter_column new_column_name)";
                }

                if (args.Contains("type_", StringComparison.Ordinal))
                {
                    yield return "changes a column's type (op.alter_column type_)";
                }
            }
        }
    }

    private static IEnumerable<string> SqlOperations(string sql)
    {
        if (SqlDropTablePattern().IsMatch(sql))
        {
            yield return "drops a table (drop table)";
        }

        if (SqlDropColumnPattern().IsMatch(sql))
        {
            yield return "drops a column (drop column)";
        }

        if (SqlRenameColumnPattern().IsMatch(sql))
        {
            yield return "renames a column (rename column)";
        }

        if (SqlTypeChangePattern().IsMatch(sql))
        {
            yield return "changes a column's type (alter column … type)";
        }
    }

    private static (Regex Pattern, string Label)[] SecretPatterns() =>
    [
        (SecretAssignmentPattern(), "PASSWORD=/SECRET=/API_KEY="),
        (PrivateKeyPattern(), "PRIVATE KEY"),
        (LiveKeyPattern(), "sk_live"),
        (ServiceRolePattern(), "service_role"),
    ];

    private static string TableName(string raw)
    {
        var parts = raw.Split('.', StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"').ToLower(CultureInfo.InvariantCulture))
            .ToList();
        return parts.Count == 2 && parts[0] == "public" ? parts[1] : string.Join('.', parts);
    }

    private static string StripSqlComments(string sql) => SqlCommentPattern().Replace(sql, " ");

    private const string Name = @"(?:""[^""]+""|[A-Za-z_][\w$]*)";

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlCommentPattern();

    [GeneratedRegex(@"\bcreate\s+(?:unlogged\s+)?table\s+(?:if\s+not\s+exists\s+)?(?<name>" + Name + @"(?:\s*\.\s*" + Name + @")?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreateTablePattern();

    [GeneratedRegex(@"\balter\s+table\s+(?:if\s+exists\s+)?(?:only\s+)?(?<name>" + Name + @"(?:\s*\.\s*" + Name + @")?)\s+enable\s+row\s+level\s+security\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnableRlsPattern();

    [GeneratedRegex(@"\bgrant\b[^;]*?\bon\s+(?<target>[^;]*?)\bto\b(?<roles>[^;]*)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GrantPattern();

    [GeneratedRegex(@"\b(?:authenticated|anon)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApiRolePattern();

    [GeneratedRegex(@"^all\s+tables\s+in\s+schema\s+public\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AllTablesInPublicPattern();

    [GeneratedRegex(@"^\s*table\s+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableKeywordPattern();

    [GeneratedRegex(@"\b(?<name>(?:NEXT_PUBLIC_|VITE_)[A-Za-z0-9_]+)\b(?:\s*[=:]\s*[""'`]?(?<value>[^\s""'`,;]+))?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PublicNamePattern();

    [GeneratedRegex(@"SECRET|SERVICE_?ROLE|PRIVATE|PASSWORD", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretWordPattern();

    [GeneratedRegex(@"sb_secret_|sk_live", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretValuePattern();

    [GeneratedRegex(@"(?:^|/)environments/environment[^/]*\.ts$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AngularEnvironmentPattern();

    [GeneratedRegex(@"(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<value>[^,\n}]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ObjectKeyPattern();

    [GeneratedRegex(@"\b(?:process\s*\.\s*env|import\s*\.\s*meta\s*\.\s*env)\s*(?:\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[\s*[""'`](?<name>[A-Za-z_][A-Za-z0-9_]*)[""'`]\s*\])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnvReadPattern();

    [GeneratedRegex(@"\b[A-Z0-9_]*(?:PASSWORD|SECRET|API_KEY)\s*=\s*(?![\s""']*$)(?![""']?\$)(?![""']?<)\S", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"PRIVATE KEY-----", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"\bsk_live_[A-Za-z0-9]{8,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiveKeyPattern();

    [GeneratedRegex(@"\bservice_role\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ServiceRolePattern();

    [GeneratedRegex(@"\b(?:EnsureCreated(?:Async)?|Database\s*\.\s*Migrate(?:Async)?)\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnsureCreatedPattern();

    [GeneratedRegex(@"ddl-auto\s*[=:]\s*[""']?(?<value>create-drop|create|update)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DdlAutoPattern();

    [GeneratedRegex(@"\bcreate_all\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreateAllPattern();

    [GeneratedRegex(@"\bprisma\s+db\s+push\b|\bdrizzle-kit\s+push\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SchemaPushPattern();

    [GeneratedRegex(@"\.\s*(?<op>DropTable|DropColumn|RenameColumn)\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EfDestructivePattern();

    [GeneratedRegex(@"\.\s*AlterColumn\s*<\s*(?<clr>[\w.?\[\]]+)\s*>\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EfAlterColumnPattern();

    [GeneratedRegex(@"(?<![\w])type\s*:\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EfTypePattern();

    [GeneratedRegex(@"\boldType\s*:\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EfOldTypePattern();

    [GeneratedRegex(@"\boldClrType\s*:\s*typeof\s*\(\s*(?<v>[\w.?\[\]]+)\s*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EfOldClrPattern();

    [GeneratedRegex(@"\bop\s*\.\s*(?<op>drop_table|drop_column|alter_column)\s*\((?<args>[^)]*)\)", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AlembicPattern();

    [GeneratedRegex(@"\bdrop\s+table\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlDropTablePattern();

    [GeneratedRegex(@"\bdrop\s+column\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlDropColumnPattern();

    [GeneratedRegex(@"\brename\s+column\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlRenameColumnPattern();

    [GeneratedRegex(@"\balter\s+column\s+(?:""[^""]+""|[\w$]+)\s+(?:set\s+data\s+)?type\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlTypeChangePattern();

    [GeneratedRegex(@"^FROM\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FromPattern();

    [GeneratedRegex(@"^USER\s+(?<user>\S+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^\s*ignore\s*=", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NetlifyIgnorePattern();

    [GeneratedRegex(@"probes\.enabled\s*[=:]\s*true|probes:\s*\n\s+enabled:\s*true", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActuatorProbesPattern();

    [GeneratedRegex(@"[._-](?:tests?|spec)\.[a-z0-9]+$|_test\.(?:py|go)$|tests?\.(?:cs|java|kt)$|it\.java$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TestFileNamePattern();
}

/// <summary>
/// FR-126: the composed review checklist in the Code Review agent's reply (the composition equivalent of v2's
/// preset checklist). A failed item is reported as an agent violation whose message starts with the
/// item's marker (<c>[C1]</c>, …, in <see cref="StackResolution.Checklist"/> order); such violations take the item's
/// category and severity and stay agent findings without penalties.
/// </summary>
public static class StackReviewChecklist
{
    /// <summary>The marker of checklist item <paramref name="number"/> (1-based): <c>[C1]</c>.</summary>
    public static string Marker(int number) => $"[C{number.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>The checklist item a violation message refers to (by its leading <c>[Cn]</c> marker), or <c>null</c>.</summary>
    public static StackChecklistItem? ItemOf(string message, StackResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(resolution);
        var trimmed = message.TrimStart();
        if (!trimmed.StartsWith("[C", StringComparison.Ordinal))
        {
            return null;
        }

        var close = trimmed.IndexOf(']', StringComparison.Ordinal);
        return close > 2 && close <= 5 && int.TryParse(trimmed[2..close], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= 1 && n <= resolution.Checklist.Count
                ? resolution.Checklist[n - 1]
                : null;
    }

    /// <summary>The agent's violations with every checklist violation mapped to its item's category and severity; others unchanged.</summary>
    public static IReadOnlyList<ReviewFinding> Apply(IReadOnlyList<ReviewFinding> violations, StackResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(violations);
        ArgumentNullException.ThrowIfNull(resolution);
        return
        [
            .. violations.Select(v => v.Source == FindingSource.Agent && ItemOf(v.Message, resolution) is { } item
                ? v with { Category = item.Category, Severity = item.Severity }
                : v),
        ];
    }
}
