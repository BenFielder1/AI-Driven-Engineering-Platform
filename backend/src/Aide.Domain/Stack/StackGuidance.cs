using System.Globalization;
using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Presets;
using Aide.Domain.Review;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-123, FR-124, FR-126, FR-128: the composition text the Spec Generator, Plan Generator, Code Review, Repository Analyst
/// and refinement agents get (the composition equivalent of v2's <see cref="PresetGuidance"/>), as the value of their
/// templates' stack placeholder in the system prompt (the composition is the platform's trusted content, not input data).
/// Every block starts with the rendered composition summary (<see cref="StackResolution.Summary"/>, whose heading is
/// <see cref="Heading"/>). The sub-headings reuse <see cref="PresetGuidance"/>'s, so one reader (the Fake provider's) finds
/// the required sections, the agent catalogue and the checklist in either.
/// </summary>
public static class StackGuidance
{
    /// <summary>The heading of the composition summary (the first line of every block).</summary>
    public const string Heading = "## Stack composition";

    /// <summary>The subheading of the required extra spec sections (one <c>####</c> heading per section below it).</summary>
    public const string SpecSectionsHeading = PresetGuidance.SpecSectionsHeading;

    /// <summary>The subheading of the specialist catalogue (one line per agent: <c>- `key` (Name): description. Owns: …</c>).</summary>
    public const string AgentCatalogueHeading = PresetGuidance.AgentCatalogueHeading;

    /// <summary>The subheading of the review checklist (one line per item: <c>- [C1] (category, severity) item</c>).</summary>
    public const string ChecklistHeading = PresetGuidance.ChecklistHeading;

    /// <summary>The subheading of the contract files a step changing the API must declare (one <c>- `path`</c> line each).</summary>
    public const string ContractFilesHeading = "### Contract files";

    /// <summary>The sentence the plan guidance has when the scaffold-first rule applies (the repository is empty).</summary>
    public const string RepoEmptyText = "**This repository is empty, so step 1 of this plan is assigned to the scaffolder.**";

    /// <summary>The sentence the plan guidance has when the repository already has code.</summary>
    public const string RepoHasCodeText = "This repository already has code, so the scaffolder doesn't run (except as the first step of a stack change).";

    /// <summary>The prefix of the plan guidance line naming the layers a stack change must plan for.</summary>
    public const string LayersChangedPrefix = "- Stack change: the plan includes at least one step for each changed layer's specialist: ";

    /// <summary>
    /// FR-123, FR-128: the required spec sections: the composition's (<see cref="StackResolution.SpecSections"/>, with "Stack
    /// Migration" for a stack change), plus "Stack Migration" when the imported code doesn't match the composition
    /// (<paramref name="mismatches"/>). Deduplicated by title, ignoring case.
    /// </summary>
    public static IReadOnlyList<StackSpecSection> SpecSections(StackResolution resolution, IReadOnlyList<StackMismatch>? mismatches = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var sections = resolution.SpecSections.ToList();
        if (mismatches is { Count: > 0 } && !sections.Any(s => s.Title.Equals(StackCoreSpecSections.StackMigrationTitle, StringComparison.OrdinalIgnoreCase)))
        {
            sections.Add(new StackSpecSection(StackCoreSpecSections.StackMigrationTitle, StackCoreSpecSections.StackMigrationGuidance, StackCoreSpecSections.Source));
        }

        return sections;
    }

    /// <summary>
    /// FR-123: the composition summary, then the required extra sections with their guidance (each a <c>##</c> section of
    /// the spec, after the eleven, before "Changes in this version"), and (FR-128) the imported code's mismatches.
    /// </summary>
    public static string Spec(StackResolution resolution, IReadOnlyList<StackMismatch>? mismatches = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var sb = new StringBuilder(resolution.Summary.TrimEnd());
        var sections = SpecSections(resolution, mismatches);
        if (sections.Count > 0)
        {
            sb.Append("\n\n").Append(SpecSectionsHeading).Append("\n\n")
                .Append("This composition requires these extra top-level sections. Write each as a `##` heading with exactly this ")
                .Append("title, after the eleven numbered sections (and before `## Changes in this version` in iterations); the ")
                .Append("platform rejects a specification that misses one.");
            foreach (var section in sections)
            {
                sb.Append("\n\n#### ").Append(section.Title).Append("\n\n").Append(section.Guidance);
            }
        }

        Mismatches(sb, mismatches, "The spec's \"Stack Migration\" section says how the code moves to the composition.");
        sb.Append("\n\nThe specification's Technical Architecture, dependencies and API contracts follow this composition; it ")
            .Append("replaces \"choose a concrete stack\".");
        return sb.ToString();
    }

