namespace Aide.Domain.Stack;

/// <summary>
/// §4.23, FR-129: the frozen v2 preset data kept for migrated projects (<c>Stack/legacy/presets.json</c>, loaded with the
/// catalog by <see cref="StackCatalogLoader"/>). It never changes again: plans locked before the upgrade keep their v2 agent
/// keys, which are mapped to v4 specialists at run time (<see cref="Map"/>), and their owned-path checks use the v2 globs
/// (<see cref="LegacyPreset.Owns"/>) until the plan is replaced, so a locked plan doesn't suddenly produce
/// <c>AGENT_SCOPE</c> warnings.
/// </summary>
public sealed record LegacyPresets(IReadOnlyList<LegacyAgentKeyRule> AgentKeys, IReadOnlyDictionary<string, LegacyPreset> Presets)
{
    /// <summary>No legacy data (a catalog without <c>legacy/</c>).</summary>
    public static LegacyPresets Empty { get; } = new([], new Dictionary<string, LegacyPreset>(StringComparer.Ordinal));

    /// <summary>The frozen preset <paramref name="presetId"/>, or <c>null</c> (no id, or not a v2 preset).</summary>
    public LegacyPreset? Find(string? presetId) =>
        presetId is not null && Presets.TryGetValue(presetId, out var preset) ? preset : null;

    /// <summary>Whether <paramref name="agentKey"/> is a v2 preset agent key (§4.23 table).</summary>
    public bool IsLegacyKey(string? agentKey) => agentKey is not null && AgentKeys.Any(r => r.From == agentKey);

    /// <summary>
    /// §4.23: the v4 specialist a step planned with the v2 key <paramref name="legacyKey"/> runs as, decided by its declared
    /// files where the table says so (<c>devops-engineer</c>: <c>ci-engineer</c> when every declared file is under
    /// <c>.github/**</c>, else <c>deploy-engineer</c>; a step with both gets <c>deploy-engineer</c> and a warning).
    /// <c>null</c> when <paramref name="legacyKey"/> isn't a v2 key.
    /// </summary>
    public LegacyAgentMapping? Map(string? legacyKey, IEnumerable<string> declaredFiles)
    {
        ArgumentNullException.ThrowIfNull(declaredFiles);
        if (AgentKeys.FirstOrDefault(r => r.From == legacyKey) is not { } rule)
        {
            return null;
        }

        var files = declaredFiles.Select(Execution.PathRules.Normalize).ToList();
        foreach (var byFiles in rule.ByFiles)
        {
            var matching = files.Count(byFiles.Glob.IsMatch);
            if (matching > 0 && matching == files.Count)
            {
                return new LegacyAgentMapping(byFiles.To, rule.From, Warning: null);
            }

            if (matching > 0)
            {
                return new LegacyAgentMapping(
                    rule.To,
                    rule.From,
                    $"The v2 step agent '{rule.From}' declares files under {byFiles.Glob.Pattern} and elsewhere, so it runs as '{rule.To}' rather than '{byFiles.To}'.");
            }
        }

        return new LegacyAgentMapping(rule.To, rule.From, Warning: null);
    }

    /// <summary>
    /// §4.23 at run time: <see cref="Map"/>, then, when the mapped specialist isn't in the stack (<paramref name="specialists"/>:
    /// e.g. <c>deploy-engineer</c> without a deploy host), the rule's other specialist that is, else
    /// <see cref="Execution.PlanAgents.Generalist"/> (the base prompt), with a warning. <c>null</c> when
    /// <paramref name="legacyKey"/> isn't a v2 key.
    /// </summary>
    public LegacyAgentMapping? MapInPlay(string? legacyKey, IEnumerable<string> declaredFiles, IReadOnlyCollection<string> specialists)
    {
        ArgumentNullException.ThrowIfNull(specialists);
        if (Map(legacyKey, declaredFiles) is not { } mapped)
        {
            return null;
        }

        if (specialists.Contains(mapped.AgentKey, StringComparer.Ordinal))
        {
            return mapped;
        }

        var rule = AgentKeys.First(r => r.From == mapped.LegacyAgentKey);
        var other = new[] { rule.To }.Concat(rule.ByFiles.Select(b => b.To)).FirstOrDefault(k => specialists.Contains(k, StringComparer.Ordinal));
        var runAs = other ?? Execution.PlanAgents.Generalist;
        return mapped with
        {
            AgentKey = runAs,
            Warning = $"The v2 step agent '{mapped.LegacyAgentKey}' maps to '{mapped.AgentKey}', which this stack doesn't have, so it runs as '{runAs}'.",
        };
    }
}

/// <summary>
/// A row of the §4.23 agent-key table: v2 key <see cref="From"/> runs as <see cref="To"/>, unless every declared file of the
/// step matches one of <see cref="ByFiles"/> (first match wins), which then names the specialist.
/// </summary>
public sealed record LegacyAgentKeyRule(string From, string To, IReadOnlyList<LegacyAgentByFiles> ByFiles);

/// <summary>A by-files exception of a <see cref="LegacyAgentKeyRule"/>: steps whose declared files all match <see cref="Glob"/> run as <see cref="To"/>.</summary>
public sealed record LegacyAgentByFiles(PathGlob Glob, string To);

/// <summary>
/// A frozen v2 preset (§4.23): its id, name and last shipped version, and the owned globs of each v2 agent key (the v2
/// <c>agents[].owns</c>, with <c>!</c> exclusions), used for the owned-path checks of its migrated in-flight plans.
/// </summary>
public sealed record LegacyPreset(string Id, string Name, string Version, IReadOnlyDictionary<string, GlobSet> Owns)
{
    /// <summary>The owned globs of v2 agent <paramref name="legacyKey"/>, or <c>null</c> when the preset had no such agent.</summary>
    public GlobSet? OwnsOf(string? legacyKey) =>
        legacyKey is not null && Owns.TryGetValue(legacyKey, out var owns) ? owns : null;

    /// <summary>"Preset &lt;id&gt;@&lt;version&gt; (retired, migrated)", as history shows it (FR-129).</summary>
    public static string HistoryLabel(string presetId, string? version) =>
        $"Preset {presetId}{(string.IsNullOrWhiteSpace(version) ? string.Empty : "@" + version)} (retired, migrated)";
}

/// <summary>
/// §4.23: how a step planned with a v2 agent key runs: as <see cref="AgentKey"/> (a v4 specialist), recording
/// <see cref="LegacyAgentKey"/>; <see cref="Warning"/> when the declared files made the choice ambiguous.
/// </summary>
public sealed record LegacyAgentMapping(string AgentKey, string LegacyAgentKey, string? Warning);
