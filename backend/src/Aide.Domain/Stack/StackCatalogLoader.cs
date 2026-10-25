using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Presets;
using Aide.Domain.Review;

namespace Aide.Domain.Stack;

/// <summary>A catalog load problem (FR-122), attributed to a pack when it is about one.</summary>
public sealed record StackLoadError(string? Pack, string Message)
{
    public override string ToString() => Pack is null ? Message : $"{Pack}: {Message}";
}

/// <summary>The load status of one pack folder (Admin → Stack catalog): what could be read, its hash and its problems.</summary>
public sealed record StackPackStatus(
    string Id,
    string? Layer,
    string? Name,
    string? Version,
    PackSource? Source,
    string ContentHash,
    IReadOnlyList<string> Errors)
{
    public bool Loaded => Errors.Count == 0;
}

/// <summary>
/// The outcome of loading the catalog folder: the catalog (only when there were no errors at all), the version and hash
/// (whatever could be read), every error and the status of each pack folder.
/// </summary>
public sealed record StackCatalogLoadResult(
    StackCatalog? Catalog,
    string? Version,
    string ContentHash,
    IReadOnlyList<StackLoadError> Errors,
    IReadOnlyList<StackPackStatus> Packs);

/// <summary>
/// FR-113, FR-122, §4.16, §4.17: builds the <see cref="StackCatalog"/> from the files of <c>Agents/Stack/</c> (relative,
/// <c>/</c>-separated path → text). Pure: the files come from embedded resources in production and from fixtures in tests.
/// Every problem is collected and any problem fails the catalog: unknown JSON properties, missing files (packs, skills,
/// core files), unknown pack or option ids in options, rules and recommendations, rules that depend on later choices,
/// invalid globs, invalid vocabulary values, a missing provenance line, and <c>agentSections</c> naming a heading the
/// pack's <c>skill.md</c> doesn't have. The format is documented in <c>Stack/README.md</c>.
/// </summary>
public static partial class StackCatalogLoader
{
    public const string CatalogFile = "catalog.json";
    public const string PacksFolder = "packs/";
    public const string ManifestFile = "pack.json";
    public const string SkillFile = "skill.md";
    public const string StackApiContractFile = "core/stack-api-contract.md";
    public const string CompositionSummaryFile = "core/composition-summary.md";
    public const string AgentsFolder = "agents/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Loads the catalog from <paramref name="files"/> (paths relative to the <c>Stack/</c> folder).</summary>
    public static StackCatalogLoadResult Load(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var normalised = files.ToDictionary(f => NormalisePath(f.Key), f => f.Value, StringComparer.Ordinal);
        var hash = ContentHash(normalised);
        var errors = new List<StackLoadError>();
        void Error(string message) => errors.Add(new StackLoadError(null, message));

        // Packs: every folder under packs/, referenced or not.
        var packFolders = normalised.Keys
            .Where(k => k.StartsWith(PacksFolder, StringComparison.Ordinal))
            .Select(k => k[PacksFolder.Length..])
            .Where(rest => rest.Contains('/', StringComparison.Ordinal))
            .Select(rest => rest[..rest.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var packs = new Dictionary<string, StackPack>(StringComparer.Ordinal);
        var statuses = new List<StackPackStatus>();
        foreach (var folder in packFolders)
        {
            var (pack, status) = LoadPack(folder, normalised);
            statuses.Add(status);
            errors.AddRange(status.Errors.Select(e => new StackLoadError(folder, e)));
            if (pack is not null)
            {
                packs[folder] = pack;
            }
        }

        // Core files and agent prompts.
        var apiContract = RequiredText(normalised, StackApiContractFile, Error);
        var summary = RequiredText(normalised, CompositionSummaryFile, Error);
        var agentPrompts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in normalised.Where(f => f.Key.StartsWith(AgentsFolder, StringComparison.Ordinal)))
        {
            var name = path[AgentsFolder.Length..];
            if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
            if (!StackAgents.All.Contains(key, StringComparer.Ordinal))
            {
                Error($"{path}: '{key}' isn't a specialist key ({string.Join(", ", StackAgents.All)}).");
                continue;
            }

            if (!HasProvenance(text))
            {
                Error($"{path} must start with a provenance comment (<!-- Adapted from … -->).");
            }

            agentPrompts[key] = Text(text);
        }

        // catalog.json.
        if (!normalised.TryGetValue(CatalogFile, out var catalogText))
        {
            Error($"{CatalogFile} is missing.");
            return new StackCatalogLoadResult(null, null, hash, errors, statuses);
        }

        RawCatalog? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawCatalog>(catalogText, Json);
        }
        catch (JsonException ex)
        {
            Error($"{CatalogFile} is not valid: {ex.Message}");
            return new StackCatalogLoadResult(null, null, hash, errors, statuses);
        }

        if (raw is null)
        {
            Error($"{CatalogFile} is empty.");
            return new StackCatalogLoadResult(null, null, hash, errors, statuses);
        }

        var version = raw.Version?.Trim();
        if (version is null || !SemVer().IsMatch(version))
        {
            Error($"version '{raw.Version}' must be a semantic version (MAJOR.MINOR.PATCH).");
        }

        var layers = ReadLayers(raw.Layers, Error);
        var choices = ReadChoices(raw.Choices, packs, packFolders, Error);
        var rules = ReadRules(raw.Rules, choices, Error);
        var recommendations = ReadRecommendations(raw.Recommendations, choices, Error);

        if (errors.Count > 0)
        {
            return new StackCatalogLoadResult(null, version, hash, errors, statuses);
        }

        var catalog = new StackCatalog
        {
            Version = version!,
            ContentHash = hash,
            Layers = layers,
            Choices = choices,
            Packs = packs,
            Rules = rules,
            Recommendations = recommendations,
            StackApiContract = apiContract!,
            CompositionSummaryTemplate = summary!,
            AgentPrompts = agentPrompts,
        };
        return new StackCatalogLoadResult(catalog, version, hash, errors, statuses);
    }

