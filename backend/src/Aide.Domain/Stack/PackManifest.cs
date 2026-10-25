using Aide.Domain.Review;

namespace Aide.Domain.Stack;

/// <summary>
/// §4.16: a pack's <c>pack.json</c>, validated. Fields consumed by later milestones (spec sections, ownership, agent
/// sections, plan rules, protected paths, generated files, env, review, gate, migrate) are parsed and validated now;
/// globs keep their <c>{fe}</c>/<c>{be}</c>/<c>{app}</c> placeholders (expanded with the composition's layer paths).
/// </summary>
public sealed record PackManifest
{
    /// <summary>The pack id: the folder name and the stack-packs plugin name.</summary>
    public required string Id { get; init; }

    /// <summary>One of <see cref="StackLayers.PackLayers"/>.</summary>
    public required string Layer { get; init; }

    public required string Name { get; init; }

    /// <summary><c>&lt;plugin version&gt;+aide.&lt;n&gt;</c>.</summary>
    public required string Version { get; init; }

    public required PackSource Source { get; init; }

    public IReadOnlyList<string> Chips { get; init; } = [];

    public required string Description { get; init; }

    /// <summary><c>frontend</c>, <c>backend</c>, <c>.</c> or <c>null</c>.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>Frontend packs: <c>NEXT_PUBLIC_</c>, <c>VITE_</c> or <c>null</c>.</summary>
    public string? PublicEnvPrefix { get; init; }

    /// <summary>Frontend and backend packs: one of <see cref="PackVocabulary.Runtimes"/>.</summary>
    public string? Runtime { get; init; }

    public IReadOnlyList<PackMode> Modes { get; init; } = [];

    public IReadOnlyList<PackSpecSection> SpecSections { get; init; } = [];

    /// <summary>Agent key → owned globs.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Ownership { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Agent key → <c>*</c> or skill section headings (matched as in <see cref="SkillSections"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AgentSections { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public PackPlanRules PlanRules { get; init; } = PackPlanRules.Empty;

    public IReadOnlyList<PackProtectedPath> ProtectedPaths { get; init; } = [];

    public IReadOnlyList<PackGeneratedFile> GeneratedFiles { get; init; } = [];

    public IReadOnlyList<PackEnvVar> RequiredEnv { get; init; } = [];

    public PackReview Review { get; init; } = PackReview.Empty;

    /// <summary>Frontend and backend packs: the Gate commands (for the ci-engineer and the composition summary).</summary>
    public IReadOnlyList<string>? Gate { get; init; }

    /// <summary>Data packs: the one-off migrate command.</summary>
    public PackMigrate? Migrate { get; init; }

    /// <summary>Deploy packs: the provider and the shapes it can deploy.</summary>
    public PackDeploy? Deploy { get; init; }
}

/// <summary>Where the pack was adapted from (FR-122): the stack-packs plugin, its version and the snapshot date.</summary>
public sealed record PackSource(string Plugin, string Version, string Snapshot);

public sealed record PackMode(string Id, string Name, string Description);

/// <summary>A required spec section with guidance; <see cref="When"/> is a <see cref="StackWhen"/> condition.</summary>
public sealed record PackSpecSection(string Title, string Guidance, string? When);

public sealed record PackPlanRules(IReadOnlyList<PackOrderRule> Order, IReadOnlyList<PackExclusiveRule> Exclusive, IReadOnlyList<PackFileLimit> MaxFilesPerStep)
{
    public static PackPlanRules Empty { get; } = new([], [], []);
}

public sealed record PackOrderRule(IReadOnlyList<string> Before, IReadOnlyList<string> After, bool Soft, string? Reason);

public sealed record PackExclusiveRule(string Agent, IReadOnlyList<string> NotWithGlobs, string? Reason);

public sealed record PackFileLimit(string Glob, int Max);

public sealed record PackProtectedPath(string Glob, bool ExistingOnBase, IReadOnlyList<string> Except, string Reason);

/// <summary>FR-127: a generated file and how it is handled (<see cref="PackVocabulary.GeneratedFileHandling"/>).</summary>
public sealed record PackGeneratedFile(string Glob, string Handling, string? CiCheck);

/// <summary>A required environment variable (§4.22): components it applies to, targets, and whether it is sensitive or public.</summary>
public sealed record PackEnvVar(
    string Key,
    IReadOnlyList<string> AppliesTo,
    IReadOnlyList<string> Targets,
    bool Sensitive,
    bool Public,
    string Description,
    string? When);

public sealed record PackReview(IReadOnlyList<PackChecklistItem> Checklist, IReadOnlyList<string> Deterministic)
{
    public static PackReview Empty { get; } = new([], []);
}

public sealed record PackChecklistItem(string Item, ReviewCategory Category, Severity Severity);

public sealed record PackMigrate(string Command, bool NeedsDirectUrl);

/// <summary>§4.17 R6–R8: a deploy pack's provider id and the shapes it deploys.</summary>
public sealed record PackDeploy(string Provider, IReadOnlyList<PackDeployShape> Shapes);

/// <summary>A deploy shape: the component it runs (<see cref="PackVocabulary.Components"/>) and the runtimes it supports.</summary>
public sealed record PackDeployShape(string Id, string Component, IReadOnlyList<string> Runtimes);
