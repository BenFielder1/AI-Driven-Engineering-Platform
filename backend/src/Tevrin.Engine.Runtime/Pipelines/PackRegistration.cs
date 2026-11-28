using System.Text;
using System.Text.RegularExpressions;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>The kinds of named pipeline component the host binds in DI (FR-278).</summary>
public enum PipelineComponentKind
{
    Guard = 0,
    StageHandler = 1,
    Tool = 2,
}

/// <summary>
/// FR-278: a guard, stage handler or tool implementation bound to its name in DI
/// (<see cref="PipelineServiceCollectionExtensions.AddPipelineGuard{T}"/> and friends).
/// </summary>
public sealed record PipelineComponentRegistration(PipelineComponentKind Kind, string Name, Type ImplementationType);

/// <summary>A definition a pack registered, before parsing.</summary>
public sealed record PackDefinitionSource(string PackId, string Name, string Yaml);

/// <summary>FR-278: everything one pack registered.</summary>
public sealed class PackRegistration : IPackRegistry
{
    private readonly List<PackDefinitionSource> _definitions = [];
    private readonly Dictionary<string, string> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prompts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _contextNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _guards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolTier> _toolTiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalHandlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _jobTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _producedArtifacts = new(StringComparer.Ordinal);

    public PackRegistration(string packId)
    {
        if (string.IsNullOrEmpty(packId) || !PipelineNames.PackId().IsMatch(packId))
        {
            throw new ArgumentException($"Pack id '{packId}' must be kebab-case.", nameof(packId));
        }

        PackId = packId;
    }

    public string PackId { get; }

    public IReadOnlyList<PackDefinitionSource> Definitions => _definitions;

    public IReadOnlyDictionary<string, string> Schemas => _schemas;

    public IReadOnlyDictionary<string, string> Prompts => _prompts;

    public IReadOnlyCollection<string> ContextNames => _contextNames;

    public IReadOnlyCollection<string> Guards => _guards;

    public IReadOnlyCollection<string> Handlers => _handlers;

    public IReadOnlyCollection<string> Tools => _tools;

    /// <summary>FR-358: each registered tool's tier (<see cref="ToolTierRules.DefaultRegisteredTier"/> when the pack
    /// stated none).</summary>
    public IReadOnlyDictionary<string, ToolTier> ToolTiers => _toolTiers;

    /// <summary>FR-360: the handlers registered with <c>external: true</c>.</summary>
    public IReadOnlyCollection<string> ExternalHandlers => _externalHandlers;

    /// <summary>FR-360: the tools registered with <c>external: true</c>.</summary>
    public IReadOnlyCollection<string> ExternalTools => _externalTools;

    public IReadOnlyCollection<string> JobTypes => _jobTypes;

    public IReadOnlyCollection<string> Settings => _settings;

    public IReadOnlyCollection<string> ProducedArtifacts => _producedArtifacts;

    public void AddDefinition(string name, string yaml)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(yaml);
        _definitions.Add(new PackDefinitionSource(PackId, name, yaml));
    }

    public void AddDefinition(string name, Stream yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        using var reader = new StreamReader(yaml, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        AddDefinition(name, reader.ReadToEnd());
    }

    public void AddSchema(string name, string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(json);
        _schemas[name] = json;
    }

    public void AddPrompt(string name, string promptTemplate)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(promptTemplate);
        _prompts[name] = promptTemplate;
    }

    public void AddContextName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _contextNames.Add(name);
    }

    public void AddGuard(string name) => _guards.Add(Namespaced(name));

    public void AddStageHandler(string name) => AddStageHandler(name, external: false);

    /// <summary>FR-360: registering a name again keeps it external once any registration says so.</summary>
    public void AddStageHandler(string name, bool external)
    {
        _handlers.Add(Namespaced(name));
        if (external)
        {
            _externalHandlers.Add(name);
        }
    }

    public void AddTool(string name) => AddTool(name, ToolTierRules.DefaultRegisteredTier);

    /// <summary>FR-358, FR-360: a tool registered twice keeps its highest tier, and stays external once any registration
    /// says so (a re-registration never weakens a tool's safety).</summary>
    public void AddTool(string name, ToolTier tier, bool external = false)
    {
        if (!System.Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Undefined tool tier.");
        }

        _tools.Add(Namespaced(name));
        _toolTiers[name] = _toolTiers.TryGetValue(name, out var earlier) && earlier > tier ? earlier : tier;
        if (external)
        {
            _externalTools.Add(name);
        }
    }

    public void AddJobType(string jobType)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobType);
        _jobTypes.Add(jobType);
    }

    public void AddSetting(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _settings.Add(name);
    }

    public void AddArtifactProducer(string artifactKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactKey);
        _producedArtifacts.Add(artifactKey);
    }

    private string Namespaced(string name)
    {
        PipelineNames.EnsureComponentName(name);
        if (!name.StartsWith(PackId + ".", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Pack '{PackId}' can only register names in its namespace ('{PackId}.<name>'), not '{name}'.", nameof(name));
        }

        return name;
    }
}

/// <summary>FR-278: name rules for packs and their components.</summary>
internal static partial class PipelineNames
{
    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    public static partial Regex PackId();

    [GeneratedRegex("^[a-z][a-z0-9-]*\\.[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentName();

    /// <summary>A guard, handler or tool name: <c>&lt;pack&gt;.&lt;name&gt;</c>.</summary>
    public static void EnsureComponentName(string name)
    {
        if (string.IsNullOrEmpty(name) || !ComponentName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' must be namespaced as '<pack>.<name>' (lower-case).", nameof(name));
        }
    }
}