    /// <summary>
    /// §4.16 (v2's preset hash, D-V2-12): SHA-256 (lowercase hex) over the files sorted by path (ordinal), each contributing
    /// <c>path \0 content \0</c> as UTF-8 with <c>\n</c> line endings and any BOM stripped.
    /// </summary>
    public static string ContentHash(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var builder = new StringBuilder();
        foreach (var (path, text) in files.Select(f => (NormalisePath(f.Key), f.Value)).OrderBy(f => f.Item1, StringComparer.Ordinal))
        {
            builder.Append(path).Append('\0').Append(Text(text)).Append('\0');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    // ---------- packs ----------

    private static (StackPack? Pack, StackPackStatus Status) LoadPack(string folder, Dictionary<string, string> files)
    {
        var prefix = $"{PacksFolder}{folder}/";
        var packFiles = files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var hash = ContentHash(packFiles);
        var reasons = new List<string>();
        StackPackStatus Status(RawPack? r) => new(folder, r?.Layer, r?.Name, r?.Version, r?.Source is { } s ? new PackSource(s.Plugin ?? string.Empty, s.Version ?? string.Empty, s.Snapshot ?? string.Empty) : null, hash, reasons);

        if (!files.TryGetValue(prefix + ManifestFile, out var manifestText))
        {
            reasons.Add($"{prefix}{ManifestFile} is missing.");
            return (null, Status(null));
        }

        RawPack? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawPack>(manifestText, Json);
        }
        catch (JsonException ex)
        {
            reasons.Add($"{ManifestFile} is not valid: {ex.Message}");
            return (null, Status(null));
        }

        if (raw is null)
        {
            reasons.Add($"{ManifestFile} is empty.");
            return (null, Status(null));
        }

        string Required(string? value, string field, int max = 2000)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                reasons.Add($"{field} is required.");
                return string.Empty;
            }

            if (value.Trim().Length > max)
            {
                reasons.Add($"{field} is longer than {max} characters.");
            }

            return value.Trim();
        }

        var id = Required(raw.Id, "id", 100);
        if (id.Length > 0 && id != folder)
        {
            reasons.Add($"id '{id}' must equal the folder name '{folder}'.");
        }

        var layer = Required(raw.Layer, "layer", 20);
        if (layer.Length > 0 && !StackLayers.PackLayers.Contains(layer, StringComparer.Ordinal))
        {
            reasons.Add($"layer '{layer}' is not one of {string.Join(", ", StackLayers.PackLayers)}.");
        }

        var name = Required(raw.Name, "name", 100);
        var description = Required(raw.Description, "description");

        PackSource source = new(string.Empty, string.Empty, string.Empty);
        if (raw.Source is null)
        {
            reasons.Add("source is required: {plugin, version, snapshot}.");
        }
        else
        {
            source = new PackSource(
                Required(raw.Source.Plugin, "source.plugin", 100),
                Required(raw.Source.Version, "source.version", 50),
                Required(raw.Source.Snapshot, "source.snapshot", 10));
            if (source.Version.Length > 0 && !SemVer().IsMatch(source.Version))
            {
                reasons.Add($"source.version '{source.Version}' must be a semantic version.");
            }

            if (source.Snapshot.Length > 0 && !DateOnly.TryParseExact(source.Snapshot, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                reasons.Add($"source.snapshot '{source.Snapshot}' must be a date (yyyy-MM-dd).");
            }
        }

        var version = Required(raw.Version, "version", 50);
        if (version.Length > 0 && (!PackVersion().IsMatch(version) || (source.Version.Length > 0 && !version.StartsWith(source.Version + "+aide.", StringComparison.Ordinal))))
        {
            reasons.Add($"version '{version}' must be '<source.version>+aide.<n>'.");
        }

        var chips = Strings(raw.Chips, "chips", reasons);
        var isComponent = layer is StackLayers.Frontend or StackLayers.Backend;

        if (raw.DefaultPath is not null && !PackVocabulary.DefaultPaths.Contains(raw.DefaultPath, StringComparer.Ordinal))
        {
            reasons.Add($"defaultPath '{raw.DefaultPath}' is not one of {string.Join(", ", PackVocabulary.DefaultPaths)} or null.");
        }

        if (raw.PublicEnvPrefix is not null)
        {
            if (layer != StackLayers.Frontend)
            {
                reasons.Add("publicEnvPrefix is for frontend packs only.");
            }
            else if (!PackVocabulary.PublicEnvPrefixes.Contains(raw.PublicEnvPrefix, StringComparer.Ordinal))
            {
                reasons.Add($"publicEnvPrefix '{raw.PublicEnvPrefix}' is not one of {string.Join(", ", PackVocabulary.PublicEnvPrefixes)} or null.");
            }
        }

        if (isComponent && raw.Runtime is null)
        {
            reasons.Add("runtime is required for frontend and backend packs.");
        }
        else if (raw.Runtime is not null && !isComponent)
        {
            reasons.Add("runtime is for frontend and backend packs only.");
        }
        else if (raw.Runtime is not null && !PackVocabulary.Runtimes.Contains(raw.Runtime, StringComparer.Ordinal))
        {
            reasons.Add($"runtime '{raw.Runtime}' is not one of {string.Join(", ", PackVocabulary.Runtimes)}.");
        }

        var modes = new List<PackMode>();
        foreach (var (m, i) in (raw.Modes ?? []).Select((m, i) => (m, i)))
        {
            var modeId = Required(m.Id, $"modes[{i}].id", 50);
            if (modeId.Length > 0 && !KebabId().IsMatch(modeId))
            {
                reasons.Add($"modes[{i}].id '{modeId}' must be lowercase kebab-case.");
            }

            if (modes.Any(x => x.Id == modeId))
            {
                reasons.Add($"modes[{i}].id '{modeId}' is listed twice.");
            }

            modes.Add(new PackMode(modeId, Required(m.Name, $"modes[{i}].name", 100), Required(m.Description, $"modes[{i}].description")));
        }

        var specSections = new List<PackSpecSection>();
        foreach (var (s, i) in (raw.SpecSections ?? []).Select((s, i) => (s, i)))
        {
            var title = Required(s.Title, $"specSections[{i}].title", 200);
            if (specSections.Any(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add($"specSections[{i}].title '{title}' is listed twice.");
            }

            specSections.Add(new PackSpecSection(title, Required(s.Guidance, $"specSections[{i}].guidance", 4000), When(s.When, $"specSections[{i}].when", reasons)));
        }

        // The skill and its sections.
        var skill = string.Empty;
        IReadOnlyList<string> sections = [];
        if (!files.TryGetValue(prefix + SkillFile, out var skillText) || string.IsNullOrWhiteSpace(skillText))
        {
            reasons.Add($"{prefix}{SkillFile} is missing or empty.");
        }
        else
        {
            skill = Text(skillText).Trim();
            sections = SkillSections.Parse(skill);
            if (!HasProvenance(skill, $"{source.Plugin}@{source.Version}"))
            {
                reasons.Add($"{SkillFile} must start with a provenance comment naming its source: <!-- Adapted from stack-packs {source.Plugin}@{source.Version} … -->.");
            }

            foreach (var duplicate in sections.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                reasons.Add($"{SkillFile} has the section '{duplicate.Key}' twice.");
            }

            if (sections.Count == 0)
            {
                reasons.Add($"{SkillFile} has no '## ' sections.");
            }
        }

        var ownership = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (agent, globs) in raw.Ownership ?? [])
        {
            if (!StackAgents.All.Contains(agent, StringComparer.Ordinal))
            {
                reasons.Add($"ownership: '{agent}' isn't a specialist key ({string.Join(", ", StackAgents.All)}).");
                continue;
            }

            ownership[agent] = Globs(globs, $"ownership.{agent}", reasons, allowNegation: true);
        }

        var agentSections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (agent, names) in raw.AgentSections ?? [])
        {
            if (!StackAgents.All.Contains(agent, StringComparer.Ordinal))
            {
                reasons.Add($"agentSections: '{agent}' isn't a specialist key ({string.Join(", ", StackAgents.All)}).");
                continue;
            }

            var list = Strings(names, $"agentSections.{agent}", reasons);
            foreach (var section in list.Where(s => s != PackVocabulary.WholeSkill))
            {
                if (SkillSections.Normalise(section) == PackVocabulary.AgentAutonomySection)
                {
                    reasons.Add($"agentSections.{agent} names the section '{section}', which no agent loads (§4.16).");
                }
                else if (!sections.Contains(SkillSections.Normalise(section), StringComparer.Ordinal))
                {
                    reasons.Add($"agentSections.{agent} names the section '{section}', which {SkillFile} doesn't have (it has: {string.Join(", ", sections)}).");
                }
            }

            if (list.Contains(PackVocabulary.WholeSkill, StringComparer.Ordinal) && list.Count > 1)
            {
                reasons.Add($"agentSections.{agent}: '*' already loads every section.");
            }

            agentSections[agent] = list;
        }

