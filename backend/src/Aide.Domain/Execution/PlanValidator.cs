using Aide.Domain.Common;
using Aide.Domain.Presets;
using Aide.Domain.Specs;

namespace Aide.Domain.Execution;

/// <summary>A file a plan step declares it will create or modify.</summary>
public sealed record DeclaredFile(string Path, FileAction Action);

/// <summary>
/// One step of a structured plan (FR-29, D6). <see cref="Agent"/> (FR-76) is the agent that runs the step: a preset
/// catalogue key, or <see cref="PlanAgents.Generalist"/> without a preset (validation fills it in when omitted).
/// <see cref="Covers"/> (FR-76, §4.13) lists the requirement IDs (<c>FR-n</c>) the step implements.
/// </summary>
public sealed record PlanStep(
    int Number,
    string Title,
    string Task,
    IReadOnlyList<DeclaredFile> Files,
    IReadOnlyList<int> Dependencies,
    string? UserInstructions = null,
    string? Agent = null,
    IReadOnlyList<string>? Covers = null)
{
    public IReadOnlyList<string> Covers { get; init; } = Covers ?? [];
}

/// <summary>
/// FR-76: the agents a plan step may name. Without a preset only <see cref="Generalist"/> (the default when a step names
/// none, so v1 plans stay valid). With a preset (<see cref="For"/>) the catalogue keys, the preset id and the preset
/// itself, whose plan rules <see cref="PlanValidator.Validate"/> then checks too (<see cref="PresetPlanValidator"/>);
/// <see cref="RepoEmpty"/> feeds the scaffold-first rule.
/// </summary>
public sealed record PlanAgents(IReadOnlyCollection<string> Keys, string? PresetId = null)
{
    /// <summary>The agent of every step without a preset.</summary>
    public const string Generalist = "generalist";

    /// <summary>No preset: every step is <see cref="Generalist"/>.</summary>
    public static PlanAgents None { get; } = new([Generalist]);

    /// <summary>The preset whose plan rules apply (§4.11), or <c>null</c>.</summary>
    public Preset? Preset { get; init; }

    /// <summary>Whether the repository has no files yet (an empty base): <c>planRules.scaffoldFirstWhenEmpty</c> applies.</summary>
    public bool RepoEmpty { get; init; }

    /// <summary>
    /// The files on the version's base commit, when known (compared ignoring case): a preset's <c>existingOnBase</c>
    /// protected paths (immutable migrations) are checked against it. <c>null</c> (unknown): a generated plan may not
    /// declare any path under such an entry (it can't be proven new); an Owner-edited plan is checked when it is known.
    /// </summary>
    public IReadOnlySet<string>? BaseFiles { get; init; }

    /// <summary>Whether the preset's plan rules (<see cref="PresetPlanValidator.Errors"/>) apply; off for optimisation plans.</summary>
    public bool ApplyPlanRules { get; init; } = true;

    /// <summary>Whether <see cref="Generalist"/> is allowed (and the default) next to a preset's catalogue (optimisation plans).</summary>
    public bool GeneralistAllowed { get; init; }

    /// <summary>FR-76: the agents of <paramref name="preset"/> (or <see cref="None"/> without one).</summary>
    public static PlanAgents For(Preset? preset, bool repoEmpty = false, IReadOnlySet<string>? baseFiles = null) =>
        preset is null ? None : new PlanAgents(preset.AgentKeys, preset.Id) { Preset = preset, RepoEmpty = repoEmpty, BaseFiles = IgnoringCase(baseFiles) };

    /// <summary>
    /// FR-38, FR-76: the agents of a review's optimisation plan. With a preset: its catalogue keys or <see cref="Generalist"/>
    /// (the default when a step names none). The preset's plan rules don't apply (fix steps follow the review, not the
    /// build order); its protected paths and the ownership exception do.
    /// </summary>
    public static PlanAgents ForOptimization(Preset? preset, IReadOnlySet<string>? baseFiles = null) =>
        preset is null
            ? None
            : new PlanAgents([.. preset.AgentKeys, Generalist], preset.Id) { Preset = preset, BaseFiles = IgnoringCase(baseFiles), ApplyPlanRules = false, GeneralistAllowed = true };

    private static HashSet<string>? IgnoringCase(IReadOnlySet<string>? files) =>
        files is null ? null : new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="key"/> is allowed (ordinal; keys are lowercase kebab-case).</summary>
    public bool Allows(string key) => Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>The agent of a step that names none: <see cref="Generalist"/> without a preset (or when allowed); with one, none (an error).</summary>
    public string? DefaultAgent => PresetId is null || GeneralistAllowed ? Generalist : null;
}

