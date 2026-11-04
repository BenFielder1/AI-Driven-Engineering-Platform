using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Domain.Common;

namespace Aide.Domain.Stack;

/// <summary>§4.27 layer status. Wire values: <c>inferred</c>, <c>user</c>, <c>none</c>, <c>fixed</c>, <c>unknown</c>, <c>conflict</c>, <c>unsupported</c>.</summary>
public enum StackProfileLayerStatus
{
    /// <summary>The agent found it, or the user confirmed it.</summary>
    Inferred = 0,

    /// <summary>Set by a user edit.</summary>
    User = 1,

    /// <summary>Evidence shows no such layer.</summary>
    None = 2,

    /// <summary>VCS, from the repository's host.</summary>
    Fixed = 3,

    /// <summary>Can't be determined: has a question. Blocks approval.</summary>
    Unknown = 4,

    /// <summary>Candidates disagree (the agent and the detector, or the detector's own matches). Blocks approval.</summary>
    Conflict = 5,

    /// <summary>The technology isn't in the catalog. Blocks approval unless the profile chooses No stack (<c>generalist</c>).</summary>
    Unsupported = 6,
}

/// <summary>§4.27 confidence: shown, never blocking. Wire values: <c>high</c>, <c>medium</c>, <c>low</c>.</summary>
public enum StackProfileConfidence
{
    High = 0,
    Medium = 1,
    Low = 2,
}

/// <summary>A cited piece of evidence: a repository path at the base commit and what it shows.</summary>
public sealed record StackProfileEvidence(string File, string Text);

/// <summary>A candidate of a <c>conflict</c> layer: the option (<c>none</c> for None), its path, who proposed it (<c>agent</c> or <c>detector</c>) and its evidence.</summary>
public sealed record StackProfileCandidate(string Option, string? Path, string Source, IReadOnlyList<StackProfileEvidence> Evidence);

/// <summary>
/// One layer of a profile (§4.27). <see cref="Option"/> is a catalog option id of the layer's choice, or <c>null</c> (for
/// <c>none</c>, <c>unknown</c>, <c>conflict</c> and <c>unsupported</c>). <see cref="Path"/> is the frontend or backend folder
/// (FR-119); <see cref="Mode"/> the data mode; <see cref="Technology"/> names what an <c>unsupported</c> layer found.
/// </summary>
public sealed record StackProfileLayer
{
    public string? Option { get; init; }

    public string? Path { get; init; }

    public string? Mode { get; init; }

    public required StackProfileLayerStatus Status { get; init; }

    public StackProfileConfidence? Confidence { get; init; }

    public string? Technology { get; init; }

    public IReadOnlyList<StackProfileEvidence> Evidence { get; init; } = [];

    public IReadOnlyList<StackProfileCandidate> Candidates { get; init; } = [];

    public static StackProfileLayer Unknown { get; } = new() { Status = StackProfileLayerStatus.Unknown };
}

/// <summary>The four layers of a profile; the Deployment layer is <see cref="StackProfileDocument.Hosts"/>.</summary>
public sealed record StackProfileLayers(StackProfileLayer Frontend, StackProfileLayer Backend, StackProfileLayer Data, StackProfileLayer Vcs)
{
    /// <summary>The layer named <paramref name="layer"/> (<see cref="StackProfile.Layers"/>).</summary>
    public StackProfileLayer Get(string layer) => layer switch
    {
        StackLayers.Frontend => Frontend,
        StackLayers.Backend => Backend,
        StackLayers.Data => Data,
        StackLayers.Vcs => Vcs,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null),
    };

    /// <summary>The layers with <paramref name="layer"/> replaced.</summary>
    public StackProfileLayers With(string layer, StackProfileLayer value) => layer switch
    {
        StackLayers.Frontend => this with { Frontend = value },
        StackLayers.Backend => this with { Backend = value },
        StackLayers.Data => this with { Data = value },
        StackLayers.Vcs => this with { Vcs = value },
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null),
    };
}

/// <summary>
/// FR-184: the profile's hosts (option ids of the host choices, <c>null</c> = None), the local dev loop (<c>compose</c>,
/// <c>none</c>, or <c>null</c> for the default), the origin model, the parts in separate projects (§4.28), and which host
/// choices the detector found (<see cref="Detected"/>: <c>frontendHost</c>, <c>backendHost</c>, <c>databaseHost</c>).
/// </summary>
public sealed record StackProfileHosts
{
    public string? FrontendHost { get; init; }

    public string? BackendHost { get; init; }

    public string? DatabaseHost { get; init; }

    public string? Local { get; init; }

    public string? OriginModel { get; init; }

    public IReadOnlyList<string> SeparateParts { get; init; } = [];

    public IReadOnlyList<string> Detected { get; init; } = [];

    public static StackProfileHosts Empty { get; } = new();
}

/// <summary>A row of the repository structure table: a path, its role (<see cref="StackProfile.StructureRoles"/>) and a note.</summary>
public sealed record StackProfileStructureEntry(string Path, string Role, string Note);

/// <summary>A question for the user about a layer (<c>frontend</c>, <c>backend</c>, <c>data</c>, <c>vcs</c> or <c>hosts</c>).</summary>
public sealed record StackProfileQuestion(string Layer, string Text);

/// <summary>§4.27: the structured profile in the <c>aide-stack-profile</c> block.</summary>
public sealed record StackProfileDocument
{
    public int SchemaVersion { get; init; } = StackProfile.SchemaVersion;

    public string? BaseCommitSha { get; init; }

    /// <summary>FR-181: "Continue with No stack (general)": approve with no composition (FR-114's generalist behaviour).</summary>
    public bool Generalist { get; init; }

    public required StackProfileLayers Layers { get; init; }

    public StackProfileHosts Hosts { get; init; } = StackProfileHosts.Empty;

    public IReadOnlyList<StackProfileStructureEntry> Structure { get; init; } = [];