    /// <summary>
    /// FR-124: the composition summary, the specialist catalogue (key, name, description, owned paths) and the composed plan
    /// rules. <paramref name="repoEmpty"/> says whether the scaffold-first rule applies to this plan.
    /// </summary>
    public static string Plan(StackResolution resolution, bool repoEmpty)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var sb = new StringBuilder(resolution.Summary.TrimEnd());
        sb.Append("\n\nEvery step is run by one specialist of this composition. Set each step's `\"agent\"` to one of these keys ")
            .Append("(never `generalist`), and declare only files the agent owns where you can (a file outside them is shown to ")
            .Append("the reviewer as an `AGENT_SCOPE` warning).");
        Catalogue(sb, resolution);

        var rules = resolution.PlanRules;
        sb.Append("\n\n### Plan rules (violations reject the plan with `INVALID_PLAN`, unless marked as a recommendation)\n");
        sb.Append("\n- Scaffold first: on an empty repository step 1 is assigned `").Append(rules.Scaffolder)
            .Append("`, and the scaffolder runs no other step. ").Append(repoEmpty ? RepoEmptyText : RepoHasCodeText);
        foreach (var order in rules.Order)
        {
            sb.Append("\n- ").Append(order.Soft ? "Recommended order" : "Order").Append(": steps declaring ")
                .Append(Globs(order.Before.Patterns)).Append(" come before steps declaring ").Append(Globs(order.After.Patterns))
                .Append(" (`").Append(rules.Scaffolder).Append("` steps are exempt)")
                .Append(order.Reason is { Length: > 0 } reason ? $". {reason}" : ".");
        }

        foreach (var exclusive in rules.Exclusive)
        {
            sb.Append("\n- A `").Append(exclusive.Agent).Append("` step never declares ").Append(Globs(exclusive.NotWith.Patterns))
                .Append(exclusive.Reason is { Length: > 0 } reason ? $". {reason}" : ".");
        }

        foreach (var limit in rules.FileLimits)
        {
            sb.Append("\n- At most ").Append(limit.Max.ToString(CultureInfo.InvariantCulture)).Append(" file(s) matching `")
                .Append(limit.Glob.Pattern).Append("` per step.");
        }

        sb.Append("\n- Protected paths: a step may declare a CI file or `.env.example` only when its specialist owns that path. ")
            .Append("Lockfiles, `.env` files with secrets and other protected paths are never declared.");
        foreach (var path in resolution.ProtectedPaths)
        {
            sb.Append("\n- Protected by `").Append(path.Pack).Append("`: `").Append(path.Glob.Pattern).Append('`')
                .Append(path.Except.Count > 0 ? $" except {Globs(path.Except.Select(e => e.Pattern))}" : string.Empty)
                .Append(path.ExistingOnBase
                    ? " (files that already exist in the repository are immutable: never modify them, add a new file)."
                    : "; only the specialist that owns it may declare it.");
        }

        if (resolution.Change.IsStackChange)
        {
            var keys = resolution.Change.LayersChanged
                .Select(l => (Layer: l, Key: StackSpecialistCatalogue.ForLayer(l)))
                .Where(x => x.Key is not null && resolution.Specialist(x.Key) is not null)
                .Select(x => $"{x.Layer} → `{x.Key}`")
                .ToList();
            if (keys.Count > 0)
            {
                sb.Append('\n').Append(LayersChangedPrefix).Append(string.Join(", ", keys))
                    .Append(" (otherwise `STACK_CHANGE_UNPLANNED`). Step 1 may be assigned to the scaffolder.");
            }
        }

