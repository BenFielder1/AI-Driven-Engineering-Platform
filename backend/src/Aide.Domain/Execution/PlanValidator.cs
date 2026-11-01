using Aide.Domain.Common;
using Aide.Domain.Specs;
using Aide.Domain.Stack;

namespace Aide.Domain.Execution;

/// <summary>A file a plan step declares it will create or modify.</summary>
public sealed record DeclaredFile(string Path, FileAction Action);

/// <summary>
/// One step of a structured plan (FR-29, D6). <see cref="Agent"/> (FR-76, FR-124) is the agent that runs the step: a
/// specialist of the composition, or <see cref="PlanAgents.Generalist"/> without a stack (validation fills it in when
/// omitted). <see cref="Covers"/> (FR-76, §4.13) lists the requirement IDs (<c>FR-n</c>) the step implements.
/// <see cref="LegacyAgent"/> (§4.23): the v2 preset key a migrated plan named, which validation mapped to <see cref="Agent"/>.
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

    /// <summary>§4.23: the v2 preset agent key the step was planned with (mapped to <see cref="Agent"/>), or <c>null</c>.</summary>
    public string? LegacyAgent { get; init; }
}

/// <summary>
/// FR-76, FR-124: the agents a plan step may name. Two modes (the v2 presets were retired by FR-129):
/// <list type="bullet">
/// <item>No stack ("No stack (general)", v1): only <see cref="Generalist"/> (the default when a step names none, so v1
/// plans stay valid).</item>
/// <item>A stack composition (<see cref="ForStack"/>): the composition's specialists (FR-120) and its resolution, whose
/// composed plan rules <see cref="PlanValidator.Validate"/> checks (<see cref="CompositionPlanValidator"/>); never
/// <see cref="Generalist"/> except in optimisation plans.</item>
/// </list>
/// <see cref="RepoEmpty"/> feeds the scaffold-first rule. §4.23: with <see cref="Legacy"/> (a version migrated from a v2
/// preset), a step may also name a v2 agent key, which validation maps to its v4 specialist (<see cref="PlanStep.LegacyAgent"/>
/// keeps the v2 key).
/// </summary>
public sealed record PlanAgents(IReadOnlyCollection<string> Keys)
{
    /// <summary>The agent of every step without a stack.</summary>
    public const string Generalist = "generalist";

    /// <summary>No stack: every step is <see cref="Generalist"/>.</summary>
    public static PlanAgents None { get; } = new([Generalist]);

    /// <summary>FR-124: the resolved composition whose plan rules apply (§4.18), or <c>null</c>.</summary>
    public StackResolution? Stack { get; init; }

    /// <summary>Whether the repository has no files yet (an empty base): <c>planRules.scaffoldFirstWhenEmpty</c> applies.</summary>
    public bool RepoEmpty { get; init; }

    /// <summary>
    /// The files on the version's base commit, when known (compared ignoring case): the packs' <c>existingOnBase</c>
    /// protected paths (immutable migrations) are checked against it. <c>null</c> (unknown): a generated plan may not
    /// declare any path under such an entry (it can't be proven new); an Owner-edited plan is checked when it is known.
    /// </summary>
    public IReadOnlySet<string>? BaseFiles { get; init; }

    /// <summary>Whether the composed plan rules (<see cref="CompositionPlanValidator.Errors"/>) apply; off for optimisation plans.</summary>
    public bool ApplyPlanRules { get; init; } = true;

    /// <summary>Whether <see cref="Generalist"/> is allowed (and the default) next to the specialists (optimisation plans).</summary>
    public bool GeneralistAllowed { get; init; }

    /// <summary>§4.23: the v2 agent-key map and the frozen preset of a migrated version, or <c>null</c>.</summary>
    public LegacyPlanAgents? Legacy { get; init; }

    /// <summary>FR-124: the specialists of a resolved composition (never <see cref="Generalist"/>) and its plan rules.</summary>
    public static PlanAgents ForStack(StackResolution stack, bool repoEmpty = false, IReadOnlySet<string>? baseFiles = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return new PlanAgents(stack.AgentKeys) { Stack = stack, RepoEmpty = repoEmpty, BaseFiles = IgnoringCase(baseFiles) };
    }

    /// <summary>
    /// FR-38, FR-124: the agents of a composed project's optimisation plan: its specialists or <see cref="Generalist"/> (the
    /// default). The composed plan rules don't apply (fix steps follow the review); its protected paths and the ownership
    /// exception do.
    /// </summary>
    public static PlanAgents ForStackOptimization(StackResolution stack, IReadOnlySet<string>? baseFiles = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return new PlanAgents([.. stack.AgentKeys, Generalist]) { Stack = stack, BaseFiles = IgnoringCase(baseFiles), ApplyPlanRules = false, GeneralistAllowed = true };
    }

