using Aide.Domain.Common;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-114–FR-116, §4.17: the one pure compatibility function behind <c>/api/stack/options</c>, <c>/api/stack/validate</c>,
/// project create, <c>PUT /stack</c>, <c>POST /versions</c> and the CLI. The rules are data (<see cref="StackRules"/>);
/// this class only evaluates them.
/// <list type="bullet">
/// <item>Choices are evaluated in <see cref="StackChoices.Order"/>, each against the valid choices before it, so the
/// options a step offers and what <see cref="Validate"/> accepts are the same thing.</item>
/// <item>A selection that earlier choices now forbid is <see cref="StackOptionState.Invalidated"/> (and a violation for
/// <see cref="Validate"/>); later choices are evaluated as if it weren't chosen.</item>
/// <item>A rule clause whose facts aren't all chosen yet never fires: an unfinished composition shows what could still work.</item>
/// <item>A forbidden option's reasons are its forbidding rules; a caveat's are its caveats.</item>
/// </list>
/// </summary>
public static class StackCompatibility
{
    /// <summary>The longest layer path accepted.</summary>
    public const int PathMaxLength = 200;

    /// <summary>
    /// §4.17 <c>Evaluate</c>: every choice with every option's state and reasons for the (partial) <paramref name="selection"/>.
    /// <c>VALIDATION</c> (details <c>"field: message"</c>) for unknown option ids or malformed paths.
    /// </summary>
    public static Result<StackEvaluation> Evaluate(StackSelection selection, StackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);
        var input = ReadInput(selection, catalog, validate: false);
        if (input.Errors.Count > 0)
        {
            return Result.Failure<StackEvaluation>(InputError(input.Errors));
        }