    public IReadOnlyList<StackProfileQuestion> Questions { get; init; } = [];
}

/// <summary>A parsed profile: the narrative Markdown around the block and the document.</summary>
public sealed record ParsedStackProfile(string Narrative, StackProfileDocument Document);

/// <summary>
/// What profile validation checks against: the catalog, the repository's VCS option (R5; <c>null</c> for a Local
/// repository), and the files at the base commit (<c>null</c>: unknown, so existence isn't checked).
/// </summary>
public sealed class StackProfileContext(StackCatalog catalog, string? repoVcs, IReadOnlySet<string>? baseFiles)
{
    private readonly Lazy<IReadOnlySet<string>?> _directories = new(() => baseFiles is null ? null : StackProfile.Directories(baseFiles));

    public StackCatalog Catalog { get; } = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public string? RepoVcs { get; } = repoVcs;

    public IReadOnlySet<string>? BaseFiles { get; } = baseFiles;

    /// <summary>Every folder at the base commit (from <see cref="BaseFiles"/>), with <c>.</c> for the root; <c>null</c> when unknown.</summary>
    public IReadOnlySet<string>? BaseDirectories => _directories.Value;
}

/// <summary>The outcome of approving a profile: the composition to pin, or none with <see cref="Generalist"/>.</summary>
public sealed record StackProfileApproval(StackComposition? Composition, bool Generalist);

/// <summary>
/// FR-180–FR-184, §4.27: the pure rules of the <c>stack_profile</c> artifact. Markdown: a narrative, then one fenced block tagged
/// <c>aide-stack-profile</c> holding the JSON document. <see cref="Parse"/> and <see cref="Render"/> round-trip it; a form
/// edit re-renders the block. <see cref="Validate"/> checks catalog options and FR-119 paths; <see cref="Reconcile"/> applies
/// the agent-versus-detector rule; <see cref="Approve"/> decides approval like <c>PUT /stack</c>.
/// </summary>
public static class StackProfile
{
    /// <summary>The fence tag of the block.</summary>
    public const string FenceTag = "aide-stack-profile";

    public const int SchemaVersion = 1;

    public const int MaxBlockLength = 200_000;

    public const int MaxEvidence = 10;

    public const int MaxStructure = 100;

    public const int MaxQuestions = 20;

    public const int MaxTextLength = 1_000;

    /// <summary>The profile's layers, in order.</summary>
    public static IReadOnlyList<string> Layers { get; } = [StackLayers.Frontend, StackLayers.Backend, StackLayers.Data, StackLayers.Vcs];

    /// <summary>The layers a question may be about (the four layers and <c>hosts</c>).</summary>
    public static IReadOnlyList<string> QuestionLayers { get; } = [.. Layers, "hosts"];

    /// <summary>§4.27 <c>structure</c> roles.</summary>
    public static IReadOnlyList<string> StructureRoles { get; } = ["frontend", "backend", "data", "migrations", "tests", "infra", "ci", "docs", "other"];

    /// <summary>The host keys of <see cref="StackProfileHosts.Detected"/>.</summary>
    public static IReadOnlyList<string> HostChoices { get; } = [StackChoices.FrontendHost, StackChoices.BackendHost, StackChoices.DatabaseHost];

    /// <summary>The parts of <see cref="StackProfileHosts.SeparateParts"/> (§4.28).</summary>
    public static IReadOnlyList<string> Parts { get; } = ["frontend", "backend", "database"];

    /// <summary>Candidate sources.</summary>
    public const string AgentSource = "agent";

    public const string DetectorSource = "detector";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 16,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    // ---------------------------------------------------------------- statuses

    /// <summary>Whether a status blocks approval (§4.27): <c>unknown</c>, <c>conflict</c>, and <c>unsupported</c> unless <paramref name="generalist"/>.</summary>
    public static bool Blocks(StackProfileLayerStatus status, bool generalist) => status switch
    {
        StackProfileLayerStatus.Unknown or StackProfileLayerStatus.Conflict => true,
        StackProfileLayerStatus.Unsupported => !generalist,
        _ => false,
    };

