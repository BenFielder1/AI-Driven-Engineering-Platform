using System.Globalization;
using System.Text;
using Aide.Domain.Common;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-120, FR-121, FR-123–FR-127, §4.18: resolves a normalised composition against the catalog into what the specialists,
/// the spec, plan and review agents and the Guardian need (<see cref="StackResolution"/>). Pure and deterministic.
/// <para>Beyond the manifests (PROGRESS "M26 inputs"):</para>
/// <list type="bullet">
/// <item>globs, gate commands, CI checks and agent prompts are expanded with <see cref="StackPlaceholders"/>; an entry whose
/// layer is None is dropped (a rule whose <c>before</c> or <c>after</c> becomes empty is dropped too);</item>
/// <item>Supabase as the database host of another data pack (<c>host</c> mode) lends only its <c>Production notes</c> to the
/// data-engineer and the deploy-engineer: no ownership, rules, checks, env or spec sections;</item>
/// <item>a data pack's sections for the frontend-engineer (Prisma/Drizzle <c>Setup</c>) apply only in Next.js full-stack
/// mode; fe-nextjs's server ownership (<c>src/app/api/**</c>, <c>src/server/**</c>) only in full-stack mode; a frontend
/// pack's generated client only with a backend to generate it from (Next.js ui mode or an SPA with a backend);</item>
/// <item>public env keys get the frontend pack's <c>publicEnvPrefix</c> (Angular: none); <c>appliesTo: backend</c> is the
/// Next.js app in full-stack mode; <c>AIDE_RUN_MIGRATIONS</c> is computed (the app's host is serverless, i.e. its pack
/// declares the key, and an ORM data pack has a database host), not taken from pack declarations;</item>
/// <item>the scaffolder is exempt from the packs' exclusivity entries and order rules (v2 decision); exclusivity is never soft;</item>
/// <item><c>rls_on_every_table</c>/<c>grants_for_exposed_tables</c> only in Supabase BaaS mode; <c>container_non_root</c> only
/// when the backend runs in a container (a non-serverless backend host, or the Compose dev loop).</item>
/// </list>
/// </summary>
public static class StackResolver
{
    /// <summary>§4.22: the derived variable telling a serverless app to run its migrations at build time.</summary>
    public const string RunMigrationsKey = "AIDE_RUN_MIGRATIONS";

    /// <summary>The <c>appliesTo</c> component names.</summary>
    public const string FrontendComponent = "frontend";

    public const string BackendComponent = "backend";

    /// <summary>
    /// Resolves <paramref name="composition"/>. <paramref name="change"/> (FR-117) adds the "Stack Migration" section and the
    /// <c>STACK_CHANGE_UNPLANNED</c> plan rule; <paramref name="baseline"/> (the composition the base code was built with)
    /// lets the summary say what changed from what. <c>VALIDATION</c> when the composition names an option or pack the
    /// catalog doesn't have (re-validate it against the current catalog first).
    /// </summary>
    public static Result<StackResolution> Resolve(StackComposition composition, StackCatalog catalog, StackChange? change = null, StackComposition? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(catalog);
        change ??= StackChange.None;

        var paths = new StackLayerPaths(composition.Frontend?.Path, composition.Backend?.Path);
        var nextjsMode = composition.Frontend?.Pack == StackIds.NextJs
            ? composition.Derived?.NextjsMode ?? (composition.Backend is null ? StackIds.FullStack : StackIds.Ui)
            : null;
        var unknown = new List<string>();
        var packs = ResolvePacks(composition, catalog, nextjsMode, unknown);
        if (unknown.Count > 0)
        {
            return Result.Failure<StackResolution>(new DomainError(ErrorCodes.Validation, $"The composition doesn't match catalog {catalog.Label}: {unknown[0]}") { Details = unknown });
        }

        var facts = Facts(composition, catalog, nextjsMode);
        var context = new Context(composition, catalog, paths, nextjsMode, packs, facts, change);
        var contributing = packs.Where(p => !p.IsSupabaseHost).ToList();

        var specialists = Agents(composition).Select(key => Specialist(context, key)).ToList();
        var generated = GeneratedFiles(context, contributing);
        var contractFiles = generated
            .Where(g => g.Handling == "hand_written" && catalog.Packs.TryGetValue(g.Pack, out var p) && p.Manifest.Layer == StackLayers.Backend)
            .Select(g => g.Glob)
            .ToList();
        var apiSurface = Compile(composition.Backend is { } be ? StackApiSurface.Globs(be.Pack).Select(g => StackPlaceholders.ExpandGlob(g, paths)).OfType<string>() : []);

        var resolution = new StackResolution
        {
            CatalogLabel = catalog.Label,
            CatalogVersion = catalog.Version,
            Composition = composition,
            Paths = paths,
            NextjsMode = nextjsMode,
            Change = change,
            Packs = packs,
            Specialists = specialists,
            SpecSections = SpecSections(context, contributing),
            PlanRules = PlanRules(context, contributing),
            ProtectedPaths = ProtectedPaths(context, contributing),
            GeneratedFiles = generated,
            ContractFiles = contractFiles,
            ApiSurface = apiSurface,
            Checklist = Checklist(contributing),
            ReviewChecks = ReviewChecks(context, contributing),
            Env = Env(context, contributing),
            Gates =
            [
                .. contributing.Where(p => p.Layer is StackLayers.Frontend or StackLayers.Backend)
                    .SelectMany(p => p.Pack.Manifest.Gate ?? [])
                    .Select(g => StackPlaceholders.ExpandCommand(g, paths))
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal),
            ],
            MigrateCommand = contributing.FirstOrDefault(p => p.Layer == StackLayers.Data)?.Pack.Manifest.Migrate?.Command,
            Summary = string.Empty,
        };

