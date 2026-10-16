using System.Text.Json;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Agents;

namespace Aide.Infrastructure.RepoTools;

/// <summary>The definitions of the read-only repo tools sent to the model (FR-71).</summary>
public static class RepoToolDefinitions
{
    public static LlmToolDefinition ListFiles { get; } = new(
        RepoToolNames.ListFiles,
        $"List files and directories of the project repository (read-only). Directories end with '/'. Paths are relative to the repository root ('.' is the root). Depth 1-{RepoToolLimits.MaxListDepth}, at most {RepoToolLimits.MaxListEntries} entries.",
        Schema($$"""
            {"type":"object","properties":{
              "path":{"type":"string","description":"Directory relative to the repository root, e.g. '.' or 'src'."},
              "depth":{"type":"integer","minimum":1,"maximum":{{RepoToolLimits.MaxListDepth}},"description":"How many levels to list (default {{RepoToolLimits.DefaultListDepth}})."}
            },"required":["path"],"additionalProperties":false}
            """));

    public static LlmToolDefinition ReadFile { get; } = new(
        RepoToolNames.ReadFile,
        $"Read a text file of the project repository (read-only), optionally a line range (1-based, inclusive). At most {RepoToolLimits.MaxReadLines} lines or {RepoToolLimits.MaxReadBytes / 1024} KB per call; the header says when the result was truncated. Secrets are redacted.",
        Schema("""
            {"type":"object","properties":{
              "path":{"type":"string","description":"File path relative to the repository root."},
              "startLine":{"type":"integer","minimum":1,"description":"First line (default 1)."},
              "endLine":{"type":"integer","minimum":1,"description":"Last line (default: the end of the file)."}
            },"required":["path"],"additionalProperties":false}
            """));

    public static LlmToolDefinition SearchCode { get; } = new(
        RepoToolNames.SearchCode,
        $"Search the text files of the project repository (read-only) for a plain-text query (case-insensitive) or a regular expression. Returns up to {RepoToolLimits.MaxSearchMatches} matches as 'path:line: text'.",
        Schema("""
            {"type":"object","properties":{
              "query":{"type":"string","description":"Text to find, or a .NET regular expression when regex is true."},
              "pathGlob":{"type":"string","description":"Only files matching this glob, e.g. 'src/**/*.cs' or '*.md'."},
              "regex":{"type":"boolean","description":"Treat query as a regular expression (default false)."}
            },"required":["query"],"additionalProperties":false}
            """));

    public static IReadOnlyList<LlmToolDefinition> All { get; } = [ListFiles, ReadFile, SearchCode];

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