        var planRules = ReadPlanRules(raw.PlanRules, reasons);

        var protectedPaths = new List<PackProtectedPath>();
        foreach (var (p, i) in (raw.ProtectedPaths ?? []).Select((p, i) => (p, i)))
        {
            var glob = Glob(p.Glob, $"protectedPaths[{i}].glob", reasons, allowNegation: false);
            protectedPaths.Add(new PackProtectedPath(glob, p.ExistingOnBase ?? false, Globs(p.Except, $"protectedPaths[{i}].except", reasons, allowNegation: false), Required(p.Reason, $"protectedPaths[{i}].reason", 500)));
        }

        var generatedFiles = new List<PackGeneratedFile>();
        foreach (var (g, i) in (raw.GeneratedFiles ?? []).Select((g, i) => (g, i)))
        {
            var handling = Required(g.Handling, $"generatedFiles[{i}].handling", 20);
            if (handling.Length > 0 && !PackVocabulary.GeneratedFileHandling.Contains(handling, StringComparer.Ordinal))
            {
                reasons.Add($"generatedFiles[{i}].handling '{handling}' is not one of {string.Join(", ", PackVocabulary.GeneratedFileHandling)}.");
            }

            generatedFiles.Add(new PackGeneratedFile(Glob(g.Glob, $"generatedFiles[{i}].glob", reasons, allowNegation: false), handling, NullIfBlank(g.CiCheck)));
        }

        var env = new List<PackEnvVar>();
        foreach (var (e, i) in (raw.RequiredEnv ?? []).Select((e, i) => (e, i)))
        {
            var where = $"requiredEnv[{i}]";
            var key = Required(e.Key, $"{where}.key", 200);
            if (key.Length > 0 && !EnvKey().IsMatch(key))
            {
                reasons.Add($"{where}.key '{key}' must be UPPER_SNAKE_CASE.");
            }

            if (env.Any(x => x.Key == key))
            {
                reasons.Add($"{where}.key '{key}' is listed twice.");
            }

            var appliesTo = Strings(e.AppliesTo, $"{where}.appliesTo", reasons, required: true);
            foreach (var c in appliesTo.Where(c => !PackVocabulary.Components.Contains(c, StringComparer.Ordinal)))
            {
                reasons.Add($"{where}.appliesTo: '{c}' is not one of {string.Join(", ", PackVocabulary.Components)}.");
            }

            var targets = Strings(e.Targets, $"{where}.targets", reasons, required: true);
            foreach (var t in targets.Where(t => !PackVocabulary.EnvTargets.Contains(t, StringComparer.Ordinal)))
            {
                reasons.Add($"{where}.targets: '{t}' is not one of {string.Join(", ", PackVocabulary.EnvTargets)}.");
            }

            if (e.Sensitive is null || e.Public is null)
            {
                reasons.Add($"{where}: sensitive and public are required.");
            }
            else if (e.Sensitive == true && e.Public == true)
            {
                reasons.Add($"{where}: a variable can't be both sensitive and public.");
            }

            env.Add(new PackEnvVar(key, appliesTo, targets, e.Sensitive ?? false, e.Public ?? false, Required(e.Description, $"{where}.description"), When(e.When, $"{where}.when", reasons)));
        }

        var checklist = new List<PackChecklistItem>();
        foreach (var (c, i) in (raw.Review?.Checklist ?? []).Select((c, i) => (c, i)))
        {
            var where = $"review.checklist[{i}]";
            if (!EnumStrings.TryParse<ReviewCategory>(c.Category, out var category))
            {
                reasons.Add($"{where}.category '{c.Category}' is not one of {string.Join(", ", EnumStrings.Values<ReviewCategory>())}.");
            }

            if (!EnumStrings.TryParse<Severity>(c.Severity, out var severity))
            {
                reasons.Add($"{where}.severity '{c.Severity}' is not one of {string.Join(", ", EnumStrings.Values<Severity>())}.");
            }

            checklist.Add(new PackChecklistItem(Required(c.Item, $"{where}.item"), category, severity));
        }