        return Result.Success(resolution with { Summary = CompositionSummary.Render(catalog.CompositionSummaryTemplate, SummaryValues(context, resolution, baseline)) });
    }

    // ---------- packs and facts ----------

    private sealed record Context(
        StackComposition Composition,
        StackCatalog Catalog,
        StackLayerPaths Paths,
        string? NextjsMode,
        IReadOnlyList<ResolvedPack> Packs,
        IReadOnlyDictionary<string, string> Facts,
        StackChange Change)
    {
        /// <summary>The facts a pack's <c>when</c> reads: the composition's, plus the pack's own mode.</summary>
        public Dictionary<string, string> FactsFor(ResolvedPack pack) =>
            new(Facts, StringComparer.Ordinal) { [StackWhen.ModeFact] = pack.Mode };

        public bool IsBaas => Composition.Data?.Pack == StackIds.Supabase;

        public bool HasFrontendGeneratedClient => Composition.Backend is not null;
    }

    private static List<ResolvedPack> ResolvePacks(StackComposition composition, StackCatalog catalog, string? nextjsMode, List<string> unknown)
    {
        var packs = new List<ResolvedPack>();
        void Add(string choice, string? option, string mode)
        {
            if (option is null || option == StackIds.None)
            {
                return;
            }

            var found = catalog.Choices.FirstOrDefault(c => c.Id == choice)?.Find(option);
            if (found is null)
            {
                unknown.Add($"{choice} '{option}' isn't an option of the catalog.");
                return;
            }

            if (found.Pack is not { } id)
            {
                return; // None, External: no pack
            }

            if (!catalog.Packs.TryGetValue(id, out var pack))
            {
                unknown.Add($"{choice} '{option}' names the unknown pack '{id}'.");
                return;
            }

            if (packs.All(p => p.Id != id))
            {
                packs.Add(new ResolvedPack(pack, choice, mode));
            }
        }

        var dataMode = composition.Data is { } data
            ? data.Mode ?? catalog.Choices.FirstOrDefault(c => c.Id == StackChoices.Data)?.Find(data.Pack)?.Mode ?? StackIds.None
            : StackIds.None;
        Add(StackChoices.Frontend, composition.Frontend?.Pack, nextjsMode ?? StackIds.None);
        Add(StackChoices.Backend, composition.Backend?.Pack, StackIds.None);
        Add(StackChoices.Data, composition.Data?.Pack, dataMode);
        Add(StackChoices.Vcs, composition.Vcs.Pack, StackIds.None);
        Add(StackChoices.FrontendHost, composition.Deploy.FrontendHost, StackIds.None);
        Add(StackChoices.BackendHost, composition.Deploy.BackendHost, StackIds.None);
        Add(StackChoices.DatabaseHost, composition.Deploy.DatabaseHost, StackIds.HostMode);
        Add(StackChoices.Local, composition.Deploy.Local, StackIds.None);
        return packs;
    }

    private static Dictionary<string, string> Facts(StackComposition composition, StackCatalog catalog, string? nextjsMode)
    {
        var vcsOption = catalog.Choices.FirstOrDefault(c => c.Id == StackChoices.Vcs)?.Find(composition.Vcs.Pack);
        var vcsHost = composition.Vcs.SelfManaged ? vcsOption?.SelfManagedVcsHost ?? vcsOption?.VcsHost : vcsOption?.VcsHost;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StackChoices.Frontend] = composition.Frontend?.Pack ?? StackIds.None,
            [StackChoices.Backend] = composition.Backend?.Pack ?? StackIds.None,
            [StackChoices.Data] = composition.Data?.Pack ?? StackIds.None,
            [StackChoices.Vcs] = composition.Vcs.Pack,
            [StackChoices.FrontendHost] = composition.Deploy.FrontendHost ?? StackIds.None,
            [StackChoices.BackendHost] = composition.Deploy.BackendHost ?? StackIds.None,
            [StackChoices.DatabaseHost] = composition.Deploy.DatabaseHost ?? StackIds.None,
            [StackChoices.Local] = composition.Deploy.Local,
            [StackChoices.OriginModel] = composition.OriginModel ?? StackIds.None,
            [StackFacts.VcsHost] = vcsHost ?? StackIds.None,
            [StackFacts.NextjsMode] = nextjsMode ?? StackIds.None,
        };
    }

    /// <summary>FR-120: the specialists whose layer exists in the composition, in catalogue order.</summary>
    private static List<string> Agents(StackComposition composition)
    {
        var deploy = composition.Deploy;
        return
        [
            .. StackAgents.All.Where(key => key switch
            {
                StackAgents.DataEngineer => composition.Data is not null,
                StackAgents.BackendEngineer => composition.Backend is not null,
                StackAgents.FrontendEngineer => composition.Frontend is not null,
                StackAgents.DeployEngineer => deploy.FrontendHost is not null || deploy.BackendHost is not null || deploy.DatabaseHost is not null || deploy.Local == StackIds.Compose,
                _ => true,
            }),
        ];
    }

    // ---------- specialists ----------

    private static StackSpecialist Specialist(Context context, string key)
    {
        var globs = new List<string>();
        void AddGlob(string glob)
        {
            if (StackPlaceholders.ExpandGlob(glob, context.Paths) is { } expanded && StackGlobSet.IsValid(expanded) && !globs.Contains(expanded, StringComparer.Ordinal))
            {
                globs.Add(expanded);
            }
        }

        foreach (var glob in StackSpecialistCatalogue.CoreOwnership(key))
        {
            AddGlob(glob);
        }

        foreach (var pack in context.Packs.Where(p => !p.IsSupabaseHost))
        {
            if (!pack.Pack.Manifest.Ownership.TryGetValue(key, out var owned))
            {
                continue;
            }

            foreach (var glob in owned)
            {
                if (pack.Id == StackIds.NextJs && context.NextjsMode != StackIds.FullStack && glob is "{fe}/src/app/api/**" or "{fe}/src/server/**")
                {
                    continue; // fe-nextjs server code is the frontend-engineer's only in full-stack mode
                }

                AddGlob(glob);
            }
        }

        var prompt = context.Catalog.AgentPrompts.TryGetValue(key, out var text)
            ? StackPlaceholders.ExpandProse(CompositionSummary.StripLeadingComments(text), context.Paths)
            : string.Empty;
        return new StackSpecialist(
            key,
            StackSpecialistCatalogue.Name(key),
            StackSpecialistCatalogue.Description(key),
            globs,
            Compile(globs),
            Skills(context, key),
            prompt);
    }

    /// <summary>FR-121, §4.18 "Skill resolution": the agent's skills in prompt order.</summary>
    private static List<StackSkill> Skills(Context context, string key)
    {
        var skills = new List<StackSkill>();
        var hasBackend = context.Composition.Backend is not null;

        // stack-api-contract (with a backend): after the backend pack for the backend-engineer, after the frontend pack for
        // the frontend-engineer, last for the scaffolder.
        var contractAfter = !hasBackend ? null : key switch
        {
            StackAgents.BackendEngineer => StackChoices.Backend,
            StackAgents.FrontendEngineer => StackChoices.Frontend,
            _ => null,
        };

        foreach (var pack in context.Packs)
        {
            foreach (var section in SectionsFor(context, pack, key))
            {
                skills.Add(new StackSkill(pack.Id, pack.Pack.Manifest.Version, section.Name, StackPlaceholders.ExpandProse(section.Text, context.Paths)));
            }

            if (pack.Choice == contractAfter)
            {
                skills.AddRange(ApiContractSkills(context));
            }
        }

        if (hasBackend && key == StackAgents.Scaffolder)
        {
            skills.AddRange(ApiContractSkills(context));
        }

        return skills;
    }

    private static IEnumerable<StackSkill> ApiContractSkills(Context context) =>
        WholeSkill(context.Catalog.StackApiContract)
            .Select(s => new StackSkill(StackCoreSkills.ApiContract, StackCoreSkills.ApiContractVersion, s.Name, StackPlaceholders.ExpandProse(s.Text, context.Paths)));

    private static List<(string Name, string Text)> SectionsFor(Context context, ResolvedPack pack, string key)
    {
        if (pack.IsSupabaseHost)
        {
            return key is StackAgents.DataEngineer or StackAgents.DeployEngineer ? Named(pack.Pack.Skill, ["Production notes"]) : [];
        }

        if (!pack.Pack.Manifest.AgentSections.TryGetValue(key, out var listed) || listed.Count == 0)
        {
            return [];
        }

        if (key == StackAgents.FrontendEngineer && pack.Layer == StackLayers.Data && pack.Id != StackIds.Supabase && context.NextjsMode != StackIds.FullStack)
        {
            return []; // Prisma/Drizzle Setup is the frontend-engineer's only when the Next.js app is the server
        }

        return listed.Contains(PackVocabulary.WholeSkill, StringComparer.Ordinal) ? WholeSkill(pack.Pack.Skill) : Named(pack.Pack.Skill, listed);
    }

    private static List<(string Name, string Text)> Named(string skill, IEnumerable<string> sections) =>
    [
        .. sections
            .Where(s => s != PackVocabulary.AgentAutonomySection)
            .Select(s => (Name: s, Text: SkillSections.Extract(skill, s)))
            .Where(s => s.Text is not null)
            .Select(s => (s.Name, Body(s.Text!))),
    ];

    /// <summary>Every section but <c>Agent autonomy</c>; the text before the first section (minus comments and the title) opens the first.</summary>
    private static List<(string Name, string Text)> WholeSkill(string skill)
    {
        var sections = Named(skill, SkillSections.Parse(skill));
        var preamble = Preamble(skill);
        if (sections.Count > 0 && preamble.Length > 0)
        {
            sections[0] = (sections[0].Name, preamble + "\n\n" + sections[0].Text);
        }

        return sections;
    }

    private static string Preamble(string skill)
    {
        var text = CompositionSummary.StripLeadingComments(skill);
        var lines = text.Split('\n');
        var end = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal));
        var head = (end < 0 ? lines : lines[..end]).Where(l => !l.StartsWith("# ", StringComparison.Ordinal));
        return string.Join('\n', head).Trim();
    }

    /// <summary>A section's text without its <c>## </c> heading line.</summary>
    private static string Body(string section)
    {
        var newline = section.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? string.Empty : section[(newline + 1)..].Trim();
    }

    // ---------- spec, plan, paths, generated files ----------

    private static List<StackSpecSection> SpecSections(Context context, List<ResolvedPack> packs)
    {
        var sections = new List<StackSpecSection>();
        void Add(string title, string guidance, string source)
        {
            if (!sections.Any(s => s.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
            {
                sections.Add(new StackSpecSection(title, guidance, source));
            }
        }

        foreach (var pack in packs)
        {
            var facts = context.FactsFor(pack);
            foreach (var section in pack.Pack.Manifest.SpecSections.Where(s => StackWhen.Holds(s.When, facts)))
            {
                Add(section.Title, section.Guidance, pack.Id);
            }
        }

        if (context.Composition.OriginModel is "proxy" or "cross-origin")
        {
            Add(StackCoreSpecSections.OriginModelTitle, StackCoreSpecSections.OriginModelGuidance, StackCoreSpecSections.Source);
        }

        if (context.Change.IsStackChange)
        {
            Add(StackCoreSpecSections.StackMigrationTitle, StackCoreSpecSections.StackMigrationGuidance, StackCoreSpecSections.Source);
        }

        if (sections.Any(s => s.Title.Equals(StackCoreSpecSections.RlsTitle, StringComparison.OrdinalIgnoreCase)))
        {
            sections.RemoveAll(s => s.Title.Equals(StackCoreSpecSections.DataModelTitle, StringComparison.OrdinalIgnoreCase));
        }

        return sections;
    }

    private static CompositionPlanRules PlanRules(Context context, List<ResolvedPack> packs)
    {
        var order = new List<StackOrderRule>();
        var exclusive = new List<StackExclusiveRule>();
        var limits = new List<StackFileLimit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            var rules = pack.Pack.Manifest.PlanRules;
            foreach (var rule in rules.Order)
            {
                var before = ExpandAll(rule.Before, context.Paths);
                var after = ExpandAll(rule.After, context.Paths);
                if (before.Count > 0 && after.Count > 0 && seen.Add($"order|{string.Join(',', before)}|{string.Join(',', after)}|{rule.Soft}"))
                {
                    order.Add(new StackOrderRule(Compile(before), Compile(after), rule.Soft, rule.Reason, pack.Id));
                }
            }

            foreach (var rule in rules.Exclusive.Where(r => r.Agent != StackAgents.Scaffolder))
            {
                var globs = ExpandAll(rule.NotWithGlobs, context.Paths);
                if (globs.Count > 0 && seen.Add($"exclusive|{rule.Agent}|{string.Join(',', globs)}"))
                {
                    exclusive.Add(new StackExclusiveRule(rule.Agent, Compile(globs), rule.Reason, pack.Id));
                }
            }

            foreach (var limit in rules.MaxFilesPerStep)
            {
                if (StackPlaceholders.ExpandGlob(limit.Glob, context.Paths) is { } glob && StackGlobSet.Glob(glob) is { } compiled
                    && seen.Add($"limit|{glob}|{limit.Max}"))
                {
                    limits.Add(new StackFileLimit(compiled, limit.Max, pack.Id));
                }
            }
        }

        return new CompositionPlanRules(StackAgents.Scaffolder, order, exclusive, limits);
    }

    private static List<StackProtectedPath> ProtectedPaths(Context context, List<ResolvedPack> packs)
    {
        var list = new List<StackProtectedPath>();
        foreach (var pack in packs)
        {
            foreach (var entry in pack.Pack.Manifest.ProtectedPaths)
            {
                if (StackPlaceholders.ExpandGlob(entry.Glob, context.Paths) is not { } glob || StackGlobSet.Glob(glob) is not { } compiled)
                {
                    continue;
                }

                var except = entry.Except
                    .Select(e => StackPlaceholders.ExpandGlob(e, context.Paths))
                    .OfType<string>()
                    .Select(StackGlobSet.Glob)
                    .OfType<PathGlob>()
                    .ToList();
                if (list.All(p => p.Glob.Pattern != compiled.Pattern))
                {
                    list.Add(new StackProtectedPath(compiled, entry.ExistingOnBase, except, entry.Reason, pack.Id));
                }
            }
        }

        return list;
    }

    private static List<StackGeneratedFile> GeneratedFiles(Context context, List<ResolvedPack> packs)
    {
        var list = new List<StackGeneratedFile>();
        foreach (var pack in packs)
        {
            foreach (var file in pack.Pack.Manifest.GeneratedFiles)
            {
                if (pack.Layer == StackLayers.Frontend && file.Handling != "ci_generated" && !context.HasFrontendGeneratedClient)
                {
                    continue; // the generated client needs a backend contract to be generated from
                }

                if (StackPlaceholders.ExpandGlob(file.Glob, context.Paths) is not { } glob || StackGlobSet.Glob(glob) is not { } compiled
                    || list.Any(g => g.Glob == glob))
                {
                    continue;
                }

                var check = file.CiCheck is null ? null : StackPlaceholders.ExpandCommand(file.CiCheck, context.Paths);
                list.Add(new StackGeneratedFile(glob, compiled, file.Handling, check, pack.Id));
            }
        }

        return list;
    }

    // ---------- review ----------

    private static List<StackChecklistItem> Checklist(List<ResolvedPack> packs)
    {
        var items = new List<StackChecklistItem>();
        foreach (var pack in packs)
        {
            foreach (var item in pack.Pack.Manifest.Review.Checklist)
            {
                if (!items.Any(i => i.Item.Equals(item.Item, StringComparison.Ordinal)))
                {
                    items.Add(new StackChecklistItem(pack.Id, item.Item, item.Category, item.Severity));
                }
            }
        }

        return items;
    }

    private static List<string> ReviewChecks(Context context, List<ResolvedPack> packs)
    {
        var deploy = context.Composition.Deploy;
        var dockerBackend = context.Composition.Backend is not null
            && ((deploy.BackendHost is { } host && !IsServerless(context.Catalog, StackChoices.BackendHost, host)) || deploy.Local == StackIds.Compose);
        return
        [
            .. packs.SelectMany(p => p.Pack.Manifest.Review.Deterministic)
                .Distinct(StringComparer.Ordinal)
                .Where(id => id switch
                {
                    StackReviewChecks.RlsOnEveryTable or StackReviewChecks.GrantsForExposedTables => context.IsBaas,
                    StackReviewChecks.ContainerNonRoot => dockerBackend,
                    StackReviewChecks.HealthEndpoints => context.Composition.Backend is not null,
                    _ => true,
                }),
        ];
    }

    // ---------- env ----------

    private static List<StackEnvVar> Env(Context context, List<ResolvedPack> packs)
    {
        var composition = context.Composition;
        var prefix = composition.Frontend is { } fe && context.Catalog.Packs.TryGetValue(fe.Pack, out var fePack) ? fePack.Manifest.PublicEnvPrefix : null;
        var list = new List<StackEnvVar>();

        void Merge(string key, IReadOnlyList<string> appliesTo, IReadOnlyList<string> targets, bool sensitive, bool isPublic, string description, string pack)
        {
            var index = list.FindIndex(e => e.Key == key);
            if (index < 0)
            {
                list.Add(new StackEnvVar(key, appliesTo, targets, sensitive, isPublic, description, [pack]));
                return;
            }

            var existing = list[index];
            list[index] = existing with
            {
                AppliesTo = [.. existing.AppliesTo.Union(appliesTo, StringComparer.Ordinal)],
                Targets = [.. PackVocabulary.EnvTargets.Where(t => existing.Targets.Contains(t, StringComparer.Ordinal) || targets.Contains(t, StringComparer.Ordinal))],
                Sensitive = existing.Sensitive || sensitive,
                Public = existing.Public || isPublic,
                Packs = [.. existing.Packs.Union([pack], StringComparer.Ordinal)],
            };
        }

        foreach (var pack in packs)
        {
            var facts = context.FactsFor(pack);
            foreach (var env in pack.Pack.Manifest.RequiredEnv.Where(e => e.Key != RunMigrationsKey && StackWhen.Holds(e.When, facts)))
            {
                var appliesTo = Components(env.AppliesTo, context);
                if (appliesTo.Count == 0)
                {
                    continue;
                }

                var key = env.Public && prefix is not null && !env.Key.StartsWith(prefix, StringComparison.Ordinal) ? prefix + env.Key : env.Key;
                Merge(key, appliesTo, env.Targets, env.Sensitive, env.Public, env.Description, pack.Id);
            }
        }

        // AIDE_RUN_MIGRATIONS: the app that owns the database (the backend, or the Next.js full-stack app) runs on a
        // serverless host (its pack declares the key), with an ORM data pack and a database host.
        var deploy = composition.Deploy;
        var (appComponent, appHostChoice, appHost) = composition.Backend is not null
            ? (BackendComponent, StackChoices.BackendHost, deploy.BackendHost)
            : context.NextjsMode == StackIds.FullStack ? (FrontendComponent, StackChoices.FrontendHost, deploy.FrontendHost) : (null, null, null);
        if (appComponent is not null && appHost is not null && composition.Data is { } data && data.Pack != StackIds.Supabase && deploy.DatabaseHost is not null
            && HostPack(context.Catalog, appHostChoice!, appHost) is { } hostPack
            && hostPack.Manifest.RequiredEnv.FirstOrDefault(e => e.Key == RunMigrationsKey) is { } declared)
        {
            Merge(RunMigrationsKey, [appComponent], declared.Targets, false, false, declared.Description, hostPack.Id);
        }

        return list;
    }

    /// <summary><c>appliesTo</c> mapped to the components that exist: <c>backend</c> is the Next.js app in full-stack mode.</summary>
    private static List<string> Components(IReadOnlyList<string> appliesTo, Context context)
    {
        var list = new List<string>();
        foreach (var component in appliesTo)
        {
            var mapped = component switch
            {
                BackendComponent when context.Composition.Backend is not null => BackendComponent,
                BackendComponent when context.NextjsMode == StackIds.FullStack => FrontendComponent,
                BackendComponent => null,
                FrontendComponent => context.Composition.Frontend is not null ? FrontendComponent : null,
                _ => component,
            };
            if (mapped is not null && !list.Contains(mapped, StringComparer.Ordinal))
            {
                list.Add(mapped);
            }
        }

        return list;
    }

    private static StackPack? HostPack(StackCatalog catalog, string choice, string option) =>
        catalog.Choices.FirstOrDefault(c => c.Id == choice)?.Find(option)?.Pack is { } id && catalog.Packs.TryGetValue(id, out var pack) ? pack : null;

    /// <summary>A serverless host: its deploy pack declares <see cref="RunMigrationsKey"/> (Vercel, Netlify, Cloudflare).</summary>
    private static bool IsServerless(StackCatalog catalog, string choice, string option) =>
        HostPack(catalog, choice, option)?.Manifest.RequiredEnv.Any(e => e.Key == RunMigrationsKey) ?? false;

    // ---------- summary ----------

    private static Dictionary<string, string?> SummaryValues(Context context, StackResolution resolution, StackComposition? baseline)
    {
        var composition = context.Composition;
        var catalog = context.Catalog;
        string PackText(string? id) =>
            id is not null && catalog.Packs.TryGetValue(id, out var pack) ? $"`{pack.Label}` ({pack.Manifest.Name})" : "none";
        string PathText(string? path) => path is null ? "n/a" : path == StackPaths.Root ? "`.`" : $"`{path}/`";
        string HostText(string choice, string? option)
        {
            if (option is null)
            {
                return "none (local only)";
            }

            var found = catalog.Choices.FirstOrDefault(c => c.Id == choice)?.Find(option);
            return choice == StackChoices.DatabaseHost || found?.Pack is null
                ? found?.Name ?? option
                : PackText(found.Pack);
        }

        var frontendPack = composition.Frontend is { } fe && catalog.Packs.TryGetValue(fe.Pack, out var fp) ? fp : null;
        var backendPack = composition.Backend is { } be && catalog.Packs.TryGetValue(be.Pack, out var bp) ? bp : null;
        var dataPack = composition.Data is { } d && catalog.Packs.TryGetValue(d.Pack, out var dp) ? dp : null;

        var feNotes = new List<string>();
        if (context.NextjsMode is { } mode)
        {
            feNotes.Add($"Next.js {mode}");
        }

        if (frontendPack?.Manifest.PublicEnvPrefix is { } prefix)
        {
            feNotes.Add($"public env prefix `{prefix}`");
        }

        if (frontendPack?.Manifest.Runtime is { } feRuntime)
        {
            feNotes.Add($"runtime {feRuntime}");
        }

        var dataLocation = dataPack is null ? "n/a"
            : dataPack.Id == StackIds.Supabase ? "`supabase/` at the root"
            : dataPack.Manifest.Ownership.Values.SelectMany(v => v).Any(g => g.Contains(StackPlaceholders.App, StringComparison.Ordinal)) && context.Paths.App is { } app
                ? $"inside {PathText(app)}"
                : context.Paths.Backend is { } bePath ? $"inside {PathText(bePath)}" : "n/a";
        var dataMode = "n/a";
        if (dataPack is not null)
        {
            var modeName = composition.Data?.Mode is { } m ? dataPack.Manifest.Modes.FirstOrDefault(x => x.Id == m)?.Name ?? m : "ORM";
            var engine = dataPack.Manifest.Chips.Contains("SQL Server", StringComparer.OrdinalIgnoreCase) ? "SQL Server" : "PostgreSQL";
            dataMode = composition.Deploy.DatabaseHost == StackIds.SupabaseHost && dataPack.Id != StackIds.Supabase
                ? $"{modeName} · {engine} · Supabase host mode"
                : $"{modeName} · {engine}";
        }

        var vcsHost = context.Facts[StackFacts.VcsHost];
        var originRule = OriginRule(catalog, context.Facts);
        var generatedClient = resolution.GeneratedFiles.FirstOrDefault(g => g.Handling == "hand_written" && catalog.Packs.TryGetValue(g.Pack, out var p) && p.Manifest.Layer == StackLayers.Frontend);
        var contract = resolution.ContractFiles.Count == 0 ? "none: there's no separate backend."
            : generatedClient is null ? $"`{resolution.ContractFiles[0]}`."
            : $"`{resolution.ContractFiles[0]}` → `{generatedClient.Glob.Replace("/**", "/", StringComparison.Ordinal)}`.";

        var gates = new List<string>(resolution.Gates);
        if (resolution.MigrateCommand is { } migrate)
        {
            gates.Add($"migrate (one-off, before the app starts): {migrate}");
        }

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["STACK_CATALOG"] = catalog.Label,
            ["FRONTEND_PACK"] = PackText(composition.Frontend?.Pack),
            ["FRONTEND_PATH"] = PathText(composition.Frontend?.Path),
            ["FRONTEND_NOTES"] = string.Join("; ", feNotes),
            ["BACKEND_PACK"] = PackText(composition.Backend?.Pack),
            ["BACKEND_PATH"] = PathText(composition.Backend?.Path),
            ["BACKEND_NOTES"] = backendPack?.Manifest.Runtime is { } beRuntime ? $"runtime {beRuntime}" : string.Empty,
            ["DATA_PACK"] = PackText(composition.Data?.Pack),
            ["DATA_LOCATION"] = dataLocation,
            ["DATA_MODE"] = dataMode,
            ["VCS_PACK"] = $"{PackText(composition.Vcs.Pack)}, {vcsHost}",
            ["CI_FILE"] = StackVcsFiles.CiFile(composition.Vcs.Pack),
            ["FRONTEND_HOST"] = HostText(StackChoices.FrontendHost, composition.Deploy.FrontendHost),
            ["BACKEND_HOST"] = HostText(StackChoices.BackendHost, composition.Deploy.BackendHost),
            ["DATABASE_HOST"] = HostText(StackChoices.DatabaseHost, composition.Deploy.DatabaseHost),
            ["LOCAL_DEV_LOOP"] = composition.Deploy.Local == StackIds.Compose
                ? (HostPack(catalog, StackChoices.Local, StackIds.Compose) is { } local ? $"Docker Compose (`{local.Label}`)" : "Docker Compose")
                : "none",
            ["NEXTJS_MODE"] = context.NextjsMode ?? "n/a",
            ["ORIGIN_MODEL"] = composition.OriginModel ?? "n/a (API only)",
            ["ORIGIN_MODEL_TEXT"] = originRule is null ? string.Empty : originRule.Text.TrimEnd('.') + ".",
            ["CONTRACT"] = contract,
            ["ENV_VARS"] = Bullets(resolution.Env.Select(e =>
                $"`{e.Key}` — applies to {string.Join(", ", e.AppliesTo)}; {(e.Sensitive ? "secret" : e.Public ? "public" : "not secret")}; {string.Join(", ", e.Targets)}")),
            ["GATES"] = gates.Count == 0 ? "none" : string.Join('\n', gates),
            ["GENERATED_FILES"] = Bullets(resolution.GeneratedFiles.Select(g =>
                $"`{g.Glob}` — {HandlingText(g.Handling)}{(g.CiCheck is null ? string.Empty : $"; CI: `{g.CiCheck}`")}")),
            ["SPECIALISTS"] = Bullets(resolution.Specialists.Select(s =>
                $"`{s.Key}` — {s.Name} — {string.Join(", ", s.OwnedGlobs.Select(g => $"`{g}`"))}")),
            ["CAVEATS"] = composition.Caveats.Count == 0 ? null : string.Join('\n', composition.Caveats.Select(c => $"- {c.Rule} — {c.Text}")),
            ["STACK_CHANGE"] = StackChangeText(context.Change, baseline, composition),
        };
    }

    private static OriginModelRule? OriginRule(StackCatalog catalog, IReadOnlyDictionary<string, string> facts) =>
        catalog.Rules.OriginModel.FirstOrDefault(r => r.When.Count == 0 || r.When.Any(clause =>
            clause.Facts.All(f => f.Value.Matches(facts.TryGetValue(f.Key, out var v) ? v : StackIds.None))));

    private static string? StackChangeText(StackChange change, StackComposition? baseline, StackComposition current)
    {
        if (!change.IsStackChange)
        {
            return null;
        }

        var sb = new StringBuilder("### Stack change\n\nThis version changes the stack of code that already exists. The spec's \"Stack Migration\" section and the plan cover each change.\n");
        foreach (var layer in change.LayersChanged)
        {
            sb.Append("\n- ").Append(layer);
            if (baseline is not null)
            {
                sb.Append(": ").Append(LayerText(baseline, layer)).Append(" → ").Append(LayerText(current, layer));
            }
        }

        return sb.ToString();
    }

    private static string LayerText(StackComposition c, string layer) => layer switch
    {
        StackLayers.Frontend => c.Frontend is { } f ? $"`{f.Pack}` at `{f.Path}`" : "none",
        StackLayers.Backend => c.Backend is { } b ? $"`{b.Pack}` at `{b.Path}`" : "none",
        StackLayers.Data => c.Data is { } d ? $"`{d.Pack}`{(d.Mode is null ? string.Empty : $" ({d.Mode})")}" : "none",
        StackLayers.Vcs => $"`{c.Vcs.Pack}`",
        _ => string.Create(CultureInfo.InvariantCulture, $"frontend host {c.Deploy.FrontendHost ?? "none"}, backend host {c.Deploy.BackendHost ?? "none"}, database host {c.Deploy.DatabaseHost ?? "none"}, local {c.Deploy.Local}, origin {c.OriginModel ?? "n/a"}"),
    };

    private static string HandlingText(string handling) => handling switch
    {
        "hand_written" => "hand-written in the generator's shape",
        "ci_checked" => "checked by CI",
        "ci_generated" => "generated by CI (absent until the first CI run)",
        _ => handling,
    };

    private static string Bullets(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "- none" : string.Join('\n', list.Select(i => "- " + i));
    }

    // ---------- helpers ----------

    private static List<string> ExpandAll(IEnumerable<string> globs, StackLayerPaths paths) =>
    [
        .. globs.Select(g => StackPlaceholders.ExpandGlob(g, paths))
            .OfType<string>()
            .Where(StackGlobSet.IsValid)
            .Distinct(StringComparer.Ordinal),
    ];

    private static StackGlobSet Compile(IEnumerable<string> globs) => StackGlobSet.Of(globs);
}
