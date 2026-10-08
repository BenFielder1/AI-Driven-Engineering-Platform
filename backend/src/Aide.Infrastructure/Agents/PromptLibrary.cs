using Aide.Domain.Agents;
using Aide.Domain.Common;

namespace Aide.Infrastructure.Agents;

/// <summary>The versioned prompt templates shipped in the repo (FR-24, D7): <c>Agents/Prompts/&lt;agent&gt;.md</c>, embedded.</summary>
public interface IPromptLibrary
{
    PromptTemplate Template(AgentKind agent);
}

/// <summary>
/// Loads and parses every agent's template once from the assembly's embedded resources. A missing or malformed
/// template is a build/packaging bug and throws on first use (and in the template unit tests).
/// </summary>
public sealed class EmbeddedPromptLibrary : IPromptLibrary
{
    private static readonly Lazy<Dictionary<AgentKind, PromptTemplate>> Templates = new(LoadAll);

    public PromptTemplate Template(AgentKind agent) =>
        Templates.Value.TryGetValue(agent, out var template)
            ? template
            : throw new InvalidOperationException($"No prompt template for {agent.ToWire()}.");

    /// <summary>The embedded resource name of an agent's template.</summary>
    public static string ResourceName(AgentKind agent) => $"Aide.Infrastructure.Agents.Prompts.{agent.ToWire()}.md";

    /// <summary>The raw template source (for tests and diagnostics).</summary>
    public static string Source(AgentKind agent)
    {
        var name = ResourceName(agent);
        using var stream = typeof(EmbeddedPromptLibrary).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The prompt template resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<AgentKind, PromptTemplate> LoadAll()
    {
        var templates = new Dictionary<AgentKind, PromptTemplate>();
        foreach (var agent in Enum.GetValues<AgentKind>())
        {
            var parsed = PromptTemplate.Parse(agent, Source(agent));
            templates[agent] = parsed.IsSuccess
                ? parsed.Value
                : throw new InvalidOperationException(parsed.Error!.Message);
        }

        return templates;
    }
}
