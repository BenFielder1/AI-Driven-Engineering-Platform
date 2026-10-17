using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Presets;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// FR-74–FR-77 (D13, §8 preset E2E): what the Fake responders read from a preset prompt. The preset blocks are rendered by
/// <see cref="PresetGuidance"/> (spec, plan, review) and <see cref="SpecialistPrompt"/> (code generator) into the system
/// prompt, so a Fake reply can follow the preset offline.
/// </summary>
internal static partial class FakePrompt
{
    /// <summary>The preset id of a <see cref="PresetGuidance.Heading"/> block in the system prompt, or <c>null</c>.</summary>
    public static string? PresetId(LlmRequest request) =>
        PresetHeadingPattern().Match(request.System) is { Success: true } m ? m.Groups["id"].Value : null;

    /// <summary>The extra spec section titles of the preset block (<see cref="PresetGuidance.SpecSectionsHeading"/>).</summary>
    public static IReadOnlyList<string> PresetSpecSections(LlmRequest request)
    {
        var lines = request.System.ReplaceLineEndings("\n").Split('\n');
        var titles = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                inside = line.Trim() == PresetGuidance.SpecSectionsHeading;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (inside && line.StartsWith("#### ", StringComparison.Ordinal))
            {
                titles.Add(line[5..].Trim());
            }
        }