        var deterministic = Strings(raw.Review?.Deterministic, "review.deterministic", reasons);
        foreach (var rule in deterministic.Where(r => !PackVocabulary.DeterministicRules.Contains(r, StringComparer.Ordinal)))
        {
            reasons.Add($"review.deterministic: '{rule}' is not one of {string.Join(", ", PackVocabulary.DeterministicRules)}.");
        }

        IReadOnlyList<string>? gate = null;
        if (raw.Gate is not null)
        {
            if (!isComponent)
            {
                reasons.Add("gate is for frontend and backend packs only.");
            }

            gate = Strings(raw.Gate, "gate", reasons);
        }

        PackMigrate? migrate = null;
        if (raw.Migrate is not null)
        {
            if (layer != StackLayers.Data)
            {
                reasons.Add("migrate is for data packs only.");
            }

            migrate = new PackMigrate(Required(raw.Migrate.Command, "migrate.command", 500), raw.Migrate.NeedsDirectUrl ?? false);
        }

        PackDeploy? deploy = null;
        if (raw.Deploy is null && layer == StackLayers.Deploy)
        {
            reasons.Add("deploy is required for deploy packs: {provider, shapes}.");
        }
        else if (raw.Deploy is not null)
        {
            if (layer != StackLayers.Deploy)
            {
                reasons.Add("deploy is for deploy packs only.");
            }

            var provider = Required(raw.Deploy.Provider, "deploy.provider", 50);
            if (provider.Length > 0 && !KebabId().IsMatch(provider))
            {
                reasons.Add($"deploy.provider '{provider}' must be lowercase kebab-case.");
            }

            var shapes = new List<PackDeployShape>();
            foreach (var (s, i) in (raw.Deploy.Shapes ?? []).Select((s, i) => (s, i)))
            {
                var shapeId = Required(s.Id, $"deploy.shapes[{i}].id", 50);
                if (shapes.Any(x => x.Id == shapeId))
                {
                    reasons.Add($"deploy.shapes[{i}].id '{shapeId}' is listed twice.");
                }

                var component = Required(s.Component, $"deploy.shapes[{i}].component", 20);
                if (component.Length > 0 && !PackVocabulary.Components.Contains(component, StringComparer.Ordinal))
                {
                    reasons.Add($"deploy.shapes[{i}].component '{component}' is not one of {string.Join(", ", PackVocabulary.Components)}.");
                }

                var runtimes = Strings(s.Runtimes, $"deploy.shapes[{i}].runtimes", reasons);
                foreach (var r in runtimes.Where(r => !PackVocabulary.Runtimes.Contains(r, StringComparer.Ordinal)))
                {
                    reasons.Add($"deploy.shapes[{i}].runtimes: '{r}' is not one of {string.Join(", ", PackVocabulary.Runtimes)}.");
                }

                shapes.Add(new PackDeployShape(shapeId, component, runtimes));
            }

            if (shapes.Count == 0)
            {
                reasons.Add("deploy.shapes: at least one shape is required.");
            }

            deploy = new PackDeploy(provider, shapes);
        }

        if (reasons.Count > 0)
        {
            return (null, Status(raw));
        }