/// <summary>
/// FR-29: structural validation of a plan (generated, or edited by a human, FR-18). Failures are <c>INVALID_PLAN</c>
/// with every reason in the details. The rules: steps numbered 1..n in order (at most <see cref="MaxSteps"/>), each with
/// a title, a task and at least one declared file (at most <c>maxFilesPerStep</c> when given); dependencies point to
/// earlier steps, once each; paths are safe (<see cref="PathRules.IsUnsafe"/>: no escape, no <c>.git</c>, no control
/// characters, line breaks or backticks) and declared once per step; a file is created by one step only, and a step that
/// modifies a file created in the plan comes after, and depends (directly or indirectly) on, the step that creates it.
/// <para>
/// Protected paths (<see cref="PathRules.IsProtected"/>) depend on who wrote the plan: an Owner-edited plan may declare
/// them (§4.6 lets a declared step write one), a generated plan may not (<c>allowProtectedPaths: false</c>), so a
/// prompt-injected agent can't plan its way into CI workflows, lockfiles or env files. No plan may declare a path under
/// <c>.aide/</c> (<see cref="PathRules.IsAgentForbidden"/>): the Guardian rejects agent output there even when declared.
/// </para>
/// <para>
/// FR-76, §4.11: a step's <c>agent</c> must be allowed by <see cref="PlanAgents"/> (without a preset: omitted, which
/// becomes <c>generalist</c>, or <c>generalist</c>); its <c>covers</c> must be requirement IDs (<c>FR-n</c>, normalised
/// and de-duplicated). §4.13: steps may modify files that already exist in the repository, and "a file is created by one
/// step only" applies within the plan. With a preset, its plan rules follow the v1 rules (<see cref="PresetPlanValidator.Errors"/>);
/// its warnings (<c>AGENT_SCOPE</c>, <c>PLAN_ORDER</c>) come from <see cref="PresetPlanValidator.Warnings"/>.
/// </para>
/// </summary>
public static class PlanValidator
{
    /// <summary>The most steps a plan may have.</summary>
    public const int MaxSteps = 100;

