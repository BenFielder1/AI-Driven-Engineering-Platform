using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Domain.Common;
using Aide.Domain.Review;

namespace Aide.Domain.Presets;

/// <summary>
/// §4.11, D-V2-12: builds a <see cref="Preset"/> from the files of its folder (relative path → text). Pure: the files
/// come from embedded resources in production and from test fixtures in tests. Every problem is collected, and any
/// problem fails the preset (<c>VALIDATION</c> with the reasons in the details): a malformed <c>preset.json</c>, an
/// unknown vocabulary value, an invalid glob, a duplicate key, or a <b>missing referenced file</b> (agent prompt,
/// <c>skills/&lt;name&gt;.md</c>, <c>specConventions</c> file). The format is documented in <c>Presets/README.md</c>.
/// </summary>
public static class PresetLoader
{
    /// <summary>The manifest file of a preset folder.</summary>
    public const string ManifestFile = "preset.json";

    /// <summary>The folder of skill files: <c>skills/&lt;name&gt;.md</c>.</summary>
    public const string SkillsFolder = "skills/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Loads the preset in folder <paramref name="folderId"/> from <paramref name="files"/> (paths relative to the folder, <c>/</c>-separated).</summary>
    public static Result<Preset> Load(string folderId, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(files);
        var reasons = new List<string>();
        var normalisedFiles = files.ToDictionary(f => NormalisePath(f.Key), f => f.Value, StringComparer.Ordinal);

        if (!normalisedFiles.TryGetValue(ManifestFile, out var manifestText))
        {
            return Fail(folderId, [$"{ManifestFile} is missing."]);
        }

        RawPreset? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawPreset>(manifestText, Json);
        }
        catch (JsonException ex)
        {
            return Fail(folderId, [$"{ManifestFile} is not valid: {ex.Message}"]);
        }

        if (raw is null)
        {
            return Fail(folderId, [$"{ManifestFile} is empty."]);
        }

        string Text(string? value, string field, int max = 2000)
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