    /// <summary>FR-181: the layers that block approval, in layer order.</summary>
    public static IReadOnlyList<string> Unresolved(StackProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. Layers.Where(l => Blocks(document.Layers.Get(l).Status, document.Generalist))];
    }

    /// <summary>FR-181 "Needs your input": the layers whose status is <c>unknown</c>, <c>conflict</c> or <c>unsupported</c>, in layer order (generalist or not).</summary>
    public static IReadOnlyList<string> NeedsInput(StackProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. Layers.Where(l => document.Layers.Get(l).Status is StackProfileLayerStatus.Unknown or StackProfileLayerStatus.Conflict or StackProfileLayerStatus.Unsupported)];
    }

    // ---------------------------------------------------------------- parse and render

    /// <summary>
    /// The narrative and document of a <c>stack_profile</c>'s Markdown. <c>VALIDATION</c> (details <c>"field: message"</c>)
    /// when there isn't exactly one <c>aide-stack-profile</c> block, or its JSON isn't a profile.
    /// </summary>
    public static Result<ParsedStackProfile> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var opens = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsOpeningFence(lines[i]))
            {
                opens.Add(i);
            }
        }

        if (opens.Count != 1)
        {
            return Invalid<ParsedStackProfile>("content", opens.Count == 0
                ? $"needs a fenced ```{FenceTag} block holding the profile."
                : $"must have exactly one ```{FenceTag} block, not {opens.Count}.");
        }

        var open = opens[0];
        var close = -1;
        for (var i = open + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "```")
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return Invalid<ParsedStackProfile>("content", $"the ```{FenceTag} block isn't closed.");
        }

        var block = string.Join('\n', lines[(open + 1)..close]);
        if (block.Length > MaxBlockLength)
        {
            return Invalid<ParsedStackProfile>("content", $"the profile block may be at most {MaxBlockLength} characters.");
        }

        var before = string.Join('\n', lines[..open]).Trim();
        var after = string.Join('\n', lines[(close + 1)..]).Trim();
        var narrative = after.Length == 0 ? before : before.Length == 0 ? after : $"{before}\n\n{after}";

        var document = FromJson(block);
        return document.IsSuccess
            ? Result.Success(new ParsedStackProfile(narrative, document.Value))
            : Result.Failure<ParsedStackProfile>(document.Error!);
    }

    /// <summary>The document of <paramref name="json"/> (the block's content). <c>VALIDATION</c> on malformed JSON or a malformed shape.</summary>
    public static Result<StackProfileDocument> FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RawDocument? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            return Invalid<StackProfileDocument>("profile", $"isn't valid JSON ({ex.Message.Split('.')[0]}).");
        }

        if (raw is null)
        {
            return Invalid<StackProfileDocument>("profile", "must be a JSON object.");
        }

        var errors = new List<StackInputError>();
        var document = ToDocument(raw, errors);
        return errors.Count > 0
            ? Result.Failure<StackProfileDocument>(ValidationError(errors))
            : Result.Success(document!);
    }

    /// <summary>
    /// The Markdown of a profile: <paramref name="narrative"/> (trimmed; omitted when blank), a blank line, then the fenced
    /// block. <c>Parse(Render(n, d))</c> gives back <c>n</c> trimmed and <c>d</c>; rendering again gives the same text.
    /// </summary>
    public static string Render(string? narrative, StackProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(narrative))
        {
            sb.Append(narrative.ReplaceLineEndings("\n").Trim()).Append("\n\n");
        }

        sb.Append("```").Append(FenceTag).Append('\n').Append(ToJson(document)).Append("\n```\n");
        return sb.ToString();
    }

    /// <summary>The document as the block's JSON (indented, field order as in §4.27).</summary>
    public static string ToJson(StackProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(ToRaw(document), Json).ReplaceLineEndings("\n");
    }

    private static bool IsOpeningFence(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed[3..].Trim() == FenceTag;
    }

    // ---------------------------------------------------------------- validation

    /// <summary>
    /// FR-180, FR-183: what's wrong with <paramref name="document"/> (empty when valid), each <c>"field: message"</c>: options
    /// that aren't catalog options of their choice, statuses without what they need (an option, candidates, a question),
    /// FR-119 paths, layer paths that aren't folders at the base commit and cited files that don't exist there (when the
    /// base is known), and the VCS fixed to the repository's host (R5).
    /// </summary>
    public static IReadOnlyList<StackInputError> Validate(StackProfileDocument document, StackProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<StackInputError>();
        if (document.SchemaVersion != SchemaVersion)
        {
            errors.Add(new StackInputError("schemaVersion", $"must be {SchemaVersion}."));
        }

        var catalog = context.Catalog;
        var paths = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var layer in Layers)
        {
            var value = document.Layers.Get(layer);
            var field = $"layers.{layer}";
            var choice = catalog.Choice(layer);
            CheckLayer(document, layer, value, field, choice, context, errors, paths);
        }

        if (paths.GetValueOrDefault(StackLayers.Frontend) is { } fe && paths.GetValueOrDefault(StackLayers.Backend) is { } be)
        {
            errors.AddRange(StackPaths.Check(fe, be).Select(e => e with { Field = $"layers.{e.Field}" }));
        }

        CheckHosts(document.Hosts, catalog, errors);
        CheckStructure(document.Structure, context, errors);
        CheckQuestions(document.Questions, errors);
        return errors;
    }

    /// <summary>As <see cref="Validate"/>, as a <c>VALIDATION</c> error, or success.</summary>
    public static Result Check(StackProfileDocument document, StackProfileContext context)
    {
        var errors = Validate(document, context);
        return errors.Count == 0 ? Result.Success() : Result.Failure(ValidationError(errors));
    }

    private static void CheckLayer(
        StackProfileDocument document,
        string layer,
        StackProfileLayer value,
        string field,
        StackChoice choice,
        StackProfileContext context,
        List<StackInputError> errors,
        Dictionary<string, string?> paths)
    {
        if (value.Option is { } option && (option == StackIds.None || choice.Find(option) is null))
        {
            errors.Add(new StackInputError($"{field}.option", option == StackIds.None
                ? "use status 'none' (with no option) for a layer that isn't there."
                : $"'{option}' is not an option of {choice.Name} ({string.Join(", ", choice.Options.Where(o => o.Id != StackIds.None).Select(o => o.Id))}). Mark the layer 'unsupported' instead."));
        }

        var isVcs = layer == StackLayers.Vcs;
        switch (value.Status)
        {
            case StackProfileLayerStatus.Inferred or StackProfileLayerStatus.User when value.Option is null:
                errors.Add(new StackInputError($"{field}.option", $"is required when the status is {value.Status.ToWire()}."));
                break;
            case StackProfileLayerStatus.Fixed when !isVcs:
                errors.Add(new StackInputError($"{field}.status", "can be 'fixed' only for the VCS layer."));
                break;
            case StackProfileLayerStatus.None when isVcs:
                errors.Add(new StackInputError($"{field}.status", "the VCS layer can't be none."));
                break;
            case StackProfileLayerStatus.None or StackProfileLayerStatus.Unknown or StackProfileLayerStatus.Conflict or StackProfileLayerStatus.Unsupported when value.Option is not null:
                errors.Add(new StackInputError($"{field}.option", $"must be null when the status is {value.Status.ToWire()}."));
                break;
            case StackProfileLayerStatus.Unknown when !document.Questions.Any(q => q.Layer == layer):
                errors.Add(new StackInputError($"{field}.status", "an unknown layer needs a question in 'questions'."));
                break;
            case StackProfileLayerStatus.Conflict when value.Candidates.Count < 2:
                errors.Add(new StackInputError($"{field}.candidates", "a conflict needs at least two candidates."));
                break;
            default:
                break;
        }

        if (isVcs && value.Status is not StackProfileLayerStatus.Fixed)
        {
            errors.Add(new StackInputError($"{field}.status", "the VCS layer is 'fixed' to the repository's host."));
        }

        if (isVcs && context.RepoVcs is { } repoVcs && value.Option is { } vcs && vcs != repoVcs)
        {
            errors.Add(new StackInputError($"{field}.option", $"must be '{repoVcs}' (the repository's host)."));
        }

        foreach (var (candidate, i) in value.Candidates.Select((c, i) => (c, i)))
        {
            if (candidate.Option != StackIds.None && choice.Find(candidate.Option) is null)
            {
                errors.Add(new StackInputError($"{field}.candidates[{i}].option", $"'{candidate.Option}' is not an option of {choice.Name}."));
            }

            if (candidate.Source is not (AgentSource or DetectorSource))
            {
                errors.Add(new StackInputError($"{field}.candidates[{i}].source", $"must be '{AgentSource}' or '{DetectorSource}'."));
            }
        }

        if (layer is StackLayers.Frontend or StackLayers.Backend)
        {
            if (value.Path is { } path)
            {
                var normalised = StackCompatibility.NormalisePath(path, out var pathError);
                if (normalised is null)
                {
                    errors.Add(new StackInputError($"{field}.path", pathError!));
                }
                else if (context.BaseDirectories is { } dirs && !dirs.Contains(normalised))
                {
                    errors.Add(new StackInputError($"{field}.path", $"'{normalised}' is not a folder at the base commit."));
                }
                else if (value.Option is not null)
                {
                    paths[layer] = normalised;
                }
            }
        }
        else if (value.Path is not null)
        {
            errors.Add(new StackInputError($"{field}.path", "only the frontend and backend layers have a path."));
        }

        if (value.Mode is not null && layer != StackLayers.Data)
        {
            errors.Add(new StackInputError($"{field}.mode", "only the data layer has a mode."));
        }

        if (value.Technology is { Length: > MaxTextLength })
        {
            errors.Add(new StackInputError($"{field}.technology", $"may be at most {MaxTextLength} characters."));
        }

        CheckEvidence(value.Evidence, $"{field}.evidence", context, errors);
        foreach (var (candidate, i) in value.Candidates.Select((c, i) => (c, i)))
        {
            CheckEvidence(candidate.Evidence, $"{field}.candidates[{i}].evidence", context, errors);
        }
    }

    private static void CheckEvidence(IReadOnlyList<StackProfileEvidence> evidence, string field, StackProfileContext context, List<StackInputError> errors)
    {
        if (evidence.Count > MaxEvidence)
        {
            errors.Add(new StackInputError(field, $"may cite at most {MaxEvidence} files."));
        }

        foreach (var (item, i) in evidence.Select((e, i) => (e, i)))
        {
            var normalised = StackCompatibility.NormalisePath(item.File, out var pathError);
            if (normalised is null || normalised == StackPaths.Root)
            {
                errors.Add(new StackInputError($"{field}[{i}].file", pathError ?? "must name a file or folder."));
            }
            else if (!ExistsAtBase(normalised, context))
            {
                errors.Add(new StackInputError($"{field}[{i}].file", $"'{normalised}' doesn't exist at the base commit."));
            }

            if (item.Text.Length > MaxTextLength)
            {
                errors.Add(new StackInputError($"{field}[{i}].text", $"may be at most {MaxTextLength} characters."));
            }
        }
    }

    private static void CheckHosts(StackProfileHosts hosts, StackCatalog catalog, List<StackInputError> errors)
    {
        void Option(string choiceId, string? value, string field)
        {
            if (value is not null && catalog.Choice(choiceId).Find(value) is null)
            {
                var choice = catalog.Choice(choiceId);
                errors.Add(new StackInputError($"hosts.{field}", $"'{value}' is not an option of {choice.Name} ({string.Join(", ", choice.Options.Select(o => o.Id))})."));
            }
        }

        Option(StackChoices.FrontendHost, hosts.FrontendHost, "frontendHost");
        Option(StackChoices.BackendHost, hosts.BackendHost, "backendHost");
        Option(StackChoices.DatabaseHost, hosts.DatabaseHost, "databaseHost");
        Option(StackChoices.Local, hosts.Local, "local");
        Option(StackChoices.OriginModel, hosts.OriginModel, "originModel");
        foreach (var part in hosts.SeparateParts.Where(p => !Parts.Contains(p)))
        {
            errors.Add(new StackInputError("hosts.separateParts", $"'{part}' is not a part ({string.Join(", ", Parts)})."));
        }

        if (hosts.SeparateParts.Distinct(StringComparer.Ordinal).Count() != hosts.SeparateParts.Count)
        {
            errors.Add(new StackInputError("hosts.separateParts", "lists a part twice."));
        }

        foreach (var detected in hosts.Detected.Where(d => !HostChoices.Contains(d)))
        {
            errors.Add(new StackInputError("hosts.detected", $"'{detected}' is not a host choice ({string.Join(", ", HostChoices)})."));
        }
    }

    private static void CheckStructure(IReadOnlyList<StackProfileStructureEntry> structure, StackProfileContext context, List<StackInputError> errors)
    {
        if (structure.Count > MaxStructure)
        {
            errors.Add(new StackInputError("structure", $"may have at most {MaxStructure} rows."));
        }

        foreach (var (row, i) in structure.Select((r, i) => (r, i)))
        {
            var normalised = StackCompatibility.NormalisePath(row.Path, out var pathError);
            if (normalised is null)
            {
                errors.Add(new StackInputError($"structure[{i}].path", pathError!));
            }
            else if (!ExistsAtBase(normalised, context))
            {
                errors.Add(new StackInputError($"structure[{i}].path", $"'{normalised}' doesn't exist at the base commit."));
            }

            if (!StructureRoles.Contains(row.Role))
            {
                errors.Add(new StackInputError($"structure[{i}].role", $"must be one of {string.Join(", ", StructureRoles)}."));
            }

            if (row.Note.Length > MaxTextLength)
            {
                errors.Add(new StackInputError($"structure[{i}].note", $"may be at most {MaxTextLength} characters."));
            }
        }
    }

    private static void CheckQuestions(IReadOnlyList<StackProfileQuestion> questions, List<StackInputError> errors)
    {
        if (questions.Count > MaxQuestions)
        {
            errors.Add(new StackInputError("questions", $"may have at most {MaxQuestions} questions."));
        }

        foreach (var (question, i) in questions.Select((q, i) => (q, i)))
        {
            if (!QuestionLayers.Contains(question.Layer))
            {
                errors.Add(new StackInputError($"questions[{i}].layer", $"must be one of {string.Join(", ", QuestionLayers)}."));
            }

            if (string.IsNullOrWhiteSpace(question.Text) || question.Text.Length > MaxTextLength)
            {
                errors.Add(new StackInputError($"questions[{i}].text", $"must be 1 to {MaxTextLength} characters."));
            }
        }
    }

    private static bool ExistsAtBase(string path, StackProfileContext context) =>
        context.BaseFiles is not { } files
        || path == StackPaths.Root
        || files.Contains(path)
        || context.BaseDirectories!.Contains(path);

    /// <summary>Every folder of <paramref name="files"/> (normalised, <c>/</c>-separated), with <c>.</c> for the root.</summary>
    public static IReadOnlySet<string> Directories(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StackPaths.Root };
        foreach (var file in files)
        {
            var path = file.Replace('\\', '/').Trim('/');
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (!dirs.Add(path[..slash]))
                {
                    break;
                }
            }
        }

        return dirs;
    }

    // ---------------------------------------------------------------- the agent's reply

    /// <summary>
    /// FR-180: the Stack Analyst's reply checked: one profile block, valid against <paramref name="context"/> (catalog options
    /// only, cited files present at the base commit). Failures are <c>STACK_PROFILE_INVALID</c> with every reason as details,
    /// so the corrective re-run gets them (<see cref="CorrectiveFeedback"/>), and a second failure fails the run.
    /// </summary>
    public static Result<ParsedStackProfile> CheckReply(string reply, StackProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(context);
        var parsed = Parse(reply);
        if (parsed.IsFailure && Agents.AgentOutput.UnwrapFence(reply) is var unwrapped && unwrapped != reply.Trim() && Parse(unwrapped) is { IsSuccess: true } inner)
        {
            // A reply wrapped whole in a ```markdown fence (FR-27 tolerance).
            parsed = inner;
        }

        if (parsed.IsFailure)
        {
            return Result.Failure<ParsedStackProfile>(new DomainError(ErrorCodes.StackProfileInvalid, "The stack profile couldn't be read: " + string.Join("; ", parsed.Error!.Details))
            {
                Details = parsed.Error.Details,
            });
        }

        var errors = Validate(parsed.Value.Document, context);
        return errors.Count == 0
            ? parsed
            : Result.Failure<ParsedStackProfile>(new DomainError(ErrorCodes.StackProfileInvalid, $"The stack profile is invalid: {string.Join("; ", errors)}")
            {
                Details = [.. errors.Select(e => e.ToString())],
            });
    }

    /// <summary>FR-180: the corrective re-run's feedback: every reason, and the options each layer may name.</summary>
    public static string CorrectiveFeedback(DomainError error, StackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder("Your previous stack profile was rejected:\n");
        foreach (var detail in error.Details.Count > 0 ? error.Details : [error.Message])
        {
            sb.Append("- ").Append(detail).Append('\n');
        }

        sb.Append("\nName only these catalog options (and only in the layer they belong to):\n");
        foreach (var choice in StackChoices.Order)
        {
            sb.Append("- ").Append(choice).Append(": ")
                .Append(string.Join(", ", catalog.Choice(choice).Options.Where(o => o.Id != StackIds.None).Select(o => o.Id)))
                .Append('\n');
        }

        sb.Append("\nWhen the repository uses something that isn't one of these options, mark the layer \"unsupported\" with option null and ")
            .Append("name it in \"technology\". When you can't tell, mark it \"unknown\" with a question. Cite only files that exist in the repository. ")
            .Append($"Return the complete profile again, with exactly one ```{FenceTag} block.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- detector

    /// <summary>
    /// §4.27 "Agent versus detector", FR-184: the profile after the deterministic rules. The VCS layer is fixed to the
    /// repository's host; a frontend, backend or data layer the agent decided (an option, or <c>none</c>) that differs from the
    /// detector's single match becomes <c>conflict</c> with both as candidates, whatever the agent's confidence; host choices the
    /// detector found are preselected where the agent left them empty, and listed in <c>detected</c>; the base commit is set.
    /// </summary>
    public static StackProfileDocument Reconcile(StackProfileDocument document, StackDetection? detection, string? repoVcs, string? baseCommitSha)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layers = document.Layers with
        {
            Vcs = new StackProfileLayer
            {
                Option = repoVcs ?? document.Layers.Vcs.Option ?? StackPinning.GitHubVcs,
                Status = StackProfileLayerStatus.Fixed,
                Confidence = StackProfileConfidence.High,
                Evidence = document.Layers.Vcs.Evidence,
            },
        };
        var questions = document.Questions.ToList();
        var hosts = document.Hosts;
        if (detection is not null)
        {
            foreach (var layer in new[] { StackLayers.Frontend, StackLayers.Backend, StackLayers.Data })
            {
                var agent = layers.Get(layer);
                if (detection.Suggestion(layer) is not { } match
                    || agent.Status is not (StackProfileLayerStatus.Inferred or StackProfileLayerStatus.None))
                {
                    continue;
                }

                var agentOption = agent.Status == StackProfileLayerStatus.None ? StackIds.None : agent.Option!;
                if (agentOption == match.Option)
                {
                    continue;
                }

                layers = layers.With(layer, new StackProfileLayer
                {
                    Status = StackProfileLayerStatus.Conflict,
                    Confidence = agent.Confidence,
                    Evidence = agent.Evidence,
                    Candidates =
                    [
                        new StackProfileCandidate(agentOption, agent.Path, AgentSource, agent.Evidence),
                        new StackProfileCandidate(match.Option, match.Path, DetectorSource, EvidenceOf(match.Evidence)),
                    ],
                });
                if (!questions.Any(q => q.Layer == layer))
                {
                    questions.Add(new StackProfileQuestion(layer, $"The analysis found `{agentOption}` but the manifests point to `{match.Option}`. Which does the {layer} use?"));
                }
            }

            var detected = hosts.Detected.ToList();
            string? Host(string choice, string? current)
            {
                if (detection.Suggestion(choice) is not { } found || found.Option == StackIds.None)
                {
                    return current;
                }

                if (current is null || current == found.Option)
                {
                    if (!detected.Contains(choice))
                    {
                        detected.Add(choice);
                    }

                    return found.Option;
                }

                return current;
            }

            hosts = hosts with
            {
                FrontendHost = Host(StackChoices.FrontendHost, hosts.FrontendHost),
                BackendHost = Host(StackChoices.BackendHost, hosts.BackendHost),
                DatabaseHost = Host(StackChoices.DatabaseHost, hosts.DatabaseHost),
            };
            hosts = hosts with { Detected = [.. HostChoices.Where(detected.Contains)] };
        }

        return document with
        {
            SchemaVersion = SchemaVersion,
            BaseCommitSha = baseCommitSha ?? document.BaseCommitSha,
            Layers = layers,
            Hosts = hosts,
            Questions = questions,
        };
    }

    /// <summary>
    /// FR-180 "Fill in manually": a profile from the detector alone: its single matches as <c>inferred</c> (medium confidence),
    /// its conflicting matches as <c>conflict</c>, every other layer <c>unknown</c> with a question; the VCS fixed to the
    /// repository's host; detected hosts preselected. No agent findings.
    /// </summary>
    public static StackProfileDocument FromDetection(StackDetection? detection, string? repoVcs, string? baseCommitSha)
    {
        var layers = new StackProfileLayers(StackProfileLayer.Unknown, StackProfileLayer.Unknown, StackProfileLayer.Unknown, StackProfileLayer.Unknown);
        var questions = new List<StackProfileQuestion>();
        foreach (var layer in new[] { StackLayers.Frontend, StackLayers.Backend, StackLayers.Data })
        {
            StackProfileLayer value;
            if (detection?.Suggestion(layer) is { } match)
            {
                value = match.Option == StackIds.None
                    ? new StackProfileLayer { Status = StackProfileLayerStatus.None, Confidence = StackProfileConfidence.Medium }
                    : new StackProfileLayer
                    {
                        Option = match.Option,
                        Path = layer is StackLayers.Frontend or StackLayers.Backend ? match.Path : null,
                        Mode = layer == StackLayers.Data ? match.Mode : null,
                        Status = StackProfileLayerStatus.Inferred,
                        Confidence = StackProfileConfidence.Medium,
                        Evidence = EvidenceOf(match.Evidence),
                    };
            }
            else if (detection?.Conflict(layer) is { } conflict)
            {
                value = new StackProfileLayer
                {
                    Status = StackProfileLayerStatus.Conflict,
                    Candidates = [.. conflict.Candidates.Select(c => new StackProfileCandidate(c.Option, c.Path, DetectorSource, EvidenceOf(c.Evidence)))],
                };
                questions.Add(new StackProfileQuestion(layer, $"The manifests point to {string.Join(" and ", conflict.Candidates.Select(c => $"`{c.Option}`"))}. Which does the {layer} use?"));
            }
            else
            {
                value = StackProfileLayer.Unknown;
                questions.Add(new StackProfileQuestion(layer, $"What does the repository use for the {layer}?"));
            }

            layers = layers.With(layer, value);
        }

        var document = new StackProfileDocument { BaseCommitSha = baseCommitSha, Layers = layers, Questions = questions };
        return Reconcile(document, detection is null ? null : new StackDetection([.. detection.Suggestions.Where(s => HostChoices.Contains(s.Choice))], []), repoVcs, baseCommitSha);
    }

    /// <summary>The detector's evidence strings as cited files: the last <c>`path`</c> in each (e.g. "`vue` 3.5 in `frontend/package.json`").</summary>
    public static IReadOnlyList<StackProfileEvidence> EvidenceOf(IEnumerable<string> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var result = new List<StackProfileEvidence>();
        foreach (var text in evidence)
        {
            var ticks = text.Split('`');
            var file = ticks.Length >= 3
                ? ticks.Where((_, i) => i % 2 == 1).LastOrDefault(t => t.Contains('/', StringComparison.Ordinal) || t.Contains('.', StringComparison.Ordinal))
                : null;
            if (file is not null && result.Count < MaxEvidence)
            {
                result.Add(new StackProfileEvidence(file.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : file, text));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- approval

    /// <summary>
    /// FR-183's selection: what <c>PUT /stack</c> would validate for the profile. Every layer explicit (None as
    /// <c>none</c>); the local dev loop is left to its default (R12) when the profile doesn't set it.
    /// </summary>
    public static StackSelection ToSelection(StackProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var l = document.Layers;
        var h = document.Hosts;
        return new StackSelection
        {
            Frontend = new StackComponentPick(l.Frontend.Option ?? StackIds.None, l.Frontend.Option is null ? null : l.Frontend.Path),
            Backend = new StackComponentPick(l.Backend.Option ?? StackIds.None, l.Backend.Option is null ? null : l.Backend.Path),
            Data = new StackDataPick(l.Data.Option ?? StackIds.None, l.Data.Option is null ? null : l.Data.Mode),
            Vcs = l.Vcs.Option is { } vcs ? new StackVcsPick(vcs) : null,
            Deploy = new StackDeployPick(h.FrontendHost ?? StackIds.None, h.BackendHost ?? StackIds.None, h.DatabaseHost ?? StackIds.None, h.Local)
            {
                SeparateParts = h.SeparateParts.Count == 0 ? null : h.SeparateParts,
            },
            OriginModel = h.OriginModel,
        };
    }

    /// <summary>
    /// FR-181, FR-183, §4.27 "Approval": <c>STACK_PROFILE_INCOMPLETE</c> (details: the unresolved layers) unless every layer's
    /// status allows approval; with <c>generalist</c>, approval with no composition; otherwise the composition from
    /// <see cref="StackCompatibility.Validate"/> after R5 (<paramref name="repoHost"/>), or <c>VCS_MISMATCH</c>,
    /// <c>VALIDATION</c> or <c>STACK_INCOMPATIBLE</c> (with the violations).
    /// </summary>
    public static Result<StackProfileApproval> Approve(StackProfileDocument document, StackCatalog catalog, StackVcsPick? repoHost)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        var unresolved = Unresolved(document);
        if (unresolved.Count > 0)
        {
            return Result.Failure<StackProfileApproval>(new DomainError(
                ErrorCodes.StackProfileIncomplete,
                $"Answer these before approving: {string.Join(", ", unresolved)}.")
            {
                Details = unresolved,
            });
        }

        if (document.Generalist)
        {
            return Result.Success(new StackProfileApproval(null, Generalist: true));
        }

        var fixedVcs = StackPinning.FixVcs(ToSelection(document), repoHost);
        if (fixedVcs.IsFailure)
        {
            return Result.Failure<StackProfileApproval>(fixedVcs.Error!);
        }

        var validation = StackCompatibility.Validate(fixedVcs.Value, catalog);
        if (validation.InputErrors.Count > 0)
        {
            return Result.Failure<StackProfileApproval>(new DomainError(ErrorCodes.Validation, "The stack choices are invalid.")
            {
                Details = [.. validation.InputErrors.Select(e => e.ToString())],
            });
        }

        return validation.Composition is { } composition
            ? Result.Success(new StackProfileApproval(composition, Generalist: false))
            : Result.Failure<StackProfileApproval>(StackCompatibility.IncompatibleError(validation.Violations));
    }

    // ---------------------------------------------------------------- raw JSON

    private static Result<T> Invalid<T>(string field, string message) =>
        Result.Failure<T>(ValidationError([new StackInputError(field, message)]));

    private static DomainError ValidationError(IReadOnlyList<StackInputError> errors) =>
        new(ErrorCodes.Validation, "The stack profile is invalid.") { Details = [.. errors.Select(e => e.ToString())] };

    private static StackProfileDocument? ToDocument(RawDocument raw, List<StackInputError> errors)
    {
        if (raw.Layers is null)
        {
            errors.Add(new StackInputError("layers", "is required."));
            return null;
        }

        StackProfileLayer Layer(RawLayer? value, string name)
        {
            if (value is null)
            {
                errors.Add(new StackInputError($"layers.{name}", "is required."));
                return StackProfileLayer.Unknown;
            }

            var status = StackProfileLayerStatus.Unknown;
            if (!EnumStrings.TryParse(value.Status, out status))
            {
                errors.Add(new StackInputError($"layers.{name}.status", $"must be one of {string.Join(", ", EnumStrings.Values<StackProfileLayerStatus>())}."));
            }

            StackProfileConfidence? confidence = null;
            if (value.Confidence is { } c)
            {
                if (EnumStrings.TryParse<StackProfileConfidence>(c, out var parsed))
                {
                    confidence = parsed;
                }
                else
                {
                    errors.Add(new StackInputError($"layers.{name}.confidence", $"must be one of {string.Join(", ", EnumStrings.Values<StackProfileConfidence>())}."));
                }
            }

            return new StackProfileLayer
            {
                Option = Blank(value.Option),
                Path = Blank(value.Path),
                Mode = Blank(value.Mode),
                Status = status,
                Confidence = confidence,
                Technology = Blank(value.Technology),
                Evidence = Evidence(value.Evidence, $"layers.{name}.evidence"),
                Candidates = [.. (value.Candidates ?? []).Select((x, i) => x is null || string.IsNullOrWhiteSpace(x.Option)
                    ? Missing<StackProfileCandidate>($"layers.{name}.candidates[{i}].option")
                    : new StackProfileCandidate(x.Option.Trim(), Blank(x.Path), x.Source?.Trim() ?? AgentSource, Evidence(x.Evidence, $"layers.{name}.candidates[{i}].evidence")))
                    .OfType<StackProfileCandidate>()],
            };
        }

        IReadOnlyList<StackProfileEvidence> Evidence(IReadOnlyList<RawEvidence?>? items, string field) =>
            [.. (items ?? []).Select((e, i) => e is null || string.IsNullOrWhiteSpace(e.File)
                ? Missing<StackProfileEvidence>($"{field}[{i}].file")
                : new StackProfileEvidence(e.File.Trim(), e.Text?.Trim() ?? string.Empty)).OfType<StackProfileEvidence>()];

        T? Missing<T>(string field)
            where T : class
        {
            errors.Add(new StackInputError(field, "is required."));
            return null;
        }

        var layers = new StackProfileLayers(
            Layer(raw.Layers.Frontend, StackLayers.Frontend),
            Layer(raw.Layers.Backend, StackLayers.Backend),
            Layer(raw.Layers.Data, StackLayers.Data),
            Layer(raw.Layers.Vcs, StackLayers.Vcs));
        var hosts = raw.Hosts is { } h
            ? new StackProfileHosts
            {
                FrontendHost = Host(h.FrontendHost),
                BackendHost = Host(h.BackendHost),
                DatabaseHost = Host(h.DatabaseHost),
                Local = Blank(h.Local),
                OriginModel = Blank(h.OriginModel),
                SeparateParts = [.. (h.SeparateParts ?? []).OfType<string>().Select(p => p.Trim())],
                Detected = [.. (h.Detected ?? []).OfType<string>().Select(p => p.Trim())],
            }
            : StackProfileHosts.Empty;
        return new StackProfileDocument
        {
            SchemaVersion = raw.SchemaVersion ?? 0,
            BaseCommitSha = Blank(raw.BaseCommitSha),
            Generalist = raw.Generalist ?? false,
            Layers = layers,
            Hosts = hosts,
            Structure = [.. (raw.Structure ?? []).Select((s, i) => s is null || string.IsNullOrWhiteSpace(s.Path)
                ? Missing<StackProfileStructureEntry>($"structure[{i}].path")
                : new StackProfileStructureEntry(s.Path.Trim(), s.Role?.Trim() ?? string.Empty, s.Note?.Trim() ?? string.Empty)).OfType<StackProfileStructureEntry>()],
            Questions = [.. (raw.Questions ?? []).Select((q, i) => q is null
                ? Missing<StackProfileQuestion>($"questions[{i}]")
                : new StackProfileQuestion(q.Layer?.Trim() ?? string.Empty, q.Text?.Trim() ?? string.Empty)).OfType<StackProfileQuestion>()],
        };
    }

    /// <summary>A host value: <c>none</c> and blank are None (<c>null</c>).</summary>
    private static string? Host(string? value) => Blank(value) is { } v && v != StackIds.None ? v : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RawDocument ToRaw(StackProfileDocument d)
    {
        static List<RawEvidence?> Evidence(IReadOnlyList<StackProfileEvidence> e) => [.. e.Select(x => new RawEvidence { File = x.File, Text = x.Text })];
        static RawLayer Layer(StackProfileLayer l, bool hasPath) => new()
        {
            Option = l.Option,
            Path = hasPath ? l.Path : null,
            Mode = l.Mode,
            Status = l.Status.ToWire(),
            Confidence = l.Confidence?.ToWire(),
            Technology = l.Technology,
            Evidence = Evidence(l.Evidence),
            Candidates = l.Candidates.Count == 0 ? null : [.. l.Candidates.Select(c => new RawCandidate { Option = c.Option, Path = c.Path, Source = c.Source, Evidence = Evidence(c.Evidence) })],
        };

        return new RawDocument
        {
            SchemaVersion = d.SchemaVersion,
            BaseCommitSha = d.BaseCommitSha,
            Generalist = d.Generalist,
            Layers = new RawLayers
            {
                Frontend = Layer(d.Layers.Frontend, true),
                Backend = Layer(d.Layers.Backend, true),
                Data = Layer(d.Layers.Data, false),
                Vcs = Layer(d.Layers.Vcs, false),
            },
            Hosts = new RawHosts
            {
                FrontendHost = d.Hosts.FrontendHost,
                BackendHost = d.Hosts.BackendHost,
                DatabaseHost = d.Hosts.DatabaseHost,
                Local = d.Hosts.Local,
                OriginModel = d.Hosts.OriginModel,
                SeparateParts = [.. d.Hosts.SeparateParts],
                Detected = [.. d.Hosts.Detected],
            },
            Structure = [.. d.Structure.Select(s => new RawStructure { Path = s.Path, Role = s.Role, Note = s.Note })],
            Questions = [.. d.Questions.Select(q => new RawQuestion { Layer = q.Layer, Text = q.Text })],
        };
    }

    // The block's JSON shape: every member optional on read, so a malformed profile gives field errors, not an exception.
    private sealed class RawDocument
    {
        public int? SchemaVersion { get; set; }

        public string? BaseCommitSha { get; set; }

        public bool? Generalist { get; set; }

        public RawLayers? Layers { get; set; }

        public RawHosts? Hosts { get; set; }

        public List<RawStructure?>? Structure { get; set; }

        public List<RawQuestion?>? Questions { get; set; }
    }

    private sealed class RawLayers
    {
        public RawLayer? Frontend { get; set; }

        public RawLayer? Backend { get; set; }

        public RawLayer? Data { get; set; }

        public RawLayer? Vcs { get; set; }
    }

    private sealed class RawLayer
    {
        public string? Option { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Path { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Mode { get; set; }

        public string? Status { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Confidence { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Technology { get; set; }

        public List<RawEvidence?>? Evidence { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RawCandidate?>? Candidates { get; set; }
    }

    private sealed class RawEvidence
    {
        public string? File { get; set; }

        public string? Text { get; set; }
    }

    private sealed class RawCandidate
    {
        public string? Option { get; set; }

        public string? Path { get; set; }

        public string? Source { get; set; }

        public List<RawEvidence?>? Evidence { get; set; }
    }

    private sealed class RawHosts
    {
        public string? FrontendHost { get; set; }

        public string? BackendHost { get; set; }

        public string? DatabaseHost { get; set; }

        public string? Local { get; set; }

        public string? OriginModel { get; set; }

        public List<string?>? SeparateParts { get; set; }

        public List<string?>? Detected { get; set; }
    }

    private sealed class RawStructure
    {
        public string? Path { get; set; }

        public string? Role { get; set; }

        public string? Note { get; set; }
    }

    private sealed class RawQuestion
    {
        public string? Layer { get; set; }

        public string? Text { get; set; }
    }
}