    /// <summary>The longest step title, in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Validates and returns the plan with declared paths normalised.</summary>
    public static Result<IReadOnlyList<PlanStep>> Validate(
        IReadOnlyList<PlanStep> steps, int? maxFilesPerStep = null, bool allowProtectedPaths = true, PlanAgents? agents = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        agents ??= PlanAgents.None;
        var reasons = new List<string>();

        if (steps.Count == 0)
        {
            reasons.Add("The plan has no steps.");
        }

        if (steps.Count > MaxSteps)
        {
            reasons.Add($"The plan has {steps.Count} steps; at most {MaxSteps} are allowed.");
        }

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var normalised = new List<PlanStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var expected = i + 1;
            if (step.Number != expected)
            {
                reasons.Add($"Steps must be numbered 1..n in order: position {expected} has number {step.Number}.");
            }

            if (string.IsNullOrWhiteSpace(step.Title))
            {
                reasons.Add($"Step {step.Number} has no title.");
            }
            else if (step.Title.Trim().Length > MaxTitleLength)
            {
                reasons.Add($"Step {step.Number} has a title longer than {MaxTitleLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(step.Task))
            {
                reasons.Add($"Step {step.Number} has no task.");
            }

            foreach (var dep in step.Dependencies)
            {
                if (dep >= step.Number || dep < 1)
                {
                    reasons.Add($"Step {step.Number} depends on step {dep}; dependencies must point to earlier steps.");
                }
            }

            if (step.Dependencies.Distinct().Count() != step.Dependencies.Count)
            {
                reasons.Add($"Step {step.Number} lists a dependency more than once.");
            }

            if (step.Files.Count == 0)
            {
                reasons.Add($"Step {step.Number} declares no files; every step must create or modify at least one file.");
            }

            if (maxFilesPerStep is { } max && step.Files.Count > max)
            {
                reasons.Add($"Step {step.Number} declares {step.Files.Count} files; at most {max} are allowed per step.");
            }

            var agent = string.IsNullOrWhiteSpace(step.Agent) ? agents.DefaultAgent : step.Agent.Trim();
            var files = new List<DeclaredFile>(step.Files.Count);
            var seenInStep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in step.Files)
            {
                var path = PathRules.Normalize(file.Path);
                if (PathRules.HasUnsafeCharacters(path))
                {
                    reasons.Add($"Step {step.Number} declares an invalid path '{Display(path)}': paths may not contain control characters, line breaks or backticks.");
                    continue;
                }

                if (PathRules.IsUnsafe(path))
                {
                    reasons.Add($"Step {step.Number} declares an invalid path '{file.Path}'.");
                    continue;
                }

                // §4.6, §4.11: a generated plan declares no protected path, except a preset specialist's own (PresetPathRules);
                // an Owner-edited one may declare v1-protected paths, but never an immutable file that exists on the base commit.
                var refusal = allowProtectedPaths
                    ? PresetPathRules.ImmutableRefusal(agents.Preset, path, agents.BaseFiles?.Contains(path) ?? false)
                    : PresetPathRules.GeneratedPlanRefusal(agents.Preset, agent, path, agents.BaseFiles?.Contains(path));
                if (refusal is not null)
                {
                    reasons.Add($"Step {step.Number} {refusal}");
                    continue;
                }

                if (PathRules.IsAgentForbidden(path))
                {
                    reasons.Add($"Step {step.Number} declares '{path}' under .aide/, which only humans edit; agents never write it.");
                    continue;
                }

                if (!seenInStep.Add(path))
                {
                    reasons.Add($"Step {step.Number} declares '{path}' more than once.");
                    continue;
                }

                if (file.Action == FileAction.Create)
                {
                    if (created.TryGetValue(path, out var firstStep))
                    {
                        reasons.Add($"'{path}' is declared as created by step {firstStep} and again by step {step.Number}.");
                    }
                    else
                    {
                        created[path] = step.Number;
                    }
                }

                files.Add(file with { Path = path });
            }

            if (agent is null)
            {
                reasons.Add($"Step {step.Number} has no \"agent\"; use one of {string.Join(", ", agents.Keys)}.");
            }
            else if (!agents.Allows(agent))
            {
                reasons.Add(agents.PresetId is null
                    ? $"Step {step.Number} names the agent '{agent}'; without a preset every step is \"{PlanAgents.Generalist}\"."
                    : $"Step {step.Number} names the unknown agent '{agent}'; the {agents.PresetId} preset has {string.Join(", ", agents.Keys)}.");
            }

            var covers = new List<string>(step.Covers.Count);
            foreach (var id in step.Covers)
            {
                if (RequirementParser.NormaliseId(id) is not { } normalisedId)
                {
                    reasons.Add($"Step {step.Number} covers '{id}', which is not a requirement ID (FR-n).");
                }
                else if (!covers.Contains(normalisedId, StringComparer.Ordinal))
                {
                    covers.Add(normalisedId);
                }
            }

            normalised.Add(step with { Title = step.Title.Trim(), Task = step.Task.Trim(), Files = files, Agent = agent, Covers = covers });
        }

        reasons.AddRange(ModifyOrderReasons(normalised, created));
        if (agents.Preset is { } preset && agents.ApplyPlanRules)
        {
            reasons.AddRange(PresetPlanValidator.Errors(normalised, preset, agents.RepoEmpty));
        }

        return reasons.Count == 0
            ? Result.Success<IReadOnlyList<PlanStep>>(normalised)
            : Result.Failure<IReadOnlyList<PlanStep>>(new DomainError(ErrorCodes.InvalidPlan, $"The plan is invalid: {reasons[0]}") { Details = reasons });
    }

    /// <summary>The path with control characters shown as <c>\uXXXX</c>, so a reason stays on one line.</summary>
    private static string Display(string path) =>
        string.Concat(path.Select(c => char.IsControl(c) ? "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : c.ToString()));

    /// <summary>A step that modifies a file created in the plan must come after, and depend on, the step creating it.</summary>
    private static IEnumerable<string> ModifyOrderReasons(IReadOnlyList<PlanStep> steps, Dictionary<string, int> created)
    {
        // Transitive dependencies, built in order (dependencies only point to earlier steps once the plan is valid).
        var closure = new Dictionary<int, HashSet<int>>();
        foreach (var step in steps)
        {
            var all = new HashSet<int>();
            foreach (var dep in step.Dependencies.Where(d => d >= 1 && d < step.Number))
            {
                all.Add(dep);
                if (closure.TryGetValue(dep, out var inherited))
                {
                    all.UnionWith(inherited);
                }
            }

            closure.TryAdd(step.Number, all);
            foreach (var file in step.Files.Where(f => f.Action == FileAction.Modify))
            {
                if (!created.TryGetValue(file.Path, out var creator) || creator == step.Number)
                {
                    continue;
                }

                if (creator > step.Number)
                {
                    yield return $"Step {step.Number} modifies '{file.Path}' before step {creator} creates it.";
                }
                else if (!all.Contains(creator))
                {
                    yield return $"Step {step.Number} modifies '{file.Path}', created by step {creator}, but doesn't depend on step {creator}.";
                }
            }
        }
    }
}
