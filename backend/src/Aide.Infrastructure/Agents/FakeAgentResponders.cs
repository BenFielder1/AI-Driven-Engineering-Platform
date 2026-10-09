using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Agents;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// Reads the inputs back out of a rendered prompt for the Fake responders: every value sits in its data tag
/// (<c>&lt;idea&gt;…&lt;/idea&gt;</c>, FR-24), so a responder can produce project-specific content offline.
/// </summary>
internal static partial class FakePrompt
{
    private static readonly HashSet<string> NotAResource = new(StringComparer.OrdinalIgnoreCase)
    {
        "demo", "api", "apis", "app", "apps", "application", "service", "services", "the", "a", "an", "my", "our", "simple",
        "small", "tiny", "basic", "platform", "system", "tool", "web", "rest", "new", "project", "for", "of", "and", "manager",
    };

    /// <summary>The trimmed content of the data tag <paramref name="name"/> in the user message, or empty.</summary>
    public static string Tag(LlmRequest request, string name)
    {
        var text = string.Join("\n", request.Messages.Select(m => m.Content));
        var open = $"<{name}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += open.Length;
        var end = text.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : text[start..end].Trim();
    }

    /// <summary>The text of the first <c># </c> heading, or <paramref name="fallback"/>.</summary>
    public static string Title(string markdown, string fallback)
    {
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }
        }

        return fallback;
    }

    /// <summary>The body of the <c>## heading</c> section (up to the next <c>##</c>), or empty.</summary>
    public static string Section(string markdown, string heading)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var body = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = line[3..].Trim().Equals(heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inside)
            {
                body.Add(line);
            }
        }

        return string.Join("\n", body).Trim();
    }

    /// <summary>The main resource a project is about, from its name ("Demo: Todo API" → <c>todo</c>), else <c>item</c>.</summary>
    public static string Resource(string name)
    {
        foreach (Match word in WordPattern().Matches(name.ToLowerInvariant()))
        {
            if (!NotAResource.Contains(word.Value) && word.Value.Length > 1)
            {
                var value = word.Value;
                return value.Length > 3 && value.EndsWith('s') && !value.EndsWith("ss", StringComparison.Ordinal) ? value[..^1] : value;
            }
        }

        return "item";
    }

    public static string Plural(string resource) => resource.EndsWith('s') ? resource + "es" : resource + "s";

    public static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The first sentence (or first 200 characters) of <paramref name="text"/>, on one line.</summary>
    public static string FirstSentence(string text)
    {
        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var stop = oneLine.IndexOf(". ", StringComparison.Ordinal);
        var sentence = stop > 0 ? oneLine[..(stop + 1)] : oneLine;
        return sentence.Length > 200 ? sentence[..200] + "…" : sentence;
    }

    [GeneratedRegex("[a-z][a-z0-9]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordPattern();
}

/// <summary>
/// Fake Idea Refinement (FR-27, D13): a project request built from the project name and idea, as the JSON the prompt asks
/// for. The first round asks three clarifying questions (<c>q1</c>–<c>q3</c>); a round with answers folds them into the
/// draft (<c>- Clarified: …</c>, kept in later rounds) and asks again only what is still unanswered; a round on an
/// existing draft without answers asks nothing.
/// </summary>
public sealed partial class FakeIdeaRefinementResponder : IFakeAgentResponder
{
    public AgentKind Agent => AgentKind.IdeaRefinement;

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = FakePrompt.Tag(request, "project_name") is { Length: > 0 } n ? n : "Untitled project";
        var idea = FakePrompt.Tag(request, "idea");
        var currentDraft = FakePrompt.Tag(request, "current_draft");
        var feedback = FakePrompt.Tag(request, "feedback");
        var answers = AnswerPattern().Matches(FakePrompt.Tag(request, "answers"))
            .Select(m => (Id: m.Groups["id"].Success ? m.Groups["id"].Value : null, Question: m.Groups["q"].Value.Trim(), Answer: m.Groups["a"].Value.Trim()))
            .ToList();

        var catalogue = new (string Id, string Question, string Reason)[]
        {
            ("q1", $"Who are the main users of {name}, and do they need to sign in?", "Decides whether accounts, roles and authentication are in scope."),
            ("q2", $"Which platform should {name} target first: a web UI, a mobile app, or an API only?", "Decides the architecture and what the first release delivers."),
            ("q3", "What scale and hosting constraints apply (number of users, data volume, where it runs)?", "Decides the data store, deployment and performance targets."),
        };
        var answeredIds = answers.Where(a => a.Id is not null).Select(a => a.Id!).ToHashSet(StringComparer.Ordinal);
        var questions = currentDraft.Length == 0 || answers.Count > 0
            ? catalogue.Where(q => !answeredIds.Contains(q.Id)).ToList()
            : [];

        var clarified = currentDraft.ReplaceLineEndings("\n").Split('\n')
            .Where(l => l.StartsWith("- Clarified: ", StringComparison.Ordinal))
            .ToList();
        clarified.AddRange(answers.Select(a => $"- Clarified: {a.Question} — {a.Answer}"));

        var resource = FakePrompt.Resource(name);
        var plural = FakePrompt.Plural(resource);
        var sb = new StringBuilder();
        sb.Append("# ").Append(name).Append("\n\n");
        sb.Append("## Summary\n").Append(idea.Length > 0 ? idea : $"{name} manages {plural}.").Append("\n\n");
        sb.Append("## Problem and goals\n")
            .Append($"People need a dependable way to manage their {plural}. Success: every core feature below works end to end and is covered by automated tests.\n\n");
        sb.Append("## Users and roles\n- **User**: manages their own ").Append(plural).Append(".\n\n");
        sb.Append("## Core features\n**Must**\n")
            .Append($"- Create, list, view, update and delete {plural}.\n")
            .Append($"- Validate input and return clear errors.\n")
            .Append("**Should**\n")
            .Append($"- Filter and sort {plural}.\n")
            .Append("**Could**\n")
            .Append("- Export data as JSON.\n\n");
        sb.Append("## Key user flows\n")
            .Append($"1. A user creates a {resource} and sees it in the list.\n")
            .Append($"2. A user updates or deletes an existing {resource}.\n\n");
        sb.Append("## Data\n").Append($"- **{FakePrompt.Capitalise(resource)}**: id, title, details, created and updated timestamps.\n\n");
        sb.Append("## Constraints and assumptions\n- Assumption: a small, single-tenant deployment.\n");
        foreach (var line in clarified.Distinct(StringComparer.Ordinal))
        {
            sb.Append(line).Append('\n');
        }

        if (feedback.Length > 0)
        {
            sb.Append("- Reviewer feedback addressed: ").Append(FakePrompt.FirstSentence(feedback)).Append('\n');
        }

        sb.Append("\n## Out of scope\n- Integrations with third-party services.\n\n");
        sb.Append("## Open questions\n");
        if (questions.Count == 0)
        {
            sb.Append("- None.\n");
        }

        foreach (var q in questions)
        {
            sb.Append("- ").Append(q.Question).Append('\n');
        }

        return JsonSerializer.Serialize(new
        {
            projectRequest = sb.ToString(),
            questions = questions.Select(q => new { id = q.Id, question = q.Question, reason = q.Reason }),
        });
    }

    [GeneratedRegex(@"^Q(?: \((?<id>[^)]+)\))?: (?<q>.*)\nA: (?<a>.*)$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnswerPattern();
}

/// <summary>
/// Fake Spec Generator (FR-28, D13): a technical specification of the project request with all eleven required sections,
/// a <c>### Dependencies</c> list and REST endpoints for the project's main resource.
/// </summary>
public sealed class FakeSpecGeneratorResponder : IFakeAgentResponder
{
    public AgentKind Agent => AgentKind.SpecGenerator;

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectRequest = FakePrompt.Tag(request, "project_request");
        var name = FakePrompt.Title(projectRequest, "the project");
        var summary = FakePrompt.Section(projectRequest, "Summary") is { Length: > 0 } s ? FakePrompt.FirstSentence(s) : $"{name}.";
        var r = FakePrompt.Resource(name);
        var rs = FakePrompt.Plural(r);
        var entity = FakePrompt.Capitalise(r);
        return $"""
            ## 1. Overview
            {name}: {summary} Users manage {rs} through a JSON REST API. Non-goals: third-party integrations.

            ## 2. Functional Requirements
            - **FR-1** Create a {r}. Given a valid title, when it is posted, then it is stored and returned with 201.
            - **FR-2** List {rs}, newest first, with optional filtering.
            - **FR-3** View, update and delete a {r} by id; an unknown id returns 404.
            - **FR-4** Invalid input returns 400 with the field errors.

            ## 3. Technical Architecture
            A Node.js 22 HTTP service using Express 5, with an in-process data layer behind a repository module.

            ### Dependencies
            - express
            - jest
            - supertest

            ## 4. Data Models
            **{entity}**: `id` (string, required, unique), `title` (string, required, 1–200 chars), `details` (string, optional), `createdAt`, `updatedAt` (timestamps, required).

            ## 5. API Contracts
            | Method | Path | Success | Errors |
            | --- | --- | --- | --- |
            | GET | /api/{rs} | 200 list | — |
            | POST | /api/{rs} | 201 {r} | 400 |
            | GET | /api/{rs}/{"{id}"} | 200 {r} | 404 |
            | PUT | /api/{rs}/{"{id}"} | 200 {r} | 400, 404 |
            | DELETE | /api/{rs}/{"{id}"} | 204 | 404 |

            ## 6. Feature Specifications
            Titles are trimmed; empty titles are rejected. Updates replace title and details and refresh `updatedAt`.

            ## 7. Design System & UI/UX
            No user interface: the deliverable is an API, documented in `docs/api.md`.

            ## 8. Security & Authentication
            No authentication in the first release (single user, local deployment). All input is validated; errors never echo internals.

            ## 9. Testing Strategy
            Unit tests for the {r} model and integration tests for every endpoint with Jest and Supertest.

            ## 10. Deployment & Infrastructure
            `npm start` runs the server on `PORT` (default 3000). Configuration comes from environment variables; no secrets are committed.

            ## 11. Open Questions
            - Persistence beyond memory: default to a file-backed store if needed.
            """;
    }
}

/// <summary>
/// Fake Plan Generator (FR-29, D13): a valid four-step plan for the project's main resource, as the JSON the prompt asks
/// for. Its files are ordinary repository files (no protected paths), so the Local Git provider can commit them later.
/// </summary>
public sealed class FakePlanGeneratorResponder : IFakeAgentResponder
{
    public AgentKind Agent => AgentKind.PlanGenerator;

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = FakePrompt.Title(FakePrompt.Tag(request, "project_request"), "the project");
        var r = FakePrompt.Resource(name);
        var rs = FakePrompt.Plural(r);
        var steps = new object[]
        {
            Step(1, "Project skeleton and configuration", $"Create the Node.js project for {name}: package.json with start and test scripts, a README and an Express server entry point listening on PORT.", [], ("package.json", "create"), ("README.md", "create"), ("src/server.js", "create")),
            Step(2, $"{FakePrompt.Capitalise(r)} model and validation", $"Implement the {r} model with validation of title and details, and an in-memory repository (FR-1, FR-4), with unit tests.", [1], ($"src/models/{r}.js", "create"), ($"test/{r}.test.js", "create")),
            Step(3, $"{FakePrompt.Capitalise(rs)} REST API", $"Add the /api/{rs} routes for create, list, view, update and delete (FR-1–FR-4) and mount them in the server, with integration tests.", [1, 2], ($"src/routes/{rs}.js", "create"), ("src/server.js", "modify"), ($"test/{rs}.api.test.js", "create")),
            Step(4, "API documentation", $"Document every /api/{rs} endpoint with examples in docs/api.md and link it from the README.", [1, 3], ("docs/api.md", "create"), ("README.md", "modify")),
        };
        return JsonSerializer.Serialize(new { steps });
    }

    private static object Step(int number, string title, string task, int[] dependencies, params (string Path, string Action)[] files) => new
    {
        number,
        title,
        task,
        files = files.Select(f => new { path = f.Path, action = f.Action }),
        dependencies,
        userInstructions = (string?)null,
    };
}

