using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;

namespace Aide.Domain.Agents;

/// <summary>A placeholder in a prompt template: <c>{{NAME}}</c> (required) or <c>{{NAME?}}</c> (optional, empty when absent).</summary>
public sealed record PromptPlaceholder(string Name, bool Optional);

/// <summary>A rendered prompt: the system prompt and the first user message.</summary>
public sealed record RenderedPrompt(string System, string User);

/// <summary>
/// FR-24, D7: a versioned prompt template, one per agent. The source is Markdown with front matter:
/// <code>
/// ---
/// version: 1
/// agent: spec_generator
/// ---
/// (system prompt)
/// &lt;!-- user --&gt;
/// (user message)
/// </code>
/// <c>version</c> is required and is recorded on every run; <c>agent</c>, when present, must match. The line
/// <c>&lt;!-- user --&gt;</c> separates the system prompt from the user message. Placeholders are
/// <c>{{UPPER_SNAKE}}</c>; a trailing <c>?</c> makes one optional.
/// </summary>
public sealed partial class PromptTemplate
{
    public const string UserMarker = "<!-- user -->";

    private readonly Regex? _closingDataTag;

    private PromptTemplate(AgentKind agent, string version, string system, string user)
    {
        Agent = agent;
        Version = version;
        System = system;
        User = user;
        Placeholders = [.. PlaceholderPattern().Matches(system + "\n" + user)
            .Select(m => new PromptPlaceholder(m.Groups["name"].Value, m.Groups["optional"].Success))
            .DistinctBy(p => (p.Name, p.Optional))];
        DataTags = [.. ClosingTagPattern().Matches(system + "\n" + user)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        _closingDataTag = DataTags.Count == 0
            ? null
            : new Regex(
                $@"<\s*/\s*(?=(?:{string.Join('|', DataTags.Select(Regex.Escape))})(?![a-z0-9_]))",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
    }

    public AgentKind Agent { get; }

    /// <summary>The template version from the front matter (stored as the run's <c>prompt_version</c>).</summary>
    public string Version { get; }

    public string System { get; }

    public string User { get; }

    public IReadOnlyList<PromptPlaceholder> Placeholders { get; }

    /// <summary>
    /// The names of the data tags the template closes (<c>&lt;/idea&gt;</c> → <c>idea</c>). Values are wrapped in these
    /// tags, and a value can't close one of them early (see <see cref="Neutralise"/>).
    /// </summary>
    public IReadOnlyList<string> DataTags { get; }

    /// <summary>Names of the placeholders that must have a value.</summary>
    public IReadOnlyList<string> RequiredInputs =>
        [.. Placeholders.Where(p => !p.Optional).Select(p => p.Name).Distinct(StringComparer.Ordinal)];

    /// <summary>Parses a template source. Fails with <c>VALIDATION</c> on malformed front matter or a missing marker.</summary>
    public static Result<PromptTemplate> Parse(AgentKind agent, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = source.ReplaceLineEndings("\n").TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return Invalid(agent, "The template must start with front matter (---).");
        }

        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return Invalid(agent, "The front matter is not closed (---).");
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return Invalid(agent, $"Front matter line '{line}' is not 'key: value'.");
            }

            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (!fields.TryGetValue("version", out var version) || string.IsNullOrWhiteSpace(version) || version.Length > 50)
        {
            return Invalid(agent, "The front matter needs a version (at most 50 characters).");
        }

        if (fields.TryGetValue("agent", out var declared) && declared != agent.ToWire())
        {
            return Invalid(agent, $"The template is for agent '{declared}'.");
        }

        var body = text[(end + 5)..];
        var parts = body.Split("\n" + UserMarker + "\n", StringSplitOptions.None);
        if (parts.Length != 2)
        {
            return Invalid(agent, $"The body needs exactly one '{UserMarker}' line.");
        }

        return Result.Success(new PromptTemplate(agent, version, parts[0].Trim(), parts[1].Trim()));
    }

    /// <summary>
    /// Fills every placeholder in one pass (values are inserted literally: a value containing <c>{{X}}</c> is never
    /// expanded). A value can't end the data tag it sits in: closing sequences of the template's data tags inside a
    /// value (<c>&lt;/idea&gt;</c>, <c>&lt;/ IDEA&gt;</c>, …) are neutralised to <c>&lt;\/idea&gt;</c>. A required placeholder whose value is absent, <c>null</c> or blank fails with <c>MISSING_INPUT</c>
    /// listing every missing name, so the provider is never called with a half-filled prompt.
    /// </summary>
    public Result<RenderedPrompt> Render(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var missing = RequiredInputs
            .Where(name => !values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .ToList();
        if (missing.Count > 0)
        {
            return Result.Failure<RenderedPrompt>(new DomainError(
                ErrorCodes.MissingInput,
                $"The {Agent.ToWire()} prompt is missing {string.Join(", ", missing)}.")
            {
                Details = missing,
            });
        }

        string Fill(string text) => PlaceholderPattern().Replace(text, m =>
            values.TryGetValue(m.Groups["name"].Value, out var value) && value is not null ? Neutralise(value.Trim()) : string.Empty);

        return Result.Success(new RenderedPrompt(Fill(System), Fill(User)));
    }

    /// <summary>
    /// Prompt-injection hardening (FR-24): rewrites every closing sequence of one of the template's
    /// <see cref="DataTags"/> in <paramref name="value"/> (any case, optional whitespace after <c>&lt;</c> or <c>/</c>)
    /// as <c>&lt;\/name</c>, so input data can't close its data tag and pose as instructions.
    /// </summary>
    public string Neutralise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _closingDataTag is null ? value : _closingDataTag.Replace(value, @"<\/");
    }

    private static Result<PromptTemplate> Invalid(AgentKind agent, string reason) =>
        Result.Failure<PromptTemplate>(new DomainError(ErrorCodes.Validation, $"Invalid {agent.ToWire()} prompt template: {reason}"));

    [GeneratedRegex(@"\{\{(?<name>[A-Z][A-Z0-9_]*)(?<optional>\?)?\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"</(?<name>[a-z][a-z0-9_]*)>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClosingTagPattern();
}

/// <summary>Builds the text of placeholder values from structured inputs.</summary>
public static class PromptValues
{
    /// <summary>Numbered list (<c>1. …</c>) of non-blank items, or <c>null</c> when there are none.</summary>
    public static string? NumberedList(IEnumerable<string?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            sb.Append(++n).Append(". ").Append(item!.Trim()).Append('\n');
        }

        return n == 0 ? null : sb.ToString().TrimEnd();
    }
}