        return titles;
    }

    /// <summary>FR-76: the specialist key of a code generator prompt (<c>## Your role: Name (key)</c>), or <c>null</c>.</summary>
    public static string? SpecialistKey(LlmRequest request) =>
        RolePattern().Match(request.System) is { Success: true } m ? m.Groups["key"].Value : null;

    /// <summary>Whether the plan guidance says the repository already has code (no scaffold-first step).</summary>
    public static bool RepoHasCode(LlmRequest request) =>
        request.System.Contains("This repository already has code", StringComparison.Ordinal);

    [GeneratedRegex(@"^## Stack preset: .+ \(`(?<id>[a-z0-9]+(?:-[a-z0-9]+)*)@[^`]+`\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PresetHeadingPattern();

    [GeneratedRegex(@"^## Your role: .+ \((?<key>[a-z0-9]+(?:-[a-z0-9]+)*)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RolePattern();
}

/// <summary>
/// The Fake provider's scripted preset outputs (D13, §8): a plan valid for the shipped presets <c>nextjs-frontend</c> and
/// <c>nextjs-supabase</c> (agents, covers, ordering, owned paths), and the files a specialist step writes (a Next.js
/// skeleton for the scaffolder, with <c>vercel.json</c> disabling <c>aide/**</c> deployments (FR-89) and a pinned
/// <c>.nvmrc</c>; migrations that enable RLS on every table they create, so the deterministic preset checks pass).
/// </summary>
public static class FakePresetScripts
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The ids this script plans for.</summary>
    public static IReadOnlyList<string> PlannedPresets { get; } = ["nextjs-frontend", "nextjs-supabase"];

    /// <summary>
    /// The plan steps (as the plan generator's JSON objects) for <paramref name="presetId"/>, or <c>null</c> for a preset this
    /// script doesn't know. On an empty repository the scaffolder goes first and the devops engineer last; an iteration
    /// (<paramref name="repoHasCode"/>) adds the feature steps only, covering <paramref name="scope"/>.
    /// </summary>
    public static IReadOnlyList<object>? Plan(string presetId, string resource, string resources, IReadOnlyList<string> scope, bool repoHasCode)
    {
        if (!PlannedPresets.Contains(presetId, StringComparer.Ordinal))
        {
            return null;
        }

        var supabase = presetId == "nextjs-supabase";
        string[] all = scope.Count > 0 ? [.. scope] : ["FR-1", "FR-2", "FR-3", "FR-4"];
        string[] model = scope.Count > 0 ? [] : ["FR-1", "FR-4"];
        var suffix = repoHasCode && scope.Count > 0 ? "-" + scope[0].ToLowerInvariant() : string.Empty;
        var steps = new List<(string Agent, string Title, string Task, string[] Covers, (string Path, string Action)[] Files)>();
        if (!repoHasCode)
        {
            (string, string)[] skeleton = supabase
                ? [("package.json", "create"), ("tsconfig.json", "create"), ("app/layout.tsx", "create"), ("lib/supabase/server.ts", "create"), (".nvmrc", "create"), ("vercel.json", "create")]
                : [("package.json", "create"), ("tsconfig.json", "create"), ("next.config.ts", "create"), ("app/layout.tsx", "create"), (".nvmrc", "create"), ("vercel.json", "create")];
            steps.Add(("project-scaffolder", "Project skeleton", "Scaffold the Next.js App Router project: package.json with pinned Node and the dev, build, lint, typecheck, test and test:e2e scripts, strict TypeScript, the root layout, .nvmrc and vercel.json.", [], skeleton));
        }

        if (supabase)
        {
            steps.Add(("db-engineer", $"{Capitalise(resources)} table and RLS", $"Create the {resources} table with RLS enabled and owner-only policies (FR-1–FR-3), plus seed data.", model, [($"supabase/migrations/0001_{resources}{suffix.Replace('-', '_')}.sql", "create"), ("supabase/seed.sql", repoHasCode ? "modify" : "create")]));
        }

        steps.Add(("domain-logic-engineer", $"{Capitalise(resource)} rules", $"Implement the pure {resource} validation and sorting rules (FR-1, FR-4) with tests citing the requirement ids.", model, [($"lib/{resource}/{resource}{suffix}.ts", "create"), ($"lib/{resource}/{resource}{suffix}.test.ts", "create")]));
        steps.Add(("api-engineer", $"{Capitalise(resources)} API", $"Add the Zod-validated /api/{resources} route handlers (FR-1–FR-4) and the error-code map.", all, [($"app/api/{resources}{suffix}/route.ts", "create"), ($"lib/api/errors{suffix}.ts", "create")]));
        steps.Add(("ui-engineer", $"{Capitalise(resources)} pages", $"Build the {resources} list page with loading, empty and error states (FR-2).", scope.Count > 0 ? [] : ["FR-2"], [($"app/{resources}{suffix}/page.tsx", "create"), ($"app/{resources}{suffix}/loading.tsx", "create"), ($"app/{resources}{suffix}/error.tsx", "create"), ($"components/{Capitalise(resource)}List{suffix}.tsx", "create")]));
        steps.Add(("test-engineer", "End-to-end tests", $"Cover creating, listing and validating {resources} end to end (FR-1–FR-4).", [], [($"e2e/{resources}{suffix}.spec.ts", "create")]));
        if (!repoHasCode)
        {
            steps.Add(("devops-engineer", "CI and environment inventory", "Add the GitHub Actions checks workflow (typecheck, lint, test, build) and the environment variable inventory.", [], [(".github/workflows/ci.yml", "create"), (".env.example", "create")]));
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

    /// <summary>The content a specialist writes for <paramref name="path"/>, or <c>null</c> to use the generic Fake content.</summary>
    public static string? Content(string specialist, string path, string project, int step, string title)
    {
        var fileName = path.Split('/')[^1];
        var header = $"{title}: written by the Fake {specialist} in step {step} of the {project} plan.";
        switch (fileName)
        {
            case "package.json":
                return JsonSerializer.Serialize(
                    new
                    {
                        name = Slug(project),
                        version = "0.1.0",
                        @private = true,
                        engines = new { node = "22.x" },
                        scripts = new Dictionary<string, string>
                        {
                            ["dev"] = "next dev",
                            ["build"] = "next build",
                            ["start"] = "next start",
                            ["lint"] = "eslint .",
                            ["typecheck"] = "tsc --noEmit",
                            ["test"] = "vitest run",
                            ["test:e2e"] = "playwright test",
                        },
                        dependencies = new Dictionary<string, string> { ["next"] = "16.0.0", ["react"] = "19.2.0", ["react-dom"] = "19.2.0", ["zod"] = "4.1.0" },
                        devDependencies = new Dictionary<string, string> { ["typescript"] = "5.9.0", ["vitest"] = "3.2.0", ["@playwright/test"] = "1.56.0" },
                    },
                    Indented) + "\n";
            case "vercel.json":
                // FR-89: AIDE triggers deployments itself; Git pushes of aide/** branches never deploy.
                return "{\n  \"git\": {\n    \"deploymentEnabled\": {\n      \"aide/**\": false\n    }\n  }\n}\n";
            case ".nvmrc":
                return "22\n";
            case "tsconfig.json":
                return """
                    {
                      "compilerOptions": {
                        "target": "ES2022",
                        "lib": ["dom", "dom.iterable", "esnext"],
                        "strict": true,
                        "noUncheckedIndexedAccess": true,
                        "module": "esnext",
                        "moduleResolution": "bundler",
                        "jsx": "preserve",
                        "noEmit": true,
                        "paths": { "@/*": ["./*"] }
                      },
                      "include": ["next-env.d.ts", "**/*.ts", "**/*.tsx"],
                      "exclude": ["node_modules"]
                    }

                    """;
            case "layout.tsx" when path == "app/layout.tsx":
                return $"// {header}\nimport type {{ ReactNode }} from 'react';\n\nexport const metadata = {{ title: '{project.Replace("'", "", StringComparison.Ordinal)}' }};\n\nexport default function RootLayout({{ children }}: {{ children: ReactNode }}) {{\n  return (\n    <html lang=\"en\">\n      <body>{{children}}</body>\n    </html>\n  );\n}}\n";
            case ".env.example":
                return "# Environment variable inventory (no values are ever committed).\n# NEXT_PUBLIC_* values are public; every other name is server-only.\nNEXT_PUBLIC_APP_URL=\n";
            case "ci.yml":
                return $"# {header}\nname: checks\non:\n  pull_request:\n  push:\n    branches: [main]\njobs:\n  checks:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - uses: actions/setup-node@v4\n        with:\n          node-version-file: .nvmrc\n      - run: if [ -f package-lock.json ]; then npm ci; else npm install; fi\n      - run: npm run typecheck\n      - run: npm run lint\n      - run: npm test\n      - run: npm run build\n";
            default:
                break;
        }

        if (path.StartsWith("supabase/migrations/", StringComparison.Ordinal) && fileName.EndsWith(".sql", StringComparison.Ordinal))
        {
            var table = TableName(fileName);
            return $"-- {header}\ncreate table if not exists public.{table} (\n  id uuid primary key default gen_random_uuid(),\n  owner_id uuid not null references auth.users (id) on delete cascade,\n  title text not null check (char_length(title) between 1 and 200),\n  created_at timestamptz not null default now()\n);\n\nalter table public.{table} enable row level security;\n\ncreate policy \"{table}_owner_all\" on public.{table}\n  for all using (owner_id = auth.uid()) with check (owner_id = auth.uid());\n";
        }

        return null;
    }

    private static string TableName(string fileName)
    {
        var stem = fileName[..^4];
        var underscore = stem.IndexOf('_', StringComparison.Ordinal);
        var name = underscore >= 0 && stem[..underscore].All(char.IsAsciiDigit) ? stem[(underscore + 1)..] : stem;
        var clean = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')]);
        return clean.Length == 0 || char.IsAsciiDigit(clean[0]) ? "t_" + clean : clean;
    }

    private static string Slug(string project)
    {
        var slug = new string([.. project.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-') is { Length: > 0 } s ? s : "app";
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