/// <summary>
/// Fake Code Generator (FR-31, D13): implements the requested step by writing exactly its declared files (read from the
/// prompt's <c>&lt;declared_files&gt;</c> list), with deterministic content per file type, so execution runs end to end with
/// the Fake and Local providers. The content never matches a secret pattern, so the Guardian accepts it.
/// </summary>
public sealed partial class FakeCodeGeneratorResponder : IFakeAgentResponder
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public AgentKind Agent => AgentKind.CodeGenerator;

    public string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = string.Join("\n", request.Messages.Select(m => m.Content));
        var number = StepPattern().Match(prompt) is { Success: true } m
            ? int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 1;
        var title = FakePrompt.Tag(request, "step_title") is { Length: > 0 } t ? t : $"Step {number}";
        var name = FakePrompt.Title(FakePrompt.Tag(request, "project_request"), "project");
        var files = FilePattern().Matches(FakePrompt.Tag(request, "declared_files"))
            .Select(f => (Path: f.Groups["path"].Value.Trim(), Action: f.Groups["action"].Value))
            .Select(f => new { path = f.Path, content = Content(f.Path, f.Action, name, number, title) })
            .ToList();
        return JsonSerializer.Serialize(new
        {
            stepNumber = number,
            files,
            notes = $"Fake implementation of step {number} ({title}): wrote {files.Count} declared file(s).",
        });
    }

    private static string Content(string path, string action, string project, int step, string title)
    {
        var verb = action == "modify" ? "Updated" : "Created";
        var fileName = path.Split('/')[^1];
        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(
                new { name = FakePrompt.Resource(project) + "-service", version = "0.1.0", @private = true, description = $"{project} (step {step}: {title})", scripts = new { start = "node src/server.js", test = "jest" } },
                Indented) + "\n";
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".md" => $"# {title}\n\n{verb} by AIDE in step {step} of the {project} plan.\n",
            ".json" => JsonSerializer.Serialize(new { step, title }) + "\n",
            ".js" or ".ts" or ".mjs" or ".cjs" or ".jsx" or ".tsx" or ".cs" or ".java" or ".go" or ".rs" or ".c" or ".cpp" =>
                $"// {title}\n// {verb} by AIDE in step {step} of the {project} plan.\n\nexport const step{step} = '{fileName}';\n",
            ".py" or ".rb" or ".sh" or ".yml" or ".yaml" or ".toml" => $"# {title}\n# {verb} by AIDE in step {step} of the {project} plan.\n",
            _ => $"{title}\n{verb} by AIDE in step {step} of the {project} plan.\n",
        };
    }

    [GeneratedRegex(@"<step number=""(?<n>\d+)"">", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StepPattern();

    [GeneratedRegex(@"^- (?<path>.+) \((?<action>create|modify)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilePattern();
}
