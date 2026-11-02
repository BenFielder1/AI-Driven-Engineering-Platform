using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;

namespace Aide.UnitTests.Stack;

/// <summary>
/// FR-180–FR-184, §4.27: the pure <c>stack_profile</c> rules: the <c>aide-stack-profile</c> round trip, validation against the
/// catalog and the base commit, the agent-versus-detector rule, and approval (the §4.27 worked examples, verbatim).
/// </summary>
public class StackProfileTests
{
    private static readonly HashSet<string> BaseFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "README.md",
        "frontend/package.json",
        "frontend/index.html",
        "frontend/src/main.ts",
        "supabase/config.toml",
        "supabase/migrations/20260101000000_init.sql",
    };

    private static StackProfileContext Context(IReadOnlySet<string>? files = null, string? repoVcs = StackPinning.GitHubVcs) =>
        new(StackTestCatalog.Catalog, repoVcs, files ?? BaseFiles);

    private static StackProfileLayer Inferred(string option, string? path = null, string? mode = null, params StackProfileEvidence[] evidence) => new()
    {
        Option = option,
        Path = path,
        Mode = mode,
        Status = StackProfileLayerStatus.Inferred,
        Confidence = StackProfileConfidence.High,
        Evidence = evidence,
    };

    private static readonly StackProfileLayer NoLayer = new() { Status = StackProfileLayerStatus.None, Confidence = StackProfileConfidence.High };

    private static readonly StackProfileLayer GitHub = new() { Option = StackPinning.GitHubVcs, Status = StackProfileLayerStatus.Fixed };

    /// <summary>The §4.27 example: <c>fe-vue</c> at <c>frontend</c>, Backend none, <c>data-supabase</c> BaaS, VCS GitHub.</summary>
    private static StackProfileDocument VueSupabase(string? frontendHost = "deploy-vercel") => new()
    {
        BaseCommitSha = "abc123",
        Layers = new StackProfileLayers(
            Inferred("fe-vue", "frontend", null, new StackProfileEvidence("frontend/package.json", "vue 3.5, vite 7")),
            NoLayer,
            Inferred(StackIds.Supabase, null, StackIds.BaasMode, new StackProfileEvidence("supabase/migrations", "SQL migrations")),
            GitHub),
        Hosts = new StackProfileHosts { FrontendHost = frontendHost, DatabaseHost = StackIds.SupabaseHost, Local = StackIds.None, OriginModel = "same-origin", Detected = [StackChoices.DatabaseHost] },
        Structure = [new StackProfileStructureEntry("frontend", "frontend", "Vue SPA"), new StackProfileStructureEntry("supabase/migrations", "migrations", "")],
    };

    private static StackProfileDocument WithBackend(StackProfileDocument document, StackProfileLayer backend, params StackProfileQuestion[] questions) =>
        document with { Layers = document.Layers with { Backend = backend }, Questions = questions };

    // ---------------------------------------------------------------- Parse / Render

    [Fact]
    public void FR183_S4_27_Parse_and_Render_round_trip_the_aide_stack_profile_block()
    {
        var markdown = StackProfile.Render("A Vue SPA on Supabase.", VueSupabase());

        var parsed = StackProfile.Parse(markdown);

        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Error?.Details ?? []));
        Assert.Equal("A Vue SPA on Supabase.", parsed.Value.Narrative);
        Assert.Equal(markdown, StackProfile.Render(parsed.Value.Narrative, parsed.Value.Document));
        Assert.Contains("```aide-stack-profile\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"inferred\"", markdown, StringComparison.Ordinal);
        Assert.Equal("fe-vue", parsed.Value.Document.Layers.Frontend.Option);
        Assert.Equal([StackChoices.DatabaseHost], parsed.Value.Document.Hosts.Detected);
    }

    [Fact]
    public void FR183_S4_27_Parse_reads_the_spec_example_document()
    {
        const string markdown = """
            The repository is a Vue SPA on Supabase.

            ```aide-stack-profile
            { "schemaVersion": 1, "baseCommitSha": "abc", "generalist": false,
              "layers": {
                "frontend": {"option": "fe-vue", "path": "frontend", "status": "inferred", "confidence": "high",
                             "evidence": [{"file": "frontend/package.json", "text": "vue 3.5, vite 7"}]},
                "backend":  {"option": null, "path": null, "status": "none", "confidence": "high", "evidence": []},
                "data":     {"option": "data-supabase", "mode": "baas", "status": "inferred", "confidence": "high", "evidence": []},
                "vcs":      {"option": "vcs-github", "status": "fixed"} },
              "hosts": {"frontendHost": null, "backendHost": null, "databaseHost": "supabase", "local": "none",
                        "originModel": "same-origin", "separateParts": [], "detected": ["databaseHost"]},
              "structure": [{"path": "frontend", "role": "frontend", "note": "Vue SPA"},
                            {"path": "supabase/migrations", "role": "migrations", "note": ""}],
              "questions": [] }
            ```
            """;

        var parsed = StackProfile.Parse(markdown);

        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Error?.Details ?? []));
        var document = parsed.Value.Document;
        Assert.Equal((StackProfileLayerStatus.None, (string?)null), (document.Layers.Backend.Status, document.Layers.Backend.Option));
        Assert.Equal(("data-supabase", "baas"), (document.Layers.Data.Option!, document.Layers.Data.Mode!));
        Assert.Equal(StackProfileLayerStatus.Fixed, document.Layers.Vcs.Status);
        Assert.Empty(StackProfile.Validate(document, Context()));
    }

    [Theory]
    [InlineData("No block here.", "content: needs a fenced")]
    [InlineData("```aide-stack-profile\n{}\n```\n```aide-stack-profile\n{}\n```", "content: must have exactly one")]
    [InlineData("```aide-stack-profile\n{\"layers\": ", "content: the ```aide-stack-profile block isn't closed")]
    [InlineData("```aide-stack-profile\nnot json\n```", "profile: isn't valid JSON")]
    [InlineData("```aide-stack-profile\n{\"schemaVersion\": 1}\n```", "layers: is required")]
    [InlineData("```aide-stack-profile\n{\"layers\": {\"frontend\": {\"status\": \"maybe\"}}}\n```", "layers.frontend.status: must be one of")]
    public void FR183_A_profile_without_one_readable_block_is_VALIDATION_with_the_field(string markdown, string detail)
    {
        var parsed = StackProfile.Parse(markdown);

        Assert.Equal(ErrorCodes.Validation, parsed.Error!.Code);
        Assert.Contains(parsed.Error.Details, d => d.StartsWith(detail, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Validate

    [Fact]
    public void FR180_A_valid_profile_has_no_errors() => Assert.Empty(StackProfile.Validate(VueSupabase(), Context()));

    [Fact]
    public void FR180_FR183_An_option_outside_the_catalog_is_refused_naming_the_allowed_options()
    {
        var document = WithBackend(VueSupabase(), Inferred("be-django", "."));

        var errors = StackProfile.Validate(document, Context());

        var error = Assert.Single(errors, e => e.Field == "layers.backend.option");
        Assert.Contains("'be-django' is not an option of", error.Message, StringComparison.Ordinal);
        Assert.Contains("be-express", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR183_FR119_A_layer_path_must_be_a_folder_at_the_base_commit()
    {
        var document = VueSupabase() with { Layers = VueSupabase().Layers with { Frontend = Inferred("fe-vue", "web") } };

        Assert.Contains(StackProfile.Validate(document, Context()), e => e.Field == "layers.frontend.path" && e.Message.Contains("not a folder at the base commit", StringComparison.Ordinal));
        Assert.DoesNotContain(StackProfile.Validate(document, new StackProfileContext(StackTestCatalog.Catalog, StackPinning.GitHubVcs, null)), e => e.Field == "layers.frontend.path");
    }

    [Theory]
    [InlineData("/etc", "must be relative")]
    [InlineData("../outside", "may not leave the repository")]
    public void FR183_FR119_Paths_follow_FR119(string path, string message)
    {
        var document = VueSupabase() with { Layers = VueSupabase().Layers with { Frontend = Inferred("fe-vue", path) } };

        Assert.Contains(StackProfile.Validate(document, Context()), e => e.Field == "layers.frontend.path" && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void FR119_The_frontend_and_backend_paths_may_not_overlap()
    {
        var files = new HashSet<string>(BaseFiles, StringComparer.OrdinalIgnoreCase) { "frontend/api/package.json" };
        var document = WithBackend(VueSupabase(), Inferred("be-express", "frontend/api"));

        Assert.Contains(StackProfile.Validate(document, Context(files)), e => e.Field == "layers.backend.path");
    }

    [Fact]
    public void FR180_S4_27_Cited_files_must_exist_at_the_base_commit()
    {
        var document = VueSupabase() with
        {
            Layers = VueSupabase().Layers with { Frontend = Inferred("fe-vue", "frontend", null, new StackProfileEvidence("frontend/vite.config.ts", "vite")) },
        };

        var error = Assert.Single(StackProfile.Validate(document, Context()));
        Assert.Equal("layers.frontend.evidence[0].file", error.Field);
    }

    [Fact]
    public void FR181_An_unknown_layer_needs_a_question_and_a_conflict_two_candidates()
    {
        var unknown = WithBackend(VueSupabase(), StackProfileLayer.Unknown);
        var conflict = WithBackend(VueSupabase(), new StackProfileLayer
        {
            Status = StackProfileLayerStatus.Conflict,
            Candidates = [new StackProfileCandidate("be-express", null, StackProfile.AgentSource, [])],
        });

        Assert.Contains(StackProfile.Validate(unknown, Context()), e => e.Field == "layers.backend.status");
        Assert.Empty(StackProfile.Validate(WithBackend(VueSupabase(), StackProfileLayer.Unknown, new StackProfileQuestion("backend", "Which backend?")), Context()));
        Assert.Contains(StackProfile.Validate(conflict, Context()), e => e.Field == "layers.backend.candidates");
    }

    [Fact]
    public void FR178_R5_The_vcs_layer_is_fixed_to_the_repository_host()
    {
        var gitLab = VueSupabase() with { Layers = VueSupabase().Layers with { Vcs = GitHub with { Option = StackPinning.GitLabVcs } } };
        var inferred = VueSupabase() with { Layers = VueSupabase().Layers with { Vcs = GitHub with { Status = StackProfileLayerStatus.Inferred } } };

        Assert.Contains(StackProfile.Validate(gitLab, Context()), e => e.Field == "layers.vcs.option");
        Assert.Contains(StackProfile.Validate(inferred, Context()), e => e.Field == "layers.vcs.status");
    }

    [Fact]
    public void FR184_S4_27_Hosts_structure_and_questions_are_checked()
    {
        var document = VueSupabase() with
        {
            Hosts = new StackProfileHosts { FrontendHost = "heroku", SeparateParts = ["frontend", "frontend"], Detected = ["vcs"] },
            Structure = [new StackProfileStructureEntry("frontend", "website", ""), new StackProfileStructureEntry("missing", "docs", "")],
            Questions = [new StackProfileQuestion("deploy", "?")],
        };

        var fields = StackProfile.Validate(document, Context()).Select(e => e.Field).ToList();

        Assert.Contains("hosts.frontendHost", fields);
        Assert.Contains("hosts.separateParts", fields);
        Assert.Contains("hosts.detected", fields);
        Assert.Contains("structure[0].role", fields);
        Assert.Contains("structure[1].path", fields);
        Assert.Contains("questions[0].layer", fields);
    }

    [Fact]
    public void FR180_A_reply_naming_be_django_is_STACK_PROFILE_INVALID_and_the_corrective_feedback_names_the_allowed_options()
    {
        var reply = StackProfile.Render("Django app.", WithBackend(VueSupabase(), Inferred("be-django", ".")));

        var checkedReply = StackProfile.CheckReply(reply, Context());

        Assert.Equal(ErrorCodes.StackProfileInvalid, checkedReply.Error!.Code);
        var feedback = StackProfile.CorrectiveFeedback(checkedReply.Error, StackTestCatalog.Catalog);
        Assert.Contains("be-django", feedback, StringComparison.Ordinal);
        Assert.Contains("- backend: be-aspnet, be-spring-boot, be-fastapi, be-express, be-nestjs", feedback, StringComparison.Ordinal);
        Assert.Contains("unsupported", feedback, StringComparison.Ordinal);

        // The second reply may mark the layer unsupported.
        var second = StackProfile.Render("Django app.", WithBackend(VueSupabase(), new StackProfileLayer { Status = StackProfileLayerStatus.Unsupported, Technology = "Django" }));
        Assert.True(StackProfile.CheckReply(second, Context()).IsSuccess);
    }

    // ---------------------------------------------------------------- detector

    [Fact]
    public void FR180_Given_a_Vue_frontend_and_supabase_migrations_the_draft_has_fe_vue_Backend_none_and_Supabase_BaaS_with_evidence()
    {
        var paths = BaseFiles.ToList();
        var manifests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["frontend/package.json"] = """{ "dependencies": { "vue": "^3.5.13" }, "devDependencies": { "vite": "^7.0.0" } }""",
        };
        var detection = StackDetector.Detect(paths, manifests, "github");

        var document = StackProfile.FromDetection(detection, StackPinning.GitHubVcs, "abc123");

        Assert.Equal(("fe-vue", "frontend", StackProfileLayerStatus.Inferred), (document.Layers.Frontend.Option!, document.Layers.Frontend.Path!, document.Layers.Frontend.Status));
        Assert.Equal(StackProfileLayerStatus.None, document.Layers.Backend.Status);
        Assert.Equal((StackIds.Supabase, StackIds.BaasMode), (document.Layers.Data.Option!, document.Layers.Data.Mode!));
        Assert.NotEmpty(document.Layers.Frontend.Evidence);
        Assert.Contains(document.Layers.Data.Evidence, e => e.File == "supabase/migrations");
        Assert.Equal((StackPinning.GitHubVcs, StackProfileLayerStatus.Fixed), (document.Layers.Vcs.Option!, document.Layers.Vcs.Status));
        Assert.Equal(StackIds.SupabaseHost, document.Hosts.DatabaseHost);
        Assert.Equal([StackChoices.DatabaseHost], document.Hosts.Detected);
        Assert.Equal("abc123", document.BaseCommitSha);
        Assert.Empty(StackProfile.Validate(document, Context()));
    }

    [Fact]
    public void FR180_Fill_in_manually_with_no_detection_leaves_every_layer_unknown_with_a_question()
    {
        var document = StackProfile.FromDetection(null, StackPinning.GitHubVcs, null);

        Assert.Equal([StackLayers.Frontend, StackLayers.Backend, StackLayers.Data], StackProfile.Unresolved(document));
        Assert.Equal(3, document.Questions.Count);
        Assert.Empty(StackProfile.Validate(document, Context()));
    }

    [Fact]
    public void FR180_S4_27_Example_6_Detector_says_be_express_agent_says_be_nestjs_is_conflict_until_a_user_picks_one()
    {
        var detection = new StackDetection(
            [new StackDetectedChoice(StackChoices.Backend, "be-express", "backend", null, ["`express` 5.1 in `backend/package.json`"])],
            []);
        var agent = WithBackend(VueSupabase(), Inferred("be-nestjs", "backend"));

        var reconciled = StackProfile.Reconcile(agent, detection, StackPinning.GitHubVcs, "abc123");

        var backend = reconciled.Layers.Backend;
        Assert.Equal((StackProfileLayerStatus.Conflict, (string?)null), (backend.Status, backend.Option));
        Assert.Equal(
            [("be-nestjs", StackProfile.AgentSource), ("be-express", StackProfile.DetectorSource)],
            backend.Candidates.Select(c => (c.Option, c.Source)));
        Assert.Contains(reconciled.Questions, q => q.Layer == StackLayers.Backend);
        Assert.Equal([StackLayers.Backend], StackProfile.Unresolved(reconciled));
        Assert.Equal(ErrorCodes.StackProfileIncomplete, StackProfile.Approve(reconciled, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs)).Error!.Code);

        // A user picks one: resolved.
        var picked = reconciled with { Layers = reconciled.Layers with { Backend = Inferred("be-express") with { Status = StackProfileLayerStatus.User } } };
        Assert.Empty(StackProfile.Unresolved(picked));
    }

    [Fact]
    public void FR180_S4_27_The_agent_agreeing_with_the_detector_stays_inferred_whatever_its_confidence()
    {
        var detection = new StackDetection([new StackDetectedChoice(StackChoices.Frontend, "fe-vue", "frontend", null, [])], []);

        var reconciled = StackProfile.Reconcile(VueSupabase() with { Layers = VueSupabase().Layers with { Frontend = Inferred("fe-vue", "frontend") with { Confidence = StackProfileConfidence.Low } } }, detection, StackPinning.GitHubVcs, "abc");

        Assert.Equal(StackProfileLayerStatus.Inferred, reconciled.Layers.Frontend.Status);
    }

    [Fact]
    public void FR184_Detected_hosts_are_preselected_and_marked_detected()
    {
        var detection = new StackDetection([new StackDetectedChoice(StackChoices.FrontendHost, "deploy-netlify", null, null, ["`netlify.toml`"])], []);

        var reconciled = StackProfile.Reconcile(VueSupabase(frontendHost: null) with { Hosts = StackProfileHosts.Empty }, detection, StackPinning.GitHubVcs, "abc");

        Assert.Equal("deploy-netlify", reconciled.Hosts.FrontendHost);
        Assert.Equal([StackChoices.FrontendHost], reconciled.Hosts.Detected);
    }

    [Fact]
    public void FR178_R5_Reconcile_fixes_the_vcs_layer_to_the_repository_host()
    {
        var reconciled = StackProfile.Reconcile(VueSupabase(), null, StackPinning.GitLabVcs, "abc");

        Assert.Equal((StackPinning.GitLabVcs, StackProfileLayerStatus.Fixed), (reconciled.Layers.Vcs.Option!, reconciled.Layers.Vcs.Status));
    }

    [Fact]
    public void FR180_Detector_evidence_strings_cite_their_files() =>
        Assert.Equal(
            ["frontend/package.json", "supabase/migrations", "netlify.toml"],
            StackProfile.EvidenceOf(["`vue` 3.5 in `frontend/package.json`", "`supabase/migrations/`", "`netlify.toml`", "no backend manifest found"]).Select(e => e.File));

    // ---------------------------------------------------------------- §4.27 approval worked examples

    [Fact]
    public void FR183_S4_27_Example_1_fe_vue_Backend_none_data_supabase_host_Vercel_pins_a_composition()
    {
        var approved = StackProfile.Approve(VueSupabase(), StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs));

        Assert.True(approved.IsSuccess, approved.Error?.Message);
        var composition = approved.Value.Composition!;
        Assert.False(approved.Value.Generalist);
        Assert.Equal(("fe-vue", "frontend"), (composition.Frontend!.Pack, composition.Frontend.Path));
        Assert.Null(composition.Backend);
        Assert.Equal(StackIds.Supabase, composition.Data!.Pack);
        Assert.Equal("deploy-vercel", composition.Deploy.FrontendHost);
        Assert.Equal(StackTestCatalog.Catalog.Version, composition.CatalogVersion);
        Assert.Equal(StackTestCatalog.Catalog.ContentHash, composition.CatalogHash);
    }

    [Fact]
    public void FR181_S4_27_Example_2_Backend_unknown_is_STACK_PROFILE_INCOMPLETE_naming_backend()
    {
        var document = WithBackend(VueSupabase(), StackProfileLayer.Unknown, new StackProfileQuestion("backend", "Which backend?"));

        var approved = StackProfile.Approve(document, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs));

        Assert.Equal(ErrorCodes.StackProfileIncomplete, approved.Error!.Code);
        Assert.Equal([StackLayers.Backend], approved.Error.Details);
        Assert.Contains("backend", approved.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR183_S4_27_Example_3_Frontend_and_backend_none_is_STACK_INCOMPATIBLE_R1()
    {
        var document = VueSupabase() with
        {
            Layers = new StackProfileLayers(NoLayer, NoLayer, NoLayer, GitHub),
            Hosts = StackProfileHosts.Empty,
        };

        var approved = StackProfile.Approve(document, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs));

        Assert.Equal(ErrorCodes.StackIncompatible, approved.Error!.Code);
        Assert.Contains(approved.Error.Violations, v => v.Rule == "R1");
    }

    [Fact]
    public void FR181_S4_27_Example_4_Backend_unsupported_with_no_generalist_choice_is_STACK_PROFILE_INCOMPLETE()
    {
        var document = WithBackend(VueSupabase(), new StackProfileLayer { Status = StackProfileLayerStatus.Unsupported, Technology = "Django" });

        Assert.Equal(ErrorCodes.StackProfileIncomplete, StackProfile.Approve(document, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs)).Error!.Code);
    }

    [Fact]
    public void FR181_S4_27_Example_5_Backend_unsupported_with_generalist_approves_with_no_composition()
    {
        var document = WithBackend(VueSupabase(), new StackProfileLayer { Status = StackProfileLayerStatus.Unsupported, Technology = "Django" }) with { Generalist = true };

        var approved = StackProfile.Approve(document, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs));

        Assert.True(approved.IsSuccess);
        Assert.Equal((null, true), (approved.Value.Composition, approved.Value.Generalist));
    }

    [Fact]
    public void FR181_S4_27_Generalist_never_resolves_an_unknown_or_conflict_layer()
    {
        var document = WithBackend(VueSupabase(), StackProfileLayer.Unknown, new StackProfileQuestion("backend", "?")) with { Generalist = true };

        Assert.Equal(ErrorCodes.StackProfileIncomplete, StackProfile.Approve(document, StackTestCatalog.Catalog, null).Error!.Code);
    }

    [Fact]
    public void FR183_Given_Prisma_with_ASPNET_approval_is_STACK_INCOMPATIBLE_with_the_R3_reason()
    {
        var files = new HashSet<string>(BaseFiles, StringComparer.OrdinalIgnoreCase) { "backend/Api.csproj" };
        var document = VueSupabase() with
        {
            Layers = new StackProfileLayers(Inferred("fe-vue", "frontend"), Inferred("be-aspnet", "backend"), Inferred("data-prisma") with { Status = StackProfileLayerStatus.User }, GitHub),
            Hosts = StackProfileHosts.Empty,
        };

        Assert.Empty(StackProfile.Validate(document, Context(files)));
        var approved = StackProfile.Approve(document, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs));

        Assert.Equal(ErrorCodes.StackIncompatible, approved.Error!.Code);
        Assert.Contains(approved.Error.Violations, v => v.Rule == "R3");
    }

    [Fact]
    public void FR184_S4_28_Hosts_including_separate_parts_are_part_of_the_pinned_composition()
    {
        var files = new HashSet<string>(BaseFiles, StringComparer.OrdinalIgnoreCase) { "backend/package.json" };
        var document = VueSupabase() with
        {
            Layers = new StackProfileLayers(Inferred("fe-vue", "frontend"), Inferred("be-express", "backend"), Inferred("data-prisma"), GitHub),
            Hosts = new StackProfileHosts
            {
                FrontendHost = "deploy-railway",
                BackendHost = "deploy-railway",
                DatabaseHost = "railway-postgres",
                SeparateParts = ["database"],
            },
        };

        Assert.Empty(StackProfile.Validate(document, Context(files)));
        var approved = StackProfile.Approve(document, StackTestCatalog.Catalog, new StackVcsPick(StackPinning.GitHubVcs));

        Assert.True(approved.IsSuccess, approved.Error?.Message);
        Assert.Equal(("deploy-railway", "deploy-railway", "railway-postgres"), (approved.Value.Composition!.Deploy.FrontendHost!, approved.Value.Composition.Deploy.BackendHost!, approved.Value.Composition.Deploy.DatabaseHost!));
        Assert.Equal(["database"], approved.Value.Composition.Deploy.SeparateParts!);
    }

    [Fact]
    public void FR181_Needs_your_input_lists_unknown_conflict_and_unsupported_layers_first()
    {
        var document = VueSupabase() with
        {
            Layers = new StackProfileLayers(
                Inferred("fe-vue", "frontend"),
                new StackProfileLayer { Status = StackProfileLayerStatus.Unsupported, Technology = "Django" },
                StackProfileLayer.Unknown,
                GitHub),
            Generalist = true,
        };

        Assert.Equal([StackLayers.Backend, StackLayers.Data], StackProfile.NeedsInput(document));
        Assert.Equal([StackLayers.Data], StackProfile.Unresolved(document));
    }

    // ---------------------------------------------------------------- artifact rules

    [Fact]
    public void FR183_S4_27_A_stack_profile_is_drafted_only_in_analysis_by_the_Stack_Analyst()
    {
        Assert.Equal(Phase.Analysis, ArtifactRules.ProducingPhase(ArtifactType.StackProfile));
        Assert.Equal(Domain.Agents.AgentKind.StackAnalyst, ArtifactRules.RerunAgent(ArtifactType.StackProfile));
        Assert.Equal(ArtifactStatus.Draft, ArtifactRules.InitialStatus(ArtifactType.StackProfile));
        Assert.Equal(Phase.Analysis, RestoreRules.RestorablePhase(ArtifactType.StackProfile));
        foreach (var phase in Enum.GetValues<Phase>())
        {
            var allowed = ArtifactRules.CanCreateDraft(ArtifactType.StackProfile, phase, ProjectStatus.Active);
            Assert.Equal(phase == Phase.Analysis, allowed.IsSuccess);
            if (phase != Phase.Analysis)
            {
                Assert.Equal(ErrorCodes.WrongPhase, allowed.Error!.Code);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FR183_Rejecting_a_stack_profile_always_re_runs_the_Stack_Analyst_even_a_human_edit(bool authoredByAgent)
    {
        var rejected = ArtifactRules.Reject(ArtifactType.StackProfile, ArtifactStatus.Draft, authoredByAgent);

        Assert.Equal((ArtifactStatus.Rejected, Domain.Agents.AgentKind.StackAnalyst), (rejected.Value.NewStatus, rejected.Value.RerunAgent));

        // FR-17 still holds for the other types: a human edit re-runs nothing.
        Assert.Null(ArtifactRules.Reject(ArtifactType.ProjectRequest, ArtifactStatus.Draft, authoredByAgent: false).Value.RerunAgent);
    }

    [Theory]
    [InlineData(Phase.Analysis, null)]
    [InlineData(Phase.Refinement, Phase.Analysis)]
    [InlineData(Phase.Planning, Phase.Analysis)]
    public void FR183_S4_27_S4_4_Approving_a_new_stack_profile_invalidates_everything_downstream_and_falls_back_to_analysis(Phase current, Phase? fallback)
    {
        var effect = InvalidationCascade.Effect(ArtifactType.StackProfile, current);

        Assert.Equal(
            [ArtifactType.ProjectRequest, ArtifactType.TechnicalSpecification, ArtifactType.ImplementationPlan, ArtifactType.OptimizationPlan],
            effect.InvalidatedTypes);
        Assert.Equal(StepReset.AllSteps, effect.StepReset);
        Assert.Equal(fallback, effect.FallbackPhase);
    }

    [Fact]
    public void FR183_S4_27_Source_items_and_the_codebase_summary_still_invalidate_nothing_and_no_cascade_touches_the_profile()
    {
        foreach (var type in Enum.GetValues<ArtifactType>())
        {
            Assert.DoesNotContain(ArtifactType.StackProfile, InvalidationCascade.Effect(type, Phase.Review).InvalidatedTypes);
        }

        Assert.Empty(InvalidationCascade.Effect(ArtifactType.SourceItems, Phase.Analysis).InvalidatedTypes);
        Assert.Empty(InvalidationCascade.Effect(ArtifactType.CodebaseSummary, Phase.Analysis).InvalidatedTypes);
    }
}