        var run = Run(catalog, input, applyDefaults: false);
        return Result.Success(new StackEvaluation(run.Choices, run.Facts.GetValueOrDefault(StackFacts.NextjsMode) is { } mode && mode != StackIds.None ? mode : null));
    }

    /// <summary>
    /// FR-116 <c>Validate</c>: the normalised composition with its derived fields, or every violation. A layer left out is
    /// None; the local dev loop and the origin model take their defaults (R12, R10). The VCS layer is required.
    /// </summary>
    public static StackValidation Validate(StackSelection selection, StackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);
        var input = ReadInput(selection, catalog, validate: true);
        if (input.Errors.Count > 0)
        {
            return new StackValidation(null, [], input.Errors);
        }

        var run = Run(catalog, input, applyDefaults: true);
        var violations = new List<StackViolation>();
        var caveats = new List<StackCaveat>();
        foreach (var choice in run.Choices)
        {
            if (choice.Selected is not { } selected)
            {
                continue;
            }

            var option = choice.Options.First(o => o.Option == selected);
            if (option.State == StackOptionState.Invalidated)
            {
                violations.AddRange(option.Reasons.Select(r => new StackViolation(choice.Choice, selected, r.Rule, r.Text)));
            }
            else if (option.State == StackOptionState.Caveat)
            {
                caveats.AddRange(option.Reasons.Select(r => new StackCaveat(choice.Choice, selected, r.Rule, r.Text)));
            }
        }

        if (violations.Count > 0)
        {
            return new StackValidation(null, violations, []);
        }

        return new StackValidation(Normalise(catalog, input, run, caveats), [], []);
    }

    /// <summary>The <c>STACK_INCOMPATIBLE</c> error for <paramref name="violations"/> (details: <c>"R3: reason"</c>).</summary>
    public static DomainError IncompatibleError(IReadOnlyList<StackViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        var reasons = violations.Select(v => $"{v.Rule}: {v.Reason}").Distinct(StringComparer.Ordinal).ToList();
        return new DomainError(ErrorCodes.StackIncompatible, $"These stack choices don't work together: {string.Join("; ", violations.Select(v => v.Reason).Distinct(StringComparer.Ordinal))}.")
        {
            Details = reasons,
            Violations = violations,
        };
    }

    /// <summary>
    /// FR-119: a layer path, normalised (relative, <c>/</c>-separated, no <c>.</c> segments, <c>.</c> for the repo root),
    /// or <c>null</c> with <paramref name="error"/> when it is absolute, escapes the repo (<c>..</c>) or has unsafe characters.
    /// </summary>
    public static string? NormalisePath(string path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed.Length > PathMaxLength)
        {
            error = $"must be at most {PathMaxLength} characters.";
            return null;
        }

        if (trimmed.StartsWith('/') || trimmed.Contains(':', StringComparison.Ordinal))
        {
            error = "must be relative to the repository root.";
            return null;
        }

        if (trimmed.Any(c => char.IsControl(c) || c is '*' or '?' or '"' or '<' or '>' or '|'))
        {
            error = "has characters a path can't have.";
            return null;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s is not ("." or "")).ToList();
        if (segments.Contains(".."))
        {
            error = "may not leave the repository ('..').";
            return null;
        }

        error = null;
        return segments.Count == 0 ? "." : string.Join('/', segments);
    }

    // ---------- input ----------

    private sealed record Input(
        Dictionary<string, string?> Picks,
        string? FrontendPath,
        string? BackendPath,
        Guid? InstanceId,
        bool SelfManaged,
        List<StackInputError> Errors);

    private static Input ReadInput(StackSelection selection, StackCatalog catalog, bool validate)
    {
        var errors = new List<StackInputError>();
        var picks = new Dictionary<string, string?>(StringComparer.Ordinal);

        string? Pick(string choiceId, string? value, string field)
        {
            if (value is null)
            {
                // Validate: left out is None, or the implicit default (applied in Run).
                return null;
            }

            var choice = catalog.Choice(choiceId);
            if (choice.Find(value) is null)
            {
                errors.Add(new StackInputError(field, $"'{value}' is not an option of {choice.Name} ({string.Join(", ", choice.Options.Select(o => o.Id))})."));
                return null;
            }

            return value;
        }

        picks[StackChoices.Frontend] = Pick(StackChoices.Frontend, selection.Frontend?.Pack, "frontend.pack");
        picks[StackChoices.Backend] = Pick(StackChoices.Backend, selection.Backend?.Pack, "backend.pack");
        picks[StackChoices.Data] = Pick(StackChoices.Data, selection.Data?.Pack, "data.pack");
        picks[StackChoices.Vcs] = Pick(StackChoices.Vcs, selection.Vcs?.Pack, "vcs.pack");
        picks[StackChoices.FrontendHost] = Pick(StackChoices.FrontendHost, selection.Deploy?.FrontendHost, "deploy.frontendHost");
        picks[StackChoices.BackendHost] = Pick(StackChoices.BackendHost, selection.Deploy?.BackendHost, "deploy.backendHost");
        picks[StackChoices.DatabaseHost] = Pick(StackChoices.DatabaseHost, selection.Deploy?.DatabaseHost, "deploy.databaseHost");
        picks[StackChoices.Local] = Pick(StackChoices.Local, selection.Deploy?.Local, "deploy.local");
        picks[StackChoices.OriginModel] = Pick(StackChoices.OriginModel, selection.OriginModel, "originModel");

        if (validate && selection.Vcs is null)
        {
            errors.Add(new StackInputError("vcs.pack", "is required."));
        }

        string? LayerPath(StackComponentPick? pick, string choiceId, string field)
        {
            if (picks[choiceId] is not { } id || id == StackIds.None)
            {
                return null;
            }

            if (pick?.Path is { } raw)
            {
                var normalised = NormalisePath(raw, out var error);
                if (normalised is null)
                {
                    errors.Add(new StackInputError(field, error!));
                }

                return normalised;
            }

            var pack = catalog.Choice(choiceId).Find(id)?.Pack;
            return (pack is not null && catalog.Packs.TryGetValue(pack, out var p) ? p.Manifest.DefaultPath : null) ?? choiceId;
        }

        var frontendPath = LayerPath(selection.Frontend, StackChoices.Frontend, "frontend.path");
        var backendPath = LayerPath(selection.Backend, StackChoices.Backend, "backend.path");
        errors.AddRange(StackPaths.Check(frontendPath, backendPath));

        if (selection.Data is { Mode: { } mode } && picks[StackChoices.Data] is { } dataId)
        {
            var option = catalog.Choice(StackChoices.Data).Find(dataId);
            if (option?.Mode != mode)
            {
                errors.Add(new StackInputError("data.mode", option?.Mode is { } expected
                    ? $"must be '{expected}' for {option.Name} (Supabase host mode is chosen as the database host)."
                    : $"{option?.Name ?? dataId} has no modes."));
            }
        }

        var vcsOption = picks[StackChoices.Vcs] is { } vcsId ? catalog.Choice(StackChoices.Vcs).Find(vcsId) : null;
        if (selection.Vcs is { SelfManaged: true } && vcsOption is not null && vcsOption.SelfManagedVcsHost is null)
        {
            errors.Add(new StackInputError("vcs.selfManaged", $"{vcsOption.Name} has no self-managed instances."));
        }

        if (selection.Vcs is { InstanceId: not null } && vcsOption is not null && vcsOption.SelfManagedVcsHost is null)
        {
            errors.Add(new StackInputError("vcs.instanceId", $"{vcsOption.Name} has no host instances."));
        }

        return new Input(picks, frontendPath, backendPath, selection.Vcs?.InstanceId, selection.Vcs?.SelfManaged ?? false, errors);
    }

    private static DomainError InputError(IReadOnlyList<StackInputError> errors) =>
        new(ErrorCodes.Validation, "The stack choices are invalid.") { Details = [.. errors.Select(e => e.ToString())] };

    // ---------- evaluation ----------

    private sealed record RunResult(List<StackChoiceEvaluation> Choices, Dictionary<string, string> Facts);

    private enum Truth
    {
        False,
        True,
        Unknown,
    }

    private static RunResult Run(StackCatalog catalog, Input input, bool applyDefaults)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new List<StackChoiceEvaluation>();
        foreach (var choice in catalog.Choices)
        {
            List<StackOptionEvaluation> options;
            string? defaultOption;
            var implicitDefault = false;
            if (choice.Id == StackChoices.OriginModel)
            {
                (options, defaultOption) = EvaluateOrigin(catalog, choice, facts);
                implicitDefault = true;
            }
            else
            {
                options = [.. choice.Options.Select(o => EvaluateOption(catalog, choice, o, facts))];
                (defaultOption, implicitDefault) = DefaultFor(catalog, choice, options, facts);
            }

            var selected = input.Picks[choice.Id];
            if (selected is null && applyDefaults)
            {
                // Validate: a left-out choice takes its implicit default, else None.
                selected = implicitDefault && defaultOption is not null ? defaultOption : choice.HasNone ? StackIds.None : null;
            }

            if (selected is not null)
            {
                var index = options.FindIndex(o => o.Option == selected);
                if (options[index].State == StackOptionState.Forbidden)
                {
                    options[index] = options[index] with { State = StackOptionState.Invalidated };
                }
                else
                {
                    facts[choice.Id] = selected;
                    AddDerivedFacts(catalog, choice.Id, selected, input.SelfManaged, facts);
                }
            }

            results.Add(new StackChoiceEvaluation(choice.Id, choice.Layer, selected, defaultOption, options));
        }

        return new RunResult(results, facts);
    }

    private static void AddDerivedFacts(StackCatalog catalog, string choice, string selected, bool selfManaged, Dictionary<string, string> facts)
    {
        switch (choice)
        {
            case StackChoices.Frontend when selected != StackIds.NextJs:
                facts[StackFacts.NextjsMode] = StackIds.None;
                break;
            case StackChoices.Backend when facts.GetValueOrDefault(StackChoices.Frontend) == StackIds.NextJs:
                // R2: Next.js is full-stack without a backend, the UI of one otherwise.
                facts[StackFacts.NextjsMode] = selected == StackIds.None ? StackIds.FullStack : StackIds.Ui;
                break;
            case StackChoices.Vcs:
                var option = catalog.Choice(StackChoices.Vcs).Find(selected)!;
                facts[StackFacts.VcsHost] = (selfManaged ? option.SelfManagedVcsHost : null) ?? option.VcsHost ?? selected;
                break;
        }
    }

    private static StackOptionEvaluation EvaluateOption(StackCatalog catalog, StackChoice choice, StackOption option, Dictionary<string, string> facts)
    {
        var forbidden = new List<StackReason>();
        var caveats = new List<StackReason>();
        foreach (var constraint in catalog.Rules.Constraints)
        {
            if (!constraint.Choices.Contains(choice.Id, StringComparer.Ordinal) || !constraint.Options.Contains(option.Id, StringComparer.Ordinal))
            {
                continue;
            }

            if (constraint.When.Count > 0 && AnyClause(constraint.When, facts) != Truth.True)
            {
                continue;
            }

            if (constraint.Unless.Count > 0 && AnyClause(constraint.Unless, facts) != Truth.False)
            {
                continue;
            }

            var reason = new StackReason(constraint.Rule, Render(catalog, constraint.Text, option, facts));
            (constraint.State == StackOptionState.Forbidden ? forbidden : caveats).Add(reason);
        }

        var state = forbidden.Count > 0 ? StackOptionState.Forbidden : caveats.Count > 0 ? StackOptionState.Caveat : StackOptionState.Allowed;
        return new StackOptionEvaluation(option.Id, state, forbidden.Count > 0 ? forbidden : caveats, IsRecommended(catalog, choice.Id, option.Id, facts));
    }

    private static (List<StackOptionEvaluation> Options, string? Default) EvaluateOrigin(StackCatalog catalog, StackChoice choice, Dictionary<string, string> facts)
    {
        foreach (var rule in catalog.Rules.OriginModel)
        {
            var truth = rule.When.Count == 0 ? Truth.True : AnyClause(rule.When, facts);
            if (truth == Truth.Unknown)
            {
                // Not decidable yet (the hosts aren't chosen): nothing is ruled out.
                return ([.. choice.Options.Select(o => new StackOptionEvaluation(o.Id, StackOptionState.Allowed, [], false))], null);
            }

            if (truth == Truth.True)
            {
                var options = choice.Options.Select(o => rule.Allowed.Contains(o.Id, StringComparer.Ordinal)
                    ? new StackOptionEvaluation(o.Id, StackOptionState.Allowed, [], o.Id == rule.Default)
                    : new StackOptionEvaluation(o.Id, StackOptionState.Forbidden, [new StackReason(OriginRule, rule.Text)], false));
                return ([.. options], rule.Default);
            }
        }

        return ([.. choice.Options.Select(o => new StackOptionEvaluation(o.Id, StackOptionState.Allowed, [], false))], null);
    }

    /// <summary>R10, the rule id of the origin model table.</summary>
    private const string OriginRule = "R10";

    private static (string? Option, bool Implicit) DefaultFor(StackCatalog catalog, StackChoice choice, List<StackOptionEvaluation> options, Dictionary<string, string> facts)
    {
        foreach (var entry in catalog.Rules.Defaults.Where(d => d.Choice == choice.Id))
        {
            var truth = entry.When.Count == 0 ? Truth.True : AnyClause(entry.When, facts);
            if (truth == Truth.Unknown)
            {
                return (null, false);
            }

            if (truth == Truth.False)
            {
                continue;
            }

            var candidate = entry.Option ?? (entry.SameAs is { } sameAs ? facts.GetValueOrDefault(sameAs) : null);
            if (candidate is not null && options.FirstOrDefault(o => o.Option == candidate) is { State: StackOptionState.Allowed or StackOptionState.Caveat })
            {
                return (candidate, entry.Implicit);
            }
        }

        return (null, false);
    }

    private static bool IsRecommended(StackCatalog catalog, string choice, string option, Dictionary<string, string> facts)
    {
        if (option == StackIds.None && choice is not (StackChoices.Backend or StackChoices.Data))
        {
            return false;
        }

        var position = StackChoices.IndexOf(choice);
        return catalog.Recommendations.Any(r =>
            r.Choices.TryGetValue(choice, out var options) && options.Contains(option, StringComparer.Ordinal)
            && r.Choices.All(c => StackChoices.IndexOf(c.Key) >= position || !facts.TryGetValue(c.Key, out var value) || c.Value.Contains(value, StringComparer.Ordinal)));
    }

    private static Truth AnyClause(IReadOnlyList<StackClause> clauses, Dictionary<string, string> facts)
    {
        var unknown = false;
        foreach (var clause in clauses)
        {
            switch (Clause(clause, facts))
            {
                case Truth.True:
                    return Truth.True;
                case Truth.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? Truth.Unknown : Truth.False;
    }

    private static Truth Clause(StackClause clause, Dictionary<string, string> facts)
    {
        var unknown = false;
        foreach (var (fact, match) in clause.Facts)
        {
            if (!facts.TryGetValue(fact, out var value))
            {
                unknown = true;
            }
            else if (!match.Matches(value))
            {
                return Truth.False;
            }
        }

        return unknown ? Truth.Unknown : Truth.True;
    }

    private static string Render(StackCatalog catalog, string text, StackOption option, Dictionary<string, string> facts)
    {
        if (!text.Contains('{', StringComparison.Ordinal))
        {
            return text;
        }

        var rendered = text.Replace("{option}", option.Name, StringComparison.Ordinal);
        foreach (var choice in catalog.Choices)
        {
            var placeholder = $"{{{choice.Id}}}";
            if (rendered.Contains(placeholder, StringComparison.Ordinal) && facts.TryGetValue(choice.Id, out var value))
            {
                rendered = rendered.Replace(placeholder, choice.Find(value)?.Name ?? value, StringComparison.Ordinal);
            }
        }

        return rendered;
    }

    // ---------- normalisation ----------

    private static StackComposition Normalise(StackCatalog catalog, Input input, RunResult run, List<StackCaveat> caveats)
    {
        string Pick(string choice) => run.Choices.First(c => c.Choice == choice).Selected ?? StackIds.None;
        string? OrNull(string choice) => Pick(choice) is var p && p == StackIds.None ? null : p;

        var frontend = OrNull(StackChoices.Frontend);
        var backend = OrNull(StackChoices.Backend);
        var data = OrNull(StackChoices.Data);
        var dataOption = data is null ? null : catalog.Choice(StackChoices.Data).Find(data);
        var vcs = Pick(StackChoices.Vcs);
        var deploy = new StackDeploy(OrNull(StackChoices.FrontendHost), OrNull(StackChoices.BackendHost), OrNull(StackChoices.DatabaseHost), Pick(StackChoices.Local));
        var origin = run.Choices.First(c => c.Choice == StackChoices.OriginModel);
        var nextjsMode = frontend == StackIds.NextJs ? run.Facts.GetValueOrDefault(StackFacts.NextjsMode) : null;

        var agents = new List<string> { StackAgents.Scaffolder };
        if (data is not null)
        {
            agents.Add(StackAgents.DataEngineer);
        }

        if (backend is not null)
        {
            agents.Add(StackAgents.BackendEngineer);
        }

        if (frontend is not null)
        {
            agents.Add(StackAgents.FrontendEngineer);
        }

        agents.Add(StackAgents.TestEngineer);
        agents.Add(StackAgents.CiEngineer);
        if (deploy.FrontendHost is not null || deploy.BackendHost is not null || deploy.DatabaseHost is not null || deploy.Local == StackIds.Compose)
        {
            agents.Add(StackAgents.DeployEngineer);
        }

        agents.Add(StackAgents.DocsWriter);

        // The packs in play, each with its own mode (§4.16 'when' conditions).
        var packs = new List<(StackPack Pack, string Mode)>();
        void AddPack(string choice, string? option, string mode)
        {
            if (option is not null && catalog.Choice(choice).Find(option)?.Pack is { } id && catalog.Packs.TryGetValue(id, out var pack)
                && packs.All(p => p.Pack.Id != id))
            {
                packs.Add((pack, mode));
            }
        }

        AddPack(StackChoices.Frontend, frontend, nextjsMode ?? StackIds.None);
        AddPack(StackChoices.Backend, backend, StackIds.None);
        AddPack(StackChoices.Data, data, dataOption?.Mode ?? StackIds.None);
        AddPack(StackChoices.Vcs, vcs, StackIds.None);
        AddPack(StackChoices.FrontendHost, deploy.FrontendHost, StackIds.None);
        AddPack(StackChoices.BackendHost, deploy.BackendHost, StackIds.None);
        AddPack(StackChoices.DatabaseHost, deploy.DatabaseHost, StackIds.HostMode);
        AddPack(StackChoices.Local, deploy.Local, StackIds.None);

        var specSections = new List<string>();
        var envNames = new List<string>();
        var gates = new List<string>();
        foreach (var (pack, mode) in packs)
        {
            var when = new Dictionary<string, string>(run.Facts, StringComparer.Ordinal)
            {
                [StackWhen.ModeFact] = mode,
                [StackChoices.OriginModel] = origin.Selected ?? StackIds.None,
            };
            specSections.AddRange(pack.Manifest.SpecSections
                .Where(s => StackWhen.Holds(s.When, when) && !specSections.Contains(s.Title, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Title));
            envNames.AddRange(pack.Manifest.RequiredEnv.Where(e => StackWhen.Holds(e.When, when) && !envNames.Contains(e.Key, StringComparer.Ordinal)).Select(e => e.Key));
            if (pack.Manifest.Layer is StackLayers.Frontend or StackLayers.Backend)
            {
                gates.AddRange(pack.Manifest.Gate ?? []);
            }
        }

        return new StackComposition
        {
            CatalogVersion = catalog.Version,
            CatalogHash = catalog.ContentHash,
            Frontend = frontend is null ? null : new StackComponent(frontend, input.FrontendPath!),
            Backend = backend is null ? null : new StackComponent(backend, input.BackendPath!),
            Data = data is null ? null : new StackDataLayer(data, dataOption?.Mode),
            Vcs = new StackVcs(vcs, input.InstanceId, input.SelfManaged),
            Deploy = deploy,
            OriginModel = origin.Selected,
            Derived = new StackDerived(
                nextjsMode,
                agents,
                [.. origin.Options.Where(o => o.State == StackOptionState.Allowed).Select(o => o.Option)],
                specSections,
                envNames,
                gates),
            Caveats = caveats,
        };
    }
}