        var manifest = new PackManifest
        {
            Id = id,
            Layer = layer,
            Name = name,
            Version = version,
            Source = source,
            Chips = chips,
            Description = description,
            DefaultPath = raw.DefaultPath,
            PublicEnvPrefix = raw.PublicEnvPrefix,
            Runtime = raw.Runtime,
            Modes = modes,
            SpecSections = specSections,
            Ownership = ownership,
            AgentSections = agentSections,
            PlanRules = planRules,
            ProtectedPaths = protectedPaths,
            GeneratedFiles = generatedFiles,
            RequiredEnv = env,
            Review = new PackReview(checklist, deterministic),
            Gate = gate,
            Migrate = migrate,
            Deploy = deploy,
        };
        return (new StackPack(manifest, skill, sections, hash), Status(raw));
    }

    private static PackPlanRules ReadPlanRules(RawPlanRules? raw, List<string> reasons)
    {
        if (raw is null)
        {
            return PackPlanRules.Empty;
        }

        var order = (raw.Order ?? []).Select((o, i) =>
        {
            var before = Globs(o.Before, $"planRules.order[{i}].before", reasons, allowNegation: false, required: true);
            var after = Globs(o.After, $"planRules.order[{i}].after", reasons, allowNegation: false, required: true);
            return new PackOrderRule(before, after, o.Soft ?? false, NullIfBlank(o.Reason));
        }).ToList();

        var exclusive = (raw.Exclusive ?? []).Select((e, i) =>
        {
            var agent = e.Agent?.Trim() ?? string.Empty;
            if (!StackAgents.All.Contains(agent, StringComparer.Ordinal))
            {
                reasons.Add($"planRules.exclusive[{i}].agent '{e.Agent}' isn't a specialist key ({string.Join(", ", StackAgents.All)}).");
            }

            return new PackExclusiveRule(agent, Globs(e.NotWithGlobs, $"planRules.exclusive[{i}].notWithGlobs", reasons, allowNegation: false, required: true), NullIfBlank(e.Reason));
        }).ToList();

        var limits = (raw.MaxFilesPerStep ?? []).Select((l, i) =>
        {
            var glob = Glob(l.Glob, $"planRules.maxFilesPerStep[{i}].glob", reasons, allowNegation: false);
            if (l.Max is not (>= 1 and <= 100))
            {
                reasons.Add($"planRules.maxFilesPerStep[{i}].max must be from 1 to 100.");
            }

            return new PackFileLimit(glob, l.Max ?? 1);
        }).ToList();

        return new PackPlanRules(order, exclusive, limits);
    }

    // ---------- catalog.json ----------

    private static List<StackLayer> ReadLayers(IReadOnlyList<RawLayer>? raw, Action<string> error)
    {
        var layers = new List<StackLayer>();
        foreach (var (l, i) in (raw ?? []).Select((l, i) => (l, i)))
        {
            if (string.IsNullOrWhiteSpace(l.Id) || string.IsNullOrWhiteSpace(l.Name) || string.IsNullOrWhiteSpace(l.Description))
            {
                error($"layers[{i}] needs id, name and description.");
                continue;
            }

            layers.Add(new StackLayer(l.Id.Trim(), l.Name.Trim(), l.Description.Trim()));
        }

        if (!layers.Select(l => l.Id).SequenceEqual(StackLayers.CatalogLayers, StringComparer.Ordinal))
        {
            error($"layers must be, in order: {string.Join(", ", StackLayers.CatalogLayers)}.");
        }

        return layers;
    }

    private static List<StackChoice> ReadChoices(
        IReadOnlyList<RawChoice>? raw, Dictionary<string, StackPack> packs, List<string> packFolders, Action<string> error)
    {
        var choices = new List<StackChoice>();
        foreach (var (c, i) in (raw ?? []).Select((c, i) => (c, i)))
        {
            var id = c.Id?.Trim() ?? string.Empty;
            var where = $"choices[{i}] '{id}'";
            if (StackChoices.IndexOf(id) < 0)
            {
                error($"choices[{i}].id '{id}' is not one of {string.Join(", ", StackChoices.Order)}.");
                continue;
            }

            if (c.Layer != StackChoices.LayerOf(id))
            {
                error($"{where}: layer must be '{StackChoices.LayerOf(id)}'.");
            }

            if (string.IsNullOrWhiteSpace(c.Name))
            {
                error($"{where}: name is required.");
            }

            var options = new List<StackOption>();
            foreach (var (o, j) in (c.Options ?? []).Select((o, j) => (o, j)))
            {
                if (ReadOption(id, o, $"{where} options[{j}]", packs, packFolders, error) is { } option)
                {
                    if (options.Any(x => x.Id == option.Id))
                    {
                        error($"{where}: the option '{option.Id}' is listed twice.");
                    }

                    options.Add(option);
                }
            }

            var hasNone = options.Any(o => o.Id == StackIds.None);
            if (StackChoices.WithNone.Contains(id, StringComparer.Ordinal) && !hasNone)
            {
                error($"{where} must offer '{StackIds.None}'.");
            }
            else if (!StackChoices.WithNone.Contains(id, StringComparer.Ordinal) && hasNone)
            {
                error($"{where} can't offer '{StackIds.None}'.");
            }

            choices.Add(new StackChoice(id, StackChoices.LayerOf(id), c.Name?.Trim() ?? string.Empty, options));
        }

        if (!choices.Select(c => c.Id).SequenceEqual(StackChoices.Order, StringComparer.Ordinal))
        {
            error($"choices must be, in order: {string.Join(", ", StackChoices.Order)}.");
        }

        return choices;
    }

    private static StackOption? ReadOption(
        string choice, RawOption o, string where, Dictionary<string, StackPack> packs, List<string> packFolders, Action<string> error)
    {
        var id = o.Id?.Trim() ?? string.Empty;
        if (!KebabId().IsMatch(id))
        {
            error($"{where}: id '{o.Id}' must be lowercase kebab-case.");
            return null;
        }

        StackPack? pack = null;
        if (o.Pack is not null)
        {
            if (choice == StackChoices.OriginModel || id == StackIds.None)
            {
                error($"{where} '{id}' can't name a pack.");
            }
            else if (!packFolders.Contains(o.Pack, StringComparer.Ordinal))
            {
                error($"{where} '{id}' names the pack '{o.Pack}', which isn't in {PacksFolder} (missing {PacksFolder}{o.Pack}/{ManifestFile}).");
            }
            else if (packs.TryGetValue(o.Pack, out pack))
            {
                var allowedLayers = choice switch
                {
                    StackChoices.FrontendHost or StackChoices.BackendHost => new[] { StackLayers.Deploy },
                    StackChoices.DatabaseHost => new[] { StackLayers.Data, StackLayers.Deploy },
                    StackChoices.Local => new[] { StackLayers.Local },
                    _ => new[] { StackChoices.LayerOf(choice) },
                };
                if (!allowedLayers.Contains(pack.Manifest.Layer, StringComparer.Ordinal))
                {
                    error($"{where} '{id}': the pack '{o.Pack}' is a {pack.Manifest.Layer} pack, not {string.Join(" or ", allowedLayers)}.");
                }

                if (choice is StackChoices.Frontend or StackChoices.Backend or StackChoices.Data or StackChoices.Vcs or StackChoices.FrontendHost or StackChoices.BackendHost
                    && id != o.Pack)
                {
                    error($"{where}: the option id '{id}' must equal its pack id '{o.Pack}'.");
                }
            }
        }
        else if (choice is StackChoices.Frontend or StackChoices.Backend or StackChoices.Data or StackChoices.Vcs or StackChoices.FrontendHost or StackChoices.BackendHost
            && id != StackIds.None)
        {
            error($"{where} '{id}' must name its pack.");
        }

        var name = NullIfBlank(o.Name) ?? pack?.Manifest.Name;
        var description = NullIfBlank(o.Description) ?? pack?.Manifest.Description;
        if (name is null || description is null)
        {
            if (o.Pack is null)
            {
                error($"{where} '{id}' needs a name and a description (or a pack).");
            }

            name ??= id;
            description ??= string.Empty;
        }

        if (o.Mode is not null)
        {
            if (choice != StackChoices.Data || pack is null || !pack.Manifest.Modes.Any(m => m.Id == o.Mode))
            {
                error($"{where} '{id}': mode '{o.Mode}' must be a mode of its data pack.");
            }
        }

        if (choice == StackChoices.Vcs)
        {
            if (string.IsNullOrWhiteSpace(o.VcsHost) || !KebabId().IsMatch(o.VcsHost))
            {
                error($"{where} '{id}' needs a vcsHost (kebab-case).");
            }

            if (o.SelfManagedVcsHost is not null && !KebabId().IsMatch(o.SelfManagedVcsHost))
            {
                error($"{where} '{id}': selfManagedVcsHost must be kebab-case.");
            }
        }
        else if (o.VcsHost is not null || o.SelfManagedVcsHost is not null)
        {
            error($"{where} '{id}': vcsHost and selfManagedVcsHost are for VCS options only.");
        }

        return new StackOption
        {
            Id = id,
            Name = name.Trim(),
            Description = description.Trim(),
            Chips = o.Chips is { } chips ? [.. chips.OfType<string>().Where(x => x.Trim().Length > 0).Select(x => x.Trim())] : pack?.Manifest.Chips ?? [],
            Pack = o.Pack,
            Mode = o.Mode,
            VcsHost = NullIfBlank(o.VcsHost),
            SelfManagedVcsHost = NullIfBlank(o.SelfManagedVcsHost),
        };
    }

    private static StackRules ReadRules(RawRules? raw, List<StackChoice> choices, Action<string> error)
    {
        var statements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rule, text) in raw?.Statements ?? [])
        {
            if (!RuleId().IsMatch(rule) || string.IsNullOrWhiteSpace(text))
            {
                error($"rules.statements: '{rule}' needs an id like R1 and a statement.");
                continue;
            }

            statements[rule] = text.Trim();
        }

        StackChoice? Choice(string id) => choices.FirstOrDefault(c => c.Id == id);
        var vcsHosts = (Choice(StackChoices.Vcs)?.Options ?? [])
            .SelectMany(o => new[] { o.VcsHost, o.SelfManagedVcsHost })
            .OfType<string>()
            .ToList();

        IReadOnlyList<string> FactValues(string fact) => fact switch
        {
            StackFacts.VcsHost => vcsHosts,
            StackFacts.NextjsMode => StackFacts.NextjsModes,
            _ => [.. (Choice(fact)?.Options ?? []).Select(o => o.Id)],
        };

        List<StackClause> Clauses(IReadOnlyList<Dictionary<string, List<string>>>? rawClauses, int position, string where)
        {
            var clauses = new List<StackClause>();
            foreach (var (clause, k) in (rawClauses ?? []).Select((c, k) => (c, k)))
            {
                if (clause.Count == 0)
                {
                    error($"{where}[{k}] is empty.");
                    continue;
                }

                var facts = new Dictionary<string, StackFactMatch>(StringComparer.Ordinal);
                foreach (var (fact, values) in clause)
                {
                    if (!StackFacts.IsKnown(fact))
                    {
                        error($"{where}[{k}]: '{fact}' isn't a known fact.");
                        continue;
                    }

                    if (StackFacts.PositionOf(fact) >= position)
                    {
                        error($"{where}[{k}]: '{fact}' isn't chosen before this rule's choice (rules may only depend on earlier choices).");
                    }

                    var positive = values.Where(v => !v.StartsWith('!')).ToList();
                    var negative = values.Where(v => v.StartsWith('!')).Select(v => v[1..]).ToList();
                    var known = FactValues(fact);
                    foreach (var unknown in positive.Concat(negative).Where(v => !known.Contains(v, StringComparer.Ordinal)))
                    {
                        error($"{where}[{k}].{fact}: '{unknown}' is not one of {string.Join(", ", known)}.");
                    }

                    if (values.Count == 0)
                    {
                        error($"{where}[{k}].{fact} lists no values.");
                    }

                    facts[fact] = new StackFactMatch(positive, negative);
                }

                clauses.Add(new StackClause(facts));
            }

            return clauses;
        }

        var constraints = new List<StackConstraint>();
        foreach (var (c, i) in (raw?.Constraints ?? []).Select((c, i) => (c, i)))
        {
            var where = $"rules.constraints[{i}]";
            var rule = c.Rule?.Trim() ?? string.Empty;
            if (!statements.ContainsKey(rule))
            {
                error($"{where}: rule '{c.Rule}' has no statement in rules.statements.");
            }

            var targetChoices = c.Choices ?? [];
            if (targetChoices.Count == 0)
            {
                error($"{where}: choices lists no choice.");
            }

            var position = int.MaxValue;
            foreach (var choiceId in targetChoices)
            {
                var choice = Choice(choiceId);
                if (choice is null || choiceId == StackChoices.OriginModel)
                {
                    error($"{where}: '{choiceId}' isn't a choice rules can constrain.");
                    continue;
                }

                position = Math.Min(position, StackChoices.IndexOf(choiceId));
                foreach (var option in (c.Options ?? []).Where(o => choice.Find(o) is null))
                {
                    error($"{where}: '{option}' is not an option of {choiceId}.");
                }
            }

            if (c.Options is not { Count: > 0 })
            {
                error($"{where}: options lists no option.");
            }

            var state = c.State switch
            {
                "forbidden" => StackOptionState.Forbidden,
                "caveat" => StackOptionState.Caveat,
                _ => (StackOptionState?)null,
            };
            if (state is null)
            {
                error($"{where}: state '{c.State}' must be forbidden or caveat.");
            }

            var text = c.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                error($"{where}: text is required.");
            }

            foreach (Match placeholder in Placeholder().Matches(text))
            {
                var name = placeholder.Groups[1].Value;
                if (name != "option" && (StackChoices.IndexOf(name) < 0 || StackChoices.IndexOf(name) >= position))
                {
                    error($"{where}: the placeholder '{{{name}}}' must be {{option}} or an earlier choice.");
                }
            }

            constraints.Add(new StackConstraint(
                rule,
                targetChoices,
                c.Options ?? [],
                state ?? StackOptionState.Forbidden,
                Clauses(c.When, position, $"{where}.when"),
                Clauses(c.Unless, position, $"{where}.unless"),
                text));
        }

        var origin = Choice(StackChoices.OriginModel);
        var originPosition = StackChoices.IndexOf(StackChoices.OriginModel);
        var originRules = new List<OriginModelRule>();
        foreach (var (o, i) in (raw?.OriginModel ?? []).Select((o, i) => (o, i)))
        {
            var where = $"rules.originModel[{i}]";
            var allowed = o.Allowed ?? [];
            foreach (var model in allowed.Where(m => origin?.Find(m) is null))
            {
                error($"{where}: '{model}' is not an origin model option.");
            }

            if (allowed.Count > 0 && (o.Default is null || !allowed.Contains(o.Default, StringComparer.Ordinal)))
            {
                error($"{where}: default must be one of the allowed models.");
            }
            else if (allowed.Count == 0 && o.Default is not null)
            {
                error($"{where}: a default needs allowed models.");
            }

            if (string.IsNullOrWhiteSpace(o.Text))
            {
                error($"{where}: text is required.");
            }

            originRules.Add(new OriginModelRule(Clauses(o.When, originPosition, $"{where}.when"), allowed, o.Default, o.Text?.Trim() ?? string.Empty));
        }

        if (originRules.Count == 0 || originRules[^1].When.Count > 0)
        {
            error("rules.originModel must end with an entry without 'when' (the table must cover every composition).");
        }

        if (!statements.ContainsKey("R10"))
        {
            error("rules.statements must state R10 (the origin model rule).");
        }

        var defaults = new List<StackDefault>();
        foreach (var (d, i) in (raw?.Defaults ?? []).Select((d, i) => (d, i)))
        {
            var where = $"rules.defaults[{i}]";
            var choice = d.Choice is null ? null : Choice(d.Choice);
            if (choice is null || choice.Id == StackChoices.OriginModel)
            {
                error($"{where}: '{d.Choice}' isn't a choice with defaults (the origin model's come from rules.originModel).");
                continue;
            }

            var position = StackChoices.IndexOf(choice.Id);
            if ((d.Option is null) == (d.SameAs is null))
            {
                error($"{where} needs exactly one of option and sameAs.");
            }

            if (d.Option is not null && choice.Find(d.Option) is null)
            {
                error($"{where}: '{d.Option}' is not an option of {choice.Id}.");
            }

            if (d.SameAs is not null && (StackChoices.IndexOf(d.SameAs) < 0 || StackChoices.IndexOf(d.SameAs) >= position))
            {
                error($"{where}: sameAs '{d.SameAs}' must be an earlier choice.");
            }

            if (d.Implicit == true && d.SameAs is not null)
            {
                error($"{where}: a sameAs default can't be implicit.");
            }

            defaults.Add(new StackDefault(choice.Id, d.Option, d.SameAs, Clauses(d.When, position, $"{where}.when"), d.Implicit ?? false));
        }

        return new StackRules { Statements = statements, Constraints = constraints, OriginModel = originRules, Defaults = defaults };
    }

    private static List<StackRecommendation> ReadRecommendations(IReadOnlyList<RawRecommendation>? raw, List<StackChoice> choices, Action<string> error)
    {
        var list = new List<StackRecommendation>();
        foreach (var (r, i) in (raw ?? []).Select((r, i) => (r, i)))
        {
            var where = $"recommendations[{i}]";
            var id = r.Id?.Trim() ?? string.Empty;
            if (!KebabId().IsMatch(id) || list.Any(x => x.Id == id))
            {
                error($"{where}: id '{r.Id}' must be unique lowercase kebab-case.");
            }

            if (string.IsNullOrWhiteSpace(r.Name))
            {
                error($"{where}: name is required.");
            }

            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (choiceId, options) in r.Choices ?? [])
            {
                var choice = choices.FirstOrDefault(c => c.Id == choiceId);
                if (choice is null)
                {
                    error($"{where}: '{choiceId}' isn't a choice.");
                    continue;
                }

                foreach (var option in options.Where(o => choice.Find(o) is null))
                {
                    error($"{where}: '{option}' is not an option of {choiceId}.");
                }

                map[choiceId] = options;
            }

            if (map.Count == 0)
            {
                error($"{where}: choices is empty.");
            }

            list.Add(new StackRecommendation(id, r.Name?.Trim() ?? string.Empty, map));
        }

        return list;
    }

    // ---------- helpers ----------

    private static string? RequiredText(Dictionary<string, string> files, string path, Action<string> error)
    {
        if (!files.TryGetValue(path, out var text) || string.IsNullOrWhiteSpace(text))
        {
            error($"{path} is missing or empty.");
            return null;
        }

        if (!HasProvenance(text))
        {
            error($"{path} must start with a provenance comment (<!-- … -->).");
        }

        return Text(text).Trim();
    }

    private static bool HasProvenance(string text, string? source = null)
    {
        var first = Text(text).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return first.StartsWith("<!--", StringComparison.Ordinal) && first.EndsWith("-->", StringComparison.Ordinal)
            && (source is null || first.StartsWith($"<!-- Adapted from stack-packs {source} ", StringComparison.Ordinal));
    }

    private static string? When(string? condition, string field, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        if (!StackWhen.IsValid(condition, out var reason))
        {
            reasons.Add($"{field}: {reason}");
        }

        return condition.Trim();
    }

    private static string Glob(string? glob, string field, List<string> reasons, bool allowNegation)
    {
        var pattern = glob?.Trim() ?? string.Empty;
        var body = allowNegation && pattern.StartsWith('!') ? pattern[1..] : pattern;
        var expanded = body.Replace("{fe}", "frontend", StringComparison.Ordinal)
            .Replace("{be}", "backend", StringComparison.Ordinal)
            .Replace("{app}", "backend", StringComparison.Ordinal);
        if (Placeholder().Match(expanded) is { Success: true } unknown)
        {
            reasons.Add($"{field}: '{pattern}' has the unknown placeholder '{unknown.Value}' ({string.Join(", ", PackVocabulary.GlobPlaceholders)}).");
        }
        else if (PathGlob.TryCreate(expanded, out var reason) is null)
        {
            reasons.Add($"{field}: invalid glob '{pattern}': {reason}");
        }

        return pattern;
    }

    private static List<string> Globs(IReadOnlyList<string?>? globs, string field, List<string> reasons, bool allowNegation, bool required = false)
    {
        var list = (globs ?? []).Select(g => Glob(g, field, reasons, allowNegation)).ToList();
        if (required && list.Count == 0)
        {
            reasons.Add($"{field}: at least one glob is required.");
        }

        return list;
    }

    private static List<string> Strings(IReadOnlyList<string?>? values, string field, List<string> reasons, bool required = false)
    {
        var result = new List<string>();
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                reasons.Add($"{field} has an empty entry.");
                continue;
            }

            result.Add(value.Trim());
        }

        if (required && result.Count == 0)
        {
            reasons.Add($"{field}: at least one entry is required.");
        }

        return result;
    }

    private static string Text(string text) => Artifacts.ContentHash.NormaliseLineEndings(text).TrimStart('﻿');

    private static string NormalisePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\+aide\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackVersion();

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabId();

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvKey();

    [GeneratedRegex("^R[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleId();

    [GeneratedRegex(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // ---------- JSON as written ----------

    private sealed class RawCatalog
    {
        public string? Version { get; init; }

        public IReadOnlyList<RawLayer>? Layers { get; init; }

        public IReadOnlyList<RawChoice>? Choices { get; init; }

        public RawRules? Rules { get; init; }

        public IReadOnlyList<RawRecommendation>? Recommendations { get; init; }

        [JsonPropertyName("$schema")]
        public string? Schema { get; init; }
    }

    private sealed class RawLayer
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }
    }

    private sealed class RawChoice
    {
        public string? Id { get; init; }

        public string? Layer { get; init; }

        public string? Name { get; init; }

        public IReadOnlyList<RawOption>? Options { get; init; }
    }

    private sealed class RawOption
    {
        public string? Id { get; init; }

        public string? Pack { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }

        public IReadOnlyList<string?>? Chips { get; init; }

        public string? Mode { get; init; }

        public string? VcsHost { get; init; }

        public string? SelfManagedVcsHost { get; init; }
    }

    private sealed class RawRules
    {
        public Dictionary<string, string>? Statements { get; init; }

        public IReadOnlyList<RawConstraint>? Constraints { get; init; }

        public IReadOnlyList<RawOriginRule>? OriginModel { get; init; }

        public IReadOnlyList<RawDefault>? Defaults { get; init; }
    }

    private sealed class RawConstraint
    {
        public string? Rule { get; init; }

        public IReadOnlyList<string>? Choices { get; init; }

        public IReadOnlyList<string>? Options { get; init; }

        public string? State { get; init; }

        public IReadOnlyList<Dictionary<string, List<string>>>? When { get; init; }

        public IReadOnlyList<Dictionary<string, List<string>>>? Unless { get; init; }

        public string? Text { get; init; }
    }

    private sealed class RawOriginRule
    {
        public IReadOnlyList<Dictionary<string, List<string>>>? When { get; init; }

        public IReadOnlyList<string>? Allowed { get; init; }

        public string? Default { get; init; }

        public string? Text { get; init; }
    }

    private sealed class RawDefault
    {
        public string? Choice { get; init; }

        public string? Option { get; init; }

        public string? SameAs { get; init; }

        public IReadOnlyList<Dictionary<string, List<string>>>? When { get; init; }

        public bool? Implicit { get; init; }
    }

    private sealed class RawRecommendation
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public Dictionary<string, List<string>>? Choices { get; init; }
    }

    private sealed class RawPack
    {
        public string? Id { get; init; }

        public string? Layer { get; init; }

        public string? Name { get; init; }

        public string? Version { get; init; }

        public RawSource? Source { get; init; }

        public IReadOnlyList<string?>? Chips { get; init; }

        public string? Description { get; init; }

        public string? DefaultPath { get; init; }

        public string? PublicEnvPrefix { get; init; }

        public string? Runtime { get; init; }

        public IReadOnlyList<RawMode>? Modes { get; init; }

        public IReadOnlyList<RawSpecSection>? SpecSections { get; init; }

        public Dictionary<string, List<string?>>? Ownership { get; init; }

        public Dictionary<string, List<string?>>? AgentSections { get; init; }

        public RawPlanRules? PlanRules { get; init; }

        public IReadOnlyList<RawProtectedPath>? ProtectedPaths { get; init; }

        public IReadOnlyList<RawGeneratedFile>? GeneratedFiles { get; init; }

        public IReadOnlyList<RawEnv>? RequiredEnv { get; init; }

        public RawReview? Review { get; init; }

        public IReadOnlyList<string?>? Gate { get; init; }

        public RawMigrate? Migrate { get; init; }

        public RawDeploy? Deploy { get; init; }

        [JsonPropertyName("$schema")]
        public string? Schema { get; init; }
    }

    private sealed class RawSource
    {
        public string? Plugin { get; init; }

        public string? Version { get; init; }

        public string? Snapshot { get; init; }
    }

    private sealed class RawMode
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }
    }

    private sealed class RawSpecSection
    {
        public string? Title { get; init; }

        public string? Guidance { get; init; }

        public string? When { get; init; }
    }

    private sealed class RawPlanRules
    {
        public IReadOnlyList<RawOrder>? Order { get; init; }

        public IReadOnlyList<RawExclusive>? Exclusive { get; init; }

        public IReadOnlyList<RawFileLimit>? MaxFilesPerStep { get; init; }
    }

    private sealed class RawOrder
    {
        public IReadOnlyList<string?>? Before { get; init; }

        public IReadOnlyList<string?>? After { get; init; }

        public bool? Soft { get; init; }

        public string? Reason { get; init; }
    }

    private sealed class RawExclusive
    {
        public string? Agent { get; init; }

        public IReadOnlyList<string?>? NotWithGlobs { get; init; }

        public string? Reason { get; init; }
    }

    private sealed class RawFileLimit
    {
        public string? Glob { get; init; }

        public int? Max { get; init; }
    }

    private sealed class RawProtectedPath
    {
        public string? Glob { get; init; }

        public bool? ExistingOnBase { get; init; }

        public IReadOnlyList<string?>? Except { get; init; }

        public string? Reason { get; init; }
    }

    private sealed class RawGeneratedFile
    {
        public string? Glob { get; init; }

        public string? Handling { get; init; }

        public string? CiCheck { get; init; }
    }

    private sealed class RawEnv
    {
        public string? Key { get; init; }

        public IReadOnlyList<string?>? AppliesTo { get; init; }

        public IReadOnlyList<string?>? Targets { get; init; }

        public bool? Sensitive { get; init; }

        public bool? Public { get; init; }

        public string? Description { get; init; }

        public string? When { get; init; }
    }

    private sealed class RawReview
    {
        public IReadOnlyList<RawChecklistItem>? Checklist { get; init; }

        public IReadOnlyList<string?>? Deterministic { get; init; }
    }

    private sealed class RawChecklistItem
    {
        public string? Item { get; init; }

        public string? Category { get; init; }

        public string? Severity { get; init; }
    }

    private sealed class RawMigrate
    {
        public string? Command { get; init; }

        public bool? NeedsDirectUrl { get; init; }
    }

    private sealed class RawDeploy
    {
        public string? Provider { get; init; }

        public IReadOnlyList<RawShape>? Shapes { get; init; }
    }

    private sealed class RawShape
    {
        public string? Id { get; init; }

        public string? Component { get; init; }

        public IReadOnlyList<string?>? Runtimes { get; init; }
    }
}