        string? ReadFile(string? reference, string field)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                reasons.Add($"{field} is required.");
                return null;
            }

            var path = NormalisePath(reference.Trim());
            if (path.Split('/').Any(s => s is ".." or "." or "") || path.StartsWith('/'))
            {
                reasons.Add($"{field} '{reference}' must be a path inside the preset folder.");
                return null;
            }

            if (!normalisedFiles.TryGetValue(path, out var text))
            {
                reasons.Add($"{field} references '{path}', which is missing.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                reasons.Add($"{field} references '{path}', which is empty.");
                return null;
            }

            return ContentHashText(text).Trim();
        }

        // Identity.
        var id = Text(raw.Id, "id", PresetRules.IdMaxLength);
        if (id.Length > 0 && !PresetRules.IsKebabId(id))
        {
            reasons.Add($"id '{id}' must be lowercase kebab-case.");
        }

        if (id.Length > 0 && id != folderId)
        {
            reasons.Add($"id '{id}' must equal the folder name '{folderId}'.");
        }

        var version = Text(raw.Version, "version", PresetRules.VersionMaxLength);
        if (version.Any(char.IsWhiteSpace) || version.Contains('@', StringComparison.Ordinal))
        {
            reasons.Add($"version '{version}' may not contain whitespace or '@'.");
        }

        var name = Text(raw.Name, "name", 100);
        var description = Text(raw.Description, "description");

        var stack = Strings(raw.Stack, "stack", reasons, required: true);
        var deployTargets = Strings(raw.DeployTargets, "deployTargets", reasons, required: false);
        foreach (var target in deployTargets.Where(t => !PresetVocabulary.DeployTargets.Contains(t, StringComparer.Ordinal)))
        {
            reasons.Add($"deployTargets: '{target}' is not one of {string.Join(", ", PresetVocabulary.DeployTargets)}.");
        }

        // Environment.
        var env = new List<PresetEnvVar>();
        foreach (var (item, i) in (raw.RequiredEnv ?? []).Select((e, i) => (e, i)))
        {
            var where = $"requiredEnv[{i}]";
            var key = Text(item.Key, $"{where}.key", 200);
            if (key.Length > 0 && !IsEnvKey(key))
            {
                reasons.Add($"{where}.key '{key}' must be UPPER_SNAKE_CASE.");
            }

            if (env.Any(e => e.Key == key))
            {
                reasons.Add($"{where}.key '{key}' is listed twice.");
            }

            var targets = Strings(item.Targets, $"{where}.targets", reasons, required: true);
            foreach (var target in targets.Where(t => !PresetVocabulary.EnvTargets.Contains(t, StringComparer.Ordinal)))
            {
                reasons.Add($"{where}.targets: '{target}' is not one of {string.Join(", ", PresetVocabulary.EnvTargets)}.");
            }

            env.Add(new PresetEnvVar(key, targets, item.Sensitive ?? false, Text(item.Description, $"{where}.description")));
        }

        // Spec.
        var sections = new List<PresetSpecSection>();
        foreach (var (item, i) in (raw.SpecSections ?? []).Select((s, i) => (s, i)))
        {
            var title = Text(item.Title, $"specSections[{i}].title", 200);
            if (title.Length > 0 && sections.Any(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add($"specSections[{i}].title '{title}' is listed twice.");
            }

            sections.Add(new PresetSpecSection(title, Text(item.Guidance, $"specSections[{i}].guidance", 4000)));
        }

        var conventions = string.IsNullOrWhiteSpace(raw.SpecConventions)
            ? string.Empty
            : IsFileReference(raw.SpecConventions)
                ? ReadFile(raw.SpecConventions, "specConventions") ?? string.Empty
                : ContentHashText(raw.SpecConventions).Trim();

        // Agents.
        var agents = new List<PresetAgent>();
        if (raw.Agents is not { Count: > 0 })
        {
            reasons.Add("agents: at least one agent is required.");
        }

        foreach (var (item, i) in (raw.Agents ?? []).Select((a, i) => (a, i)))
        {
            var where = $"agents[{i}]";
            var key = Text(item.Key, $"{where}.key", PresetRules.IdMaxLength);
            if (key.Length > 0 && !PresetRules.IsKebabId(key))
            {
                reasons.Add($"{where}.key '{key}' must be lowercase kebab-case.");
            }

            if (key == Execution.PlanAgents.Generalist)
            {
                reasons.Add($"{where}.key '{key}' is reserved for plans without a preset.");
            }

            if (agents.Any(a => a.Key == key))
            {
                reasons.Add($"{where}.key '{key}' is listed twice.");
            }

            var owns = GlobSet.Compile(item.Owns, $"{where}.owns", reasons);
            if (owns.Patterns.Count == 0)
            {
                reasons.Add($"{where}.owns: at least one glob is required.");
            }

            var skills = Strings(item.Skills, $"{where}.skills", reasons, required: false);
            var skillTexts = new List<PresetSkill>();
            foreach (var skill in skills)
            {
                if (!PresetRules.IsKebabId(skill))
                {
                    reasons.Add($"{where}.skills: '{skill}' must be lowercase kebab-case (the file skills/{skill}.md).");
                    continue;
                }

                if (ReadFile($"{SkillsFolder}{skill}.md", $"{where}.skills '{skill}'") is { } text)
                {
                    skillTexts.Add(new PresetSkill(skill, text));
                }
            }

            if (skills.Distinct(StringComparer.Ordinal).Count() != skills.Count)
            {
                reasons.Add($"{where}.skills lists a skill twice.");
            }

            agents.Add(new PresetAgent
            {
                Key = key,
                Name = Text(item.Name, $"{where}.name", 100),
                Description = Text(item.Description, $"{where}.description"),
                Owns = owns,
                Skills = skills,
                PromptFile = item.Prompt?.Trim() ?? string.Empty,
                Prompt = ReadFile(item.Prompt, $"{where}.prompt") ?? string.Empty,
                SkillTexts = skillTexts,
            });
        }

        var keys = agents.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

        // Plan rules.
        var rawRules = raw.PlanRules ?? new RawPlanRules();
        var scaffolder = string.IsNullOrWhiteSpace(rawRules.Scaffolder) ? PresetRules.DefaultScaffolder : rawRules.Scaffolder.Trim();
        if (rawRules.ScaffoldFirstWhenEmpty == true && !keys.Contains(scaffolder))
        {
            reasons.Add($"planRules.scaffoldFirstWhenEmpty needs the agent '{scaffolder}' in the catalogue (or set planRules.scaffolder).");
        }

        var order = new List<PresetOrderRule>();
        foreach (var (item, i) in (rawRules.Order ?? []).Select((o, i) => (o, i)))
        {
            var before = GlobSet.Compile(item.Before, $"planRules.order[{i}].before", reasons);
            var after = GlobSet.Compile(item.After, $"planRules.order[{i}].after", reasons);
            if (before.Patterns.Count == 0 || after.Patterns.Count == 0)
            {
                reasons.Add($"planRules.order[{i}] needs before and after globs.");
            }

            order.Add(new PresetOrderRule(before, after, item.Soft ?? false, NullIfBlank(item.Reason)));
        }

        var exclusive = new List<PresetExclusiveRule>();
        foreach (var (item, i) in (rawRules.Exclusive ?? []).Select((e, i) => (e, i)))
        {
            var agent = Text(item.Agent, $"planRules.exclusive[{i}].agent", PresetRules.IdMaxLength);
            if (agent.Length > 0 && !keys.Contains(agent))
            {
                reasons.Add($"planRules.exclusive[{i}].agent '{agent}' is not in the catalogue.");
            }

            var notWith = GlobSet.Compile(item.NotWithGlobs, $"planRules.exclusive[{i}].notWithGlobs", reasons);
            if (notWith.Patterns.Count == 0)
            {
                reasons.Add($"planRules.exclusive[{i}].notWithGlobs: at least one glob is required.");
            }

            exclusive.Add(new PresetExclusiveRule(agent, notWith, NullIfBlank(item.Reason)));
        }

        var fileLimits = new List<PresetFileLimit>();
        var limits = (rawRules.MaxFilesPerStep ?? []).Select((l, i) => (l.Glob, l.Max, $"planRules.maxFilesPerStep[{i}]"));
        if (rawRules.OneMigrationPerStep is { } oneMigration)
        {
            limits = limits.Append((oneMigration.Glob, (int?)1, "planRules.oneMigrationPerStep"));
        }

        foreach (var (glob, max, where) in limits)
        {
            var compiled = PathGlob.TryCreate(glob, out var reason);
            if (compiled is null)
            {
                reasons.Add($"{where}.glob: {reason}");
            }
            else if (max is not (>= 1 and <= 100))
            {
                reasons.Add($"{where}.max must be from 1 to 100.");
            }
            else
            {
                fileLimits.Add(new PresetFileLimit(compiled, max.Value));
            }
        }

        // Review.
        var checklist = new List<PresetChecklistItem>();
        foreach (var (item, i) in (raw.Review?.Checklist ?? []).Select((c, i) => (c, i)))
        {
            var where = $"review.checklist[{i}]";
            if (!EnumStrings.TryParse<ReviewCategory>(item.Category, out var category))
            {
                reasons.Add($"{where}.category '{item.Category}' is not one of {string.Join(", ", EnumStrings.Values<ReviewCategory>())}.");
            }

            if (!EnumStrings.TryParse<Severity>(item.Severity, out var severity))
            {
                reasons.Add($"{where}.severity '{item.Severity}' is not one of {string.Join(", ", EnumStrings.Values<Severity>())}.");
            }

            checklist.Add(new PresetChecklistItem(category, Text(item.Item, $"{where}.item"), severity));
        }

        var deterministic = Strings(raw.Review?.Deterministic, "review.deterministic", reasons, required: false);
        foreach (var rule in deterministic.Where(r => !PresetVocabulary.DeterministicRules.Contains(r, StringComparer.Ordinal)))
        {
            reasons.Add($"review.deterministic: '{rule}' is not one of {string.Join(", ", PresetVocabulary.DeterministicRules)}.");
        }

        // Protected paths.
        var protectedPaths = new List<PresetProtectedPath>();
        foreach (var (item, i) in (raw.ProtectedPaths ?? []).Select((p, i) => (p, i)))
        {
            if (ReadProtectedPath(item, $"protectedPaths[{i}]", reasons) is { } path)
            {
                protectedPaths.Add(path);
            }
        }

        if (reasons.Count > 0)
        {
            return Fail(folderId, reasons);
        }

        return Result.Success(new Preset
        {
            Id = id,
            Name = name,
            Version = version,
            ContentHash = ContentHash(normalisedFiles),
            Description = description,
            Stack = stack,
            DeployTargets = deployTargets,
            RequiredEnv = env,
            SpecSections = sections,
            SpecConventions = conventions,
            PlanRules = new PresetPlanRules
            {
                ScaffoldFirstWhenEmpty = rawRules.ScaffoldFirstWhenEmpty ?? false,
                Scaffolder = scaffolder,
                Order = order,
                Exclusive = exclusive,
                FileLimits = fileLimits,
            },
            Agents = agents,
            Review = new PresetReview(checklist, deterministic),
            ProtectedPaths = protectedPaths,
        });
    }

    /// <summary>
    /// D-V2-12: the content hash of a preset folder — SHA-256 (lowercase hex) over the files sorted by path (ordinal),
    /// each contributing <c>path \0 content \0</c> as UTF-8 with line endings normalised to <c>\n</c>, so the hash is the
    /// same on every checkout (Windows or not). Any file in the folder counts, referenced or not.
    /// </summary>
    public static string ContentHash(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var builder = new StringBuilder();
        foreach (var (path, text) in files.Select(f => (NormalisePath(f.Key), f.Value)).OrderBy(f => f.Item1, StringComparer.Ordinal))
        {
            builder.Append(path).Append('\0').Append(ContentHashText(text)).Append('\0');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Whether a <c>specConventions</c> value is a file reference (a single-line relative path ending in <c>.md</c>).</summary>
    public static bool IsFileReference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !trimmed.Any(char.IsWhiteSpace);
    }

    private static string ContentHashText(string text) => Artifacts.ContentHash.NormaliseLineEndings(text).TrimStart('﻿');

    private static string NormalisePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsEnvKey(string key) =>
        key[0] is >= 'A' and <= 'Z' && key.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    private static List<string> Strings(IReadOnlyList<string?>? values, string field, List<string> reasons, bool required)
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

    private static PresetProtectedPath? ReadProtectedPath(JsonElement item, string where, List<string> reasons)
    {
        string? glob;
        string? pathReason = null;
        var existingOnBase = false;
        var except = new List<PathGlob>();
        switch (item.ValueKind)
        {
            case JsonValueKind.String:
                glob = item.GetString();
                break;
            case JsonValueKind.Object:
                glob = null;
                foreach (var property in item.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "glob" when property.Value.ValueKind == JsonValueKind.String:
                            glob = property.Value.GetString();
                            break;
                        case "reason" when property.Value.ValueKind == JsonValueKind.String:
                            pathReason = NullIfBlank(property.Value.GetString());
                            break;
                        case "existingOnBase" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                            existingOnBase = property.Value.GetBoolean();
                            break;
                        case "except" when property.Value.ValueKind == JsonValueKind.Array:
                            foreach (var entry in property.Value.EnumerateArray())
                            {
                                var compiled = PathGlob.TryCreate(entry.ValueKind == JsonValueKind.String ? entry.GetString() : null, out var exceptReason);
                                if (compiled is null)
                                {
                                    reasons.Add($"{where}.except: {exceptReason}");
                                }
                                else
                                {
                                    except.Add(compiled);
                                }
                            }

                            break;
                        default:
                            reasons.Add($"{where}: unknown or mistyped property '{property.Name}' (glob: string, existingOnBase: boolean, except: string[], reason: string).");
                            break;
                    }
                }

                break;
            default:
                reasons.Add($"{where} must be a glob string or an object {{glob, existingOnBase?, except?, reason?}}.");
                return null;
        }

        var compiledGlob = PathGlob.TryCreate(glob, out var reason);
        if (compiledGlob is null)
        {
            reasons.Add($"{where}.glob: {reason}");
            return null;
        }

        return new PresetProtectedPath(compiledGlob, existingOnBase, except, pathReason);
    }

    private static Result<Preset> Fail(string folderId, List<string> reasons) =>
        Result.Failure<Preset>(new DomainError(ErrorCodes.Validation, $"The preset '{folderId}' is invalid: {reasons[0]}") { Details = reasons });

    // ---------- preset.json as written ----------

    private sealed record RawPreset
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Version { get; init; }

        public string? Description { get; init; }

        public IReadOnlyList<string?>? Stack { get; init; }

        public IReadOnlyList<string?>? DeployTargets { get; init; }

        public IReadOnlyList<RawEnv>? RequiredEnv { get; init; }

        public IReadOnlyList<RawSection>? SpecSections { get; init; }

        public string? SpecConventions { get; init; }

        public RawPlanRules? PlanRules { get; init; }

        public IReadOnlyList<RawAgent>? Agents { get; init; }

        public RawReview? Review { get; init; }

        public IReadOnlyList<JsonElement>? ProtectedPaths { get; init; }

        [JsonPropertyName("$schema")]
        public string? Schema { get; init; }
    }

    private sealed record RawEnv(string? Key, IReadOnlyList<string?>? Targets, bool? Sensitive, string? Description);

    private sealed record RawSection(string? Title, string? Guidance);

    private sealed record RawPlanRules
    {
        public bool? ScaffoldFirstWhenEmpty { get; init; }

        public string? Scaffolder { get; init; }

        public IReadOnlyList<RawOrder>? Order { get; init; }

        public IReadOnlyList<RawExclusive>? Exclusive { get; init; }

        public RawOneFile? OneMigrationPerStep { get; init; }

        public IReadOnlyList<RawFileLimit>? MaxFilesPerStep { get; init; }
    }

    private sealed record RawFileLimit(string? Glob, int? Max);

    private sealed record RawOrder(IReadOnlyList<string>? Before, IReadOnlyList<string>? After, bool? Soft, string? Reason);

    private sealed record RawExclusive(string? Agent, IReadOnlyList<string>? NotWithGlobs, string? Reason);

    private sealed record RawOneFile(string? Glob);

    private sealed record RawAgent(string? Key, string? Name, string? Description, IReadOnlyList<string>? Owns, IReadOnlyList<string?>? Skills, string? Prompt);

    private sealed record RawReview(IReadOnlyList<RawChecklistItem>? Checklist, IReadOnlyList<string?>? Deterministic);

    private sealed record RawChecklistItem(string? Category, string? Item, string? Severity);
}