    private static HashSet<string>? IgnoringCase(IReadOnlySet<string>? files) =>
        files is null ? null : new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="key"/> is allowed (ordinal; keys are lowercase kebab-case).</summary>
    public bool Allows(string key) => Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// The agent of a step that names none: <see cref="Generalist"/> without a composition (or when allowed); with one,
    /// none (an error).
    /// </summary>
    public string? DefaultAgent => Stack is null || GeneralistAllowed ? Generalist : null;
}

/// <summary>§4.23: what a migrated version's plans may still name: the v2 agent-key map and the frozen preset's globs.</summary>
public sealed record LegacyPlanAgents(LegacyPresets Map, LegacyPreset? Preset);

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
/// FR-76, FR-124: a step's <c>agent</c> must be allowed by <see cref="PlanAgents"/> (without a stack: omitted, which
/// becomes <c>generalist</c>, or <c>generalist</c>; §4.23: a migrated version's v2 keys are mapped); its <c>covers</c> must be requirement IDs (<c>FR-n</c>, normalised
/// and de-duplicated). §4.13: steps may modify files that already exist in the repository, and "a file is created by one
/// step only" applies within the plan. With a composition, its plan rules follow the v1 rules
/// (<see cref="CompositionPlanValidator.Errors"/>); its warnings come from <see cref="CompositionPlanValidator.Warnings"/>.
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

        // §4.23: a plan of a migrated version naming any v2-only key is a v2 plan: every v2 key in it is mapped (test-engineer
        // too, whose key didn't change), so its steps keep the frozen v2 globs for owned-path checks.
        var legacyPlan = agents.Legacy is { } legacyMap
            && steps.Any(s => s.Agent?.Trim() is { Length: > 0 } key && legacyMap.Map.IsLegacyKey(key) && !agents.Allows(key));
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

            // §4.23: a migrated plan's v2 key runs as its mapped specialist (decided by the declared files for devops-engineer).
            var legacy = legacyPlan && agent is not null && agents.Legacy is { } legacyAgents
                ? legacyAgents.Map.Map(agent, step.Files.Select(f => f.Path))
                : null;
            agent = legacy?.AgentKey ?? agent;
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

                // §4.6, FR-125: a generated plan declares no protected path, except a specialist's own (StackPathRules); an
                // Owner-edited one may declare v1-protected paths, but never an immutable file that exists on the base commit
                // (with a composition, every data pack's migrations).
                var existsOnBase = agents.BaseFiles?.Contains(path);
                var refusal = agents.Stack is { } stack
                    ? allowProtectedPaths
                        ? StackPathRules.ImmutableRefusal(stack, path, existsOnBase ?? false)
                        : StackPathRules.GeneratedPlanRefusal(stack, agent, path, existsOnBase)
                    : allowProtectedPaths || !PathRules.IsProtected(path)
                        ? null
                        : $"declares the protected path '{path}'; a generated plan may not create or modify protected paths (.git/, .aide/, CI workflows, lockfiles, .env files).";
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
            else if (!agents.Allows(agent) && legacy is null)
            {
                reasons.Add(agents.Stack is not null
                    ? $"Step {step.Number} names the unknown agent '{agent}'; this stack's specialists are {string.Join(", ", agents.Keys)}."
                    : $"Step {step.Number} names the agent '{agent}'; without a stack every step is \"{PlanAgents.Generalist}\".");
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

            normalised.Add(step with { Title = step.Title.Trim(), Task = step.Task.Trim(), Files = files, Agent = agent, Covers = covers, LegacyAgent = legacy?.LegacyAgentKey });
        }

        reasons.AddRange(ModifyOrderReasons(normalised, created));

        // FR-124: the composed plan rules. A plan whose only problem is a missing step for a changed layer is STACK_CHANGE_UNPLANNED.
        IReadOnlyList<PlanIssue> issues = agents.Stack is { } composed && agents.ApplyPlanRules
            ? CompositionPlanValidator.Errors(normalised, composed, agents.RepoEmpty)
            : [];
        if (reasons.Count == 0 && issues.Count > 0)
        {
            return Result.Failure<IReadOnlyList<PlanStep>>(CompositionPlanValidator.ToError(issues)!);
        }

        reasons.AddRange(issues.Select(i => i.Message));
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
