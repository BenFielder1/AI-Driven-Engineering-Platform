using System.Globalization;
using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Review;

namespace Aide.Domain.Presets;

/// <summary>
/// FR-75–FR-77: the preset text the Spec Generator, Plan Generator and Code Review agents get, as the value of their
/// templates' <c>{{PRESET_GUIDANCE?}}</c> placeholder (in the system prompt: the preset is shipped, trusted content, not
/// input data). Without a preset the value is <c>null</c> and the prompts are v1's. Every block starts with
/// <see cref="Heading"/> and names the preset as <c>`id@version`</c> (<see cref="Preset.RunLabel"/>).
/// </summary>
public static class PresetGuidance
{
    /// <summary>The heading every preset block starts with.</summary>
    public const string Heading = "## Stack preset";

    /// <summary>The subheading of the extra spec sections (one <c>####</c> heading per section below it).</summary>
    public const string SpecSectionsHeading = "### Required extra sections";

    /// <summary>The subheading of the agent catalogue (one line per agent: <c>- `key` (Name): description. Owns: …</c>).</summary>
    public const string AgentCatalogueHeading = "### Agent catalogue";

    /// <summary>The subheading of the review checklist (one line per item: <c>- [C1] (category, severity) item</c>).</summary>
    public const string ChecklistHeading = "### Review checklist";

    /// <summary>
    /// FR-75: the stack description, the required extra sections with their guidance (each a <c>##</c> section of the spec,
    /// after the eleven, before "Changes in this version") and the stack conventions.
    /// </summary>
    public static string? Spec(Preset? preset)
    {
        if (preset is null)
        {
            return null;
        }

        var sb = Header(preset);
        if (preset.SpecSections.Count > 0)
        {
            sb.Append("\n\n").Append(SpecSectionsHeading).Append("\n\n")
                .Append("This preset requires these extra top-level sections. Write each as a `##` heading with exactly this title, ")
                .Append("after the eleven numbered sections (and before `## Changes in this version` in iterations); the platform ")
                .Append("rejects a specification that misses one.");
            foreach (var section in preset.SpecSections)
            {
                sb.Append("\n\n#### ").Append(section.Title).Append("\n\n").Append(section.Guidance);
            }
        }

        Conventions(sb, preset);
        return sb.ToString();
    }

    /// <summary>
    /// FR-76: the agent catalogue (key, name, description, owned paths) and the plan rules. <paramref name="repoEmpty"/> says
    /// whether the scaffold-first rule applies to this plan.
    /// </summary>
    public static string? Plan(Preset? preset, bool repoEmpty)
    {
        if (preset is null)
        {
            return null;
        }

        var sb = Header(preset);
        sb.Append("\n\nEvery step is run by one specialist agent of this preset. Set each step's `\"agent\"` to one of these keys ")
            .Append("(never `generalist`), and declare only files the agent owns where you can (a file outside them is shown to ")
            .Append("the reviewer as an `AGENT_SCOPE` warning).");
        Catalogue(sb, preset);

        var rules = preset.PlanRules;
        sb.Append("\n\n### Plan rules (violations reject the plan with `INVALID_PLAN`, unless marked as a recommendation)\n");
        if (rules.ScaffoldFirstWhenEmpty)
        {
            sb.Append("\n- Scaffold first: on an empty repository step 1 is assigned `").Append(rules.Scaffolder).Append("`. ")
                .Append(repoEmpty ? "**This repository is empty, so this applies to this plan.**" : "This repository already has code, so it doesn't apply to this plan.");
        }

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

        sb.Append("\n- Protected paths: a step may declare a CI workflow (`.github/workflows/**`) or `.env.example` only when its ")
            .Append("agent owns that path. Lockfiles, `.env` files with secrets and other protected paths are never declared.");
        foreach (var path in preset.ProtectedPaths)
        {
            sb.Append("\n- Protected by this preset: `").Append(path.Glob.Pattern).Append('`')
                .Append(path.ExistingOnBase ? " (files that already exist in the repository: never modify them, add a new file)" : string.Empty)
                .Append(path.Except.Count > 0 ? $" except {Globs(path.Except.Select(e => e.Pattern))}" : string.Empty)
                .Append(path.ExistingOnBase ? "." : "; only the agent that owns it may declare it.");
        }

        sb.Append("\n- List the requirement ids each step implements in `covers`; new or changed requirements no step covers are ")
            .Append("shown as an `UNCOVERED_REQUIREMENTS` warning.");
        Conventions(sb, preset);
        return sb.ToString();
    }

    /// <summary>
    /// FR-77: the review checklist, numbered <c>C1</c>…, each with its category and severity, and the agent catalogue for the
    /// optimisation plan's steps. A failed item is reported as a violation whose message starts with its <c>[Cn]</c> marker
    /// (<see cref="PresetReviewChecklist.Apply"/> maps it to the item's category and severity).
    /// </summary>
    public static string? Review(Preset? preset)
    {
        if (preset is null)
        {
            return null;
        }

        var sb = Header(preset);
        var checklist = preset.Review.Checklist;
        if (checklist.Count > 0)
        {
            sb.Append("\n\n").Append(ChecklistHeading).Append("\n\n")
                .Append("Also score the code against this checklist. For every item the code fails, add a violation whose message ")
                .Append("starts with the item's marker (e.g. `[C1] `), with the item's category and severity. These violations ")
                .Append("inform your scores; they carry no extra penalty.\n");
            for (var i = 0; i < checklist.Count; i++)
            {
                var item = checklist[i];
                sb.Append("\n- ").Append(PresetReviewChecklist.Marker(i + 1)).Append(" (").Append(item.Category.ToWire()).Append(", ")
                    .Append(item.Severity.ToWire()).Append(") ").Append(item.Item);
            }
        }

        sb.Append("\n\nOptimisation plan steps may set `\"agent\"` to one of these specialist keys (default `generalist`):");
        Catalogue(sb, preset);
        Conventions(sb, preset);
        return sb.ToString();
    }

    private static StringBuilder Header(Preset preset)
    {
        var sb = new StringBuilder();
        sb.Append(Heading).Append(": ").Append(preset.Name).Append(" (`").Append(preset.RunLabel).Append("`)\n\n")
            .Append(preset.Description).Append("\n\nStack: ").Append(string.Join(", ", preset.Stack)).Append('.');
        return sb;
    }

    private static void Catalogue(StringBuilder sb, Preset preset)
    {
        sb.Append("\n\n").Append(AgentCatalogueHeading).Append('\n');
        foreach (var agent in preset.Agents)
        {
            sb.Append("\n- `").Append(agent.Key).Append("` (").Append(agent.Name).Append("): ").Append(agent.Description.TrimEnd('.'))
                .Append(". Owns: ").Append(Globs(agent.Owns.Patterns)).Append('.');
        }
    }

    private static void Conventions(StringBuilder sb, Preset preset)
    {
        if (preset.SpecConventions.Length > 0)
        {
            sb.Append("\n\n### Stack conventions\n\n").Append(preset.SpecConventions);
        }
    }

    private static string Globs(IEnumerable<string> patterns) => string.Join(", ", patterns.Select(p => $"`{p}`"));
}