        sb.Append("\n- List the requirement ids each step implements in `covers`; new or changed requirements no step covers are ")
            .Append("shown as an `UNCOVERED_REQUIREMENTS` warning.");
        if (resolution.ContractFiles.Count > 0)
        {
            sb.Append("\n\n").Append(ContractFilesHeading).Append("\n\n")
                .Append("Agents can't run generators. A step that adds or changes endpoints (")
                .Append(Globs(resolution.ApiSurface.Patterns))
                .Append("), or a later step, declares the contract file and writes it in the generator's shape; otherwise the plan ")
                .Append("shows an `UNDECLARED_GENERATED_FILE` warning.\n");
            foreach (var file in resolution.ContractFiles)
            {
                sb.Append("\n- `").Append(file).Append('`');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// FR-126: the composition summary, the composed checklist numbered <c>C1</c>…, each with its category and severity, and the
    /// specialist catalogue for the optimisation plan's steps. A failed item is reported as a violation whose message starts
    /// with its <c>[Cn]</c> marker (<see cref="StackReviewChecklist.Apply"/> maps it to the item's category and severity).
    /// </summary>
    public static string Review(StackResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var sb = new StringBuilder(resolution.Summary.TrimEnd());
        var checklist = resolution.Checklist;
        if (checklist.Count > 0)
        {
            sb.Append("\n\n").Append(ChecklistHeading).Append("\n\n")
                .Append("Also score the code against this checklist, composed from the stack's packs. For every item the code ")
                .Append("fails, add a violation whose message starts with the item's marker (e.g. `[C1] `), with the item's ")
                .Append("category and severity. These violations inform your scores; they carry no extra penalty.\n");
            for (var i = 0; i < checklist.Count; i++)
            {
                var item = checklist[i];
                sb.Append("\n- ").Append(PresetReviewChecklist.Marker(i + 1)).Append(" (").Append(item.Category.ToWire()).Append(", ")
                    .Append(item.Severity.ToWire()).Append(") `").Append(item.Pack).Append("`: ").Append(item.Item);
            }
        }

        sb.Append("\n\nOptimisation plan steps may set `\"agent\"` to one of these specialist keys (default `generalist`):");
        Catalogue(sb, resolution);
        return sb.ToString();
    }

    /// <summary>
    /// FR-128: what the Repository Analyst must say in "Stack &amp; Dependencies" about the composition and, for imports, the
    /// detected packs and mismatches (<see cref="StackAnalysis"/> checks it). The detector's evidence goes in the user message
    /// (<see cref="DetectionInput"/>): it is read from the repository, so it is input data.
    /// </summary>
    public static string Analysis(StackResolution resolution, StackDetectionReport? detection)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var sb = new StringBuilder(resolution.Summary.TrimEnd());
        sb.Append("\n\nThis is the stack the Owner chose for the project.");
        if (detection is null)
        {
            sb.Append(" In \"Stack & Dependencies\", compare what you find with it.");
            return sb.ToString();
        }

        var expectation = StackAnalysis.Expectation(detection, resolution.Composition);
        sb.Append(" AIDE's stack detector also read the repository's manifests (its evidence is in `<stack_detection>`).");
        if (expectation.DetectedPacks.Count > 0)
        {
            sb.Append(" \"Stack & Dependencies\" must name each detected pack by its id: ")
                .Append(string.Join(", ", expectation.DetectedPacks.Select(p => $"`{p}`"))).Append('.');
        }

        Mismatches(sb, detection.Mismatches, "In \"Stack & Dependencies\", flag each one as a **mismatch**, naming both packs.");
        return sb.ToString();
    }

    /// <summary>FR-128: the composition for refinement, and (for imports) the detected packs and any mismatch.</summary>
    public static string Refinement(StackResolution resolution, StackDetectionReport? detection)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var sb = new StringBuilder(resolution.Summary.TrimEnd());
        sb.Append("\n\nThe project request describes work on this stack; ask about it only where the request and the stack disagree.");
        Mismatches(sb, detection?.Mismatches, "The project request says how the code moves to the composition (it becomes the spec's \"Stack Migration\").");
        return sb.ToString();
    }

    /// <summary>FR-118, FR-128: the detector's suggestions, conflicts and mismatches as input data (one line each), or <c>null</c>.</summary>
    public static string? DetectionInput(StackDetectionReport? detection)
    {
        if (detection is null)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var s in detection.Suggestions)
        {
            lines.Add($"- {s.Choice}: `{s.Option}`{(s.Path is null ? string.Empty : $" at `{s.Path}`")}{(s.Mode is null ? string.Empty : $" ({s.Mode})")}; evidence: {string.Join("; ", s.Evidence)}");
        }

        foreach (var c in detection.Conflicts)
        {
            lines.Add($"- {c.Choice}: conflicting matches {string.Join(", ", c.Candidates.Select(x => $"`{x.Option}` ({string.Join("; ", x.Evidence)})"))}");
        }

        foreach (var m in detection.Mismatches)
        {
            lines.Add($"- mismatch on {m.Layer}: detected `{m.Detected}`, the composition has `{m.Chosen}`");
        }

        return lines.Count == 0 ? "- nothing detected" : string.Join('\n', lines);
    }

    private static void Mismatches(StringBuilder sb, IReadOnlyList<StackMismatch>? mismatches, string instruction)
    {
        if (mismatches is not { Count: > 0 })
        {
            return;
        }

        sb.Append("\n\n### Detected stack mismatch\n\nThe imported code doesn't match the composition:\n");
        foreach (var m in mismatches)
        {
            sb.Append("\n- ").Append(m.Layer).Append(": the code is `").Append(m.Detected).Append("`, the composition has `").Append(m.Chosen).Append('`');
        }

        sb.Append("\n\n").Append(instruction);
    }

    private static void Catalogue(StringBuilder sb, StackResolution resolution)
    {
        sb.Append("\n\n").Append(AgentCatalogueHeading).Append('\n');
        foreach (var agent in resolution.Specialists)
        {
            sb.Append("\n- `").Append(agent.Key).Append("` (").Append(agent.Name).Append("): ").Append(agent.Description.TrimEnd('.'))
                .Append(". Owns: ").Append(Globs(agent.OwnedGlobs)).Append('.');
        }
    }

    private static string Globs(IEnumerable<string> patterns) => string.Join(", ", patterns.Select(p => $"`{p}`"));
}
