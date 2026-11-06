using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-187, FR-189, FR-190, FR-192, §4.28: the groups, windows, R8, R10 and R13 (worked examples verbatim), and the group rules.</summary>
public class DeploymentGroupingTests
{
    private const DeploymentPart F = DeploymentPart.Frontend;
    private const DeploymentPart B = DeploymentPart.Backend;
    private const DeploymentPart D = DeploymentPart.Database;

    private static readonly string[] AllThree = ["frontend", "backend", "database"];
    private static readonly string[] DatabaseOnly = ["database"];
    private static readonly string[] BackendOnly = ["backend"];
    private static readonly string[] FrontendOnly = ["frontend"];
    private static readonly string[] FrontendAndBackend = ["frontend", "backend"];

    private static StackSelection With(StackSelection selection, params string[] separate) =>
        selection with { Deploy = (selection.Deploy ?? new StackDeployPick()) with { SeparateParts = separate } };

    private static StackSelection RailwayX3(string frontend = "fe-react-vite") =>
        Sel(frontend, "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-railway", "deploy-railway", "railway-postgres");

    private static string Describe(StackComposition composition) => string.Join(
        " · ",
        DeploymentGrouping.Windows(composition).Select(w => $"{w.Platform}:{string.Join('+', w.Parts.Select(DeploymentGrouping.WireName))}{(w.Environment is { } e ? $"({e.ToWire()})" : string.Empty)}"));

    // ── §4.28 worked examples ────────────────────────────────────────────

    [Fact]
    public void FR187_Example1_Railway_x3_is_one_group_of_frontend_backend_and_database()
    {
        var composition = Valid(RailwayX3());

        var group = Assert.Single(DeploymentGrouping.Groups(composition));
        Assert.Equal((DeploymentProvider.Railway, SharedProjectCapability.Project), (group.Provider, group.Capability));
        Assert.Equal([F, B, D], group.Parts);
        Assert.Equal("railway:frontend+backend+database", Describe(composition));
    }

    [Fact]
    public void FR187_Example2_Netlify_Render_Render_is_a_Netlify_frontend_and_a_Render_backend_and_database()
    {
        var composition = Valid(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-netlify", "deploy-render", "render-postgres"));

        Assert.Equal([B, D], Assert.Single(DeploymentGrouping.Groups(composition)).Parts);
        Assert.Equal("render:backend+database · netlify:frontend", Describe(composition));
    }

    [Fact]
    public void FR187_Example3_Vercel_Railway_Railway_is_a_Vercel_frontend_and_a_Railway_backend_and_database()
    {
        var composition = Valid(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-vercel", "deploy-railway", "railway-postgres"));

        Assert.Equal("railway:backend+database · vercel:frontend", Describe(composition));
    }

    [Fact]
    public void FR187_Example4_Vercel_Vercel_Supabase_is_a_Vercel_group_and_one_Supabase_window_per_environment()
    {
        var composition = Valid(Sel("fe-nextjs", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-vercel", "deploy-vercel", "supabase"));

        var group = Assert.Single(DeploymentGrouping.Groups(composition));
        Assert.Equal((DeploymentProvider.Vercel, SharedProjectCapability.SharedSetup), (group.Provider, group.Capability));
        Assert.Equal("vercel:frontend+backend · supabase:database(production) · supabase:database(preview)", Describe(composition));
    }

    [Fact]
    public void FR189_Example5_Railway_x3_with_the_database_separate_keeps_frontend_and_backend_grouped()
    {
        var composition = Valid(With(RailwayX3(), DatabaseOnly));

        Assert.Equal([F, B], Assert.Single(DeploymentGrouping.Groups(composition)).Parts);
        Assert.Equal("railway:frontend+backend · railway:database", Describe(composition));
        Assert.Equal(DatabaseOnly, composition.Deploy.SeparateParts);
    }

    [Fact]
    public void FR189_Example6_Railway_x3_with_all_three_separate_has_three_windows()
    {
        var composition = Valid(With(RailwayX3(), AllThree) with { OriginModel = "cross-origin" });

        Assert.Empty(DeploymentGrouping.Groups(composition));
        Assert.Equal("railway:frontend · railway:backend · railway:database", Describe(composition));
    }

    [Fact]
    public void FR189_Example7_Render_Render_none_with_the_backend_separate_has_no_group_because_a_group_needs_two()
    {
        var composition = Valid(With(Sel("fe-react-vite", "be-fastapi", vcs: "vcs-github", frontendHost: "deploy-render", backendHost: "deploy-render"), BackendOnly));

        Assert.Empty(DeploymentGrouping.Groups(composition));
        Assert.Equal("render:frontend · render:backend", Describe(composition));
    }

    [Fact]
    public void FR187_Example8_A_Railway_frontend_alone_has_one_window_and_nothing_to_separate()
    {
        var composition = Valid(Sel("fe-react-vite", "none", "none", "vcs-github", "deploy-railway"));

        Assert.Empty(DeploymentGrouping.Groups(composition));
        Assert.Equal("railway:frontend", Describe(composition));
    }

    // ── §4.28 composition rules ──────────────────────────────────────────

    [Fact]
    public void FR187_External_and_Supabase_never_group_and_frontend_only_hosts_never_group()
    {
        Assert.Empty(DeploymentGrouping.Groups("deploy-netlify", "deploy-render", "external", null));
        Assert.Empty(DeploymentGrouping.Groups("deploy-railway", null, "supabase", null));
        Assert.Empty(DeploymentGrouping.Groups("deploy-netlify", "deploy-netlify", null, null));
    }

    [Fact]
    public void FR192_The_SharedProject_capability_per_platform()
    {
        Assert.Equal(SharedProjectCapability.Project, DeploymentGrouping.Capability(DeploymentProvider.Railway));
        Assert.Equal(SharedProjectCapability.Project, DeploymentGrouping.Capability(DeploymentProvider.Render));
        Assert.Equal(SharedProjectCapability.Project, DeploymentGrouping.Capability(DeploymentProvider.Fake));
        Assert.Equal(SharedProjectCapability.SharedSetup, DeploymentGrouping.Capability(DeploymentProvider.Vercel));
        Assert.Equal(SharedProjectCapability.None, DeploymentGrouping.Capability(DeploymentProvider.Netlify));
    }

    [Fact]
    public void FR187_A_0_4_0_composition_keeps_its_exact_JSON_and_behaviour()
    {
        var composition = Valid(RailwayX3());

        Assert.Null(composition.Deploy.SeparateParts);
        var json = StackCompositionJson.Serialize(composition);
        Assert.DoesNotContain("separateParts", json, StringComparison.Ordinal);
        Assert.Equal(composition.Deploy, StackCompositionJson.Deserialize(json)!.Deploy);
    }

    [Fact]
    public void FR189_SeparateParts_survives_the_stored_composition_and_a_re_pin()
    {
        var composition = Valid(With(RailwayX3(), DatabaseOnly));
        var stored = StackCompositionJson.Deserialize(StackCompositionJson.Serialize(composition))!;

        Assert.Equal(DatabaseOnly, stored.Deploy.SeparateParts);
        Assert.Equal(DatabaseOnly, Valid(StackPinning.ToSelection(stored)).Deploy.SeparateParts);
    }

    // ── R13 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FR189_R13_A_part_sharing_no_platform_is_removed_on_normalise_and_the_response_says_so()
    {
        var composition = Valid(With(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-netlify", "deploy-render", "render-postgres"), FrontendOnly));

        Assert.Null(composition.Deploy.SeparateParts);
        var caveat = Assert.Single(composition.Caveats, c => c.Rule == "R13");
        Assert.Equal((StackCompatibility.SeparatePartsLayer, "frontend"), (caveat.Layer, caveat.Option));
        Assert.Contains(Evaluate(With(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-netlify", "deploy-render", "render-postgres"), FrontendOnly)).Caveats, c => c.Rule == "R13");
    }

    [Fact]
    public void FR189_R13_A_separate_part_keeps_its_place_while_it_shares_a_platform_even_with_another_separate_part()
    {
        var kept = DeploymentGrouping.NormaliseSeparate("deploy-railway", "deploy-railway", null, AllThree);

        Assert.Equal(FrontendAndBackend, kept.Kept);
        Assert.Equal(DatabaseOnly, kept.Removed);
    }

    [Fact]
    public void FR189_An_unknown_part_is_VALIDATION()
    {
        var validation = StackCompatibility.Validate(With(RailwayX3(), "cache"), Catalog);

        Assert.Equal("deploy.separateParts", Assert.Single(validation.InputErrors).Field);
    }

    // ── R8 ───────────────────────────────────────────────────────────────

    [Fact]
    public void FR189_R8_Railway_Postgres_in_a_shared_project_with_the_backend_is_allowed()
    {
        var evaluation = Evaluate(RailwayX3());

        Assert.Equal(StackOptionState.Allowed, Option(evaluation, StackChoices.DatabaseHost, "railway-postgres").State);
        Assert.DoesNotContain(evaluation.Caveats, c => c.Rule == "R8");
    }

    [Fact]
    public void FR189_R8_A_separate_Railway_database_has_the_caveat_of_FR189()
    {
        var database = Option(Evaluate(With(RailwayX3(), DatabaseOnly)), StackChoices.DatabaseHost, "railway-postgres");

        Assert.Equal(StackOptionState.Caveat, database.State);
        Assert.Equal("Separate Railway project: no private network, public connection, higher latency", Assert.Single(database.Reasons).Text);
        Assert.Contains(Valid(With(RailwayX3(), DatabaseOnly)).Caveats, c => c is { Layer: StackChoices.DatabaseHost, Rule: "R8" });
    }

    [Fact]
    public void FR189_R8_Splitting_the_backend_from_a_Railway_database_has_the_caveat_too()
    {
        Assert.Equal(StackOptionState.Caveat, Option(Evaluate(With(RailwayX3(), BackendOnly) with { OriginModel = "cross-origin" }), StackChoices.DatabaseHost, "railway-postgres").State);
    }

    [Fact]
    public void FR189_R8_A_Nextjs_full_stack_app_in_the_shared_project_is_allowed_and_splitting_it_is_a_caveat()
    {
        var fullStack = Sel("fe-nextjs", "none", "data-prisma", "vcs-github", "deploy-railway", null, "railway-postgres");

        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(fullStack), StackChoices.DatabaseHost, "railway-postgres").State);
        Assert.Equal(StackOptionState.Caveat, Option(Evaluate(With(fullStack, FrontendOnly)), StackChoices.DatabaseHost, "railway-postgres").State);
    }

    [Fact]
    public void FR189_R8_Render_Postgres_keeps_its_0_4_0_rule_when_separate()
    {
        var render = Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-render", "deploy-render", "render-postgres");

        Assert.Equal(StackOptionState.Allowed, Option(Evaluate(With(render, DatabaseOnly)), StackChoices.DatabaseHost, "render-postgres").State);
    }

    // ── R10 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FR189_R10_A_Railway_SPA_and_backend_in_one_project_can_be_same_origin()
    {
        Assert.Equal("same-origin", Valid(RailwayX3()).OriginModel);
        Assert.Equal("same-origin", Valid(With(RailwayX3(), DatabaseOnly)).OriginModel);
    }

    [Theory]
    [InlineData("frontend")]
    [InlineData("backend")]
    public void FR189_R10_Splitting_them_leaves_cross_origin_and_same_origin_is_invalidated(string split)
    {
        var selection = With(RailwayX3(), split);

        Assert.Equal("cross-origin", Valid(selection).OriginModel);
        var evaluation = Evaluate(selection with { OriginModel = "same-origin" });
        Assert.Equal(StackOptionState.Invalidated, Option(evaluation, StackChoices.OriginModel, "same-origin").State);
        Assert.Contains(Violations(selection with { OriginModel = "same-origin" }), v => v is { Layer: StackChoices.OriginModel, Option: "same-origin", Rule: "R10" });
    }

    // ── /options agrees ──────────────────────────────────────────────────

    [Fact]
    public void FR187_Groups_and_options_agree_over_the_whole_composition_space()
    {
        string[] frontends = ["fe-react-vite", "fe-nextjs", "none"];
        string[] backends = ["be-fastapi", "be-aspnet", "none"];
        string[] datas = ["data-sqlalchemy", "data-supabase", "none"];
        var frontendHosts = Catalog.Choice(StackChoices.FrontendHost).Options.Select(o => o.Id).ToList();
        var backendHosts = Catalog.Choice(StackChoices.BackendHost).Options.Select(o => o.Id).ToList();
        var databaseHosts = Catalog.Choice(StackChoices.DatabaseHost).Options.Select(o => o.Id).ToList();
        string[][] subsets =
        [
            [], ["frontend"], ["backend"], ["database"], ["frontend", "backend"], ["frontend", "database"], ["backend", "database"], AllThree,
        ];
        var compared = 0;
        foreach (var frontend in frontends)
        {
            foreach (var backend in backends)
            {
                foreach (var data in datas)
                {
                    foreach (var fh in frontendHosts)
                    {
                        foreach (var bh in backendHosts)
                        {
                            foreach (var dh in databaseHosts)
                            {
                                foreach (var subset in subsets)
                                {
                                    var selection = With(Sel(frontend, backend, data, "vcs-github", fh, bh, dh), subset);
                                    if (StackCompatibility.Validate(selection, Catalog).Composition is not { } composition)
                                    {
                                        continue;
                                    }

                                    compared++;
                                    var options = StackCompatibility.Evaluate(selection, Catalog).Value;
                                    Assert.Equal(DeploymentGrouping.Groups(composition).Select(Key), options.Groups.Select(Key));
                                    Assert.Equal(DeploymentGrouping.Windows(composition).Select(Key), options.Windows.Select(Key));
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.True(compared > 100, $"Only {compared} valid compositions were compared.");
    }

    private static string Key(DeploymentGroupSpec g) => $"{g.Provider}:{string.Join('+', g.Parts)}:{g.Capability}";

    private static string Key(DeploymentWindow w) => $"{w.Platform}:{string.Join('+', w.Parts)}:{w.Capability}:{w.Environment}";

    // ── Group rules ──────────────────────────────────────────────────────

    private static DeploymentGroupPut Put(
        DeploymentProvider provider = DeploymentProvider.Railway,
        StackComposition? composition = null,
        bool create = true,
        string? name = "todo",
        bool link = false,
        string? resource = null,
        string? linkFrontend = null,
        string? linkDatabase = null,
        bool hasGroup = false,
        Guid? connection = null,
        IReadOnlyList<DeploymentPart>? alone = null,
        bool fake = false) => new(
            provider,
            composition ?? Valid(RailwayX3()),
            GitProvider.GithubApp,
            fake,
            connection ?? Guid.CreateVersion7(),
            create,
            name,
            link,
            resource,
            linkFrontend,
            null,
            linkDatabase,
            hasGroup,
            alone ?? []);

    [Fact]
    public void FR190_A_create_on_the_groups_platform_passes()
    {
        Assert.True(DeploymentGroupRules.Validate(Put()).IsSuccess);
    }

    [Fact]
    public void FR187_A_platform_the_stack_doesnt_use_is_STACK_INCOMPATIBLE()
    {
        Assert.Equal(ErrorCodes.StackIncompatible, DeploymentGroupRules.Validate(Put(DeploymentProvider.Render)).Error!.Code);
    }

    [Fact]
    public void FR187_A_platform_with_one_part_is_DEPLOYMENT_GROUP_INVALID()
    {
        var one = Valid(Sel("fe-react-vite", "none", "none", "vcs-github", "deploy-railway"));

        var error = DeploymentGroupRules.Validate(Put(composition: one)).Error!;

        Assert.Equal(ErrorCodes.DeploymentGroupInvalid, error.Code);
        Assert.StartsWith("These parts can't share a Railway project:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR191_A_part_set_up_on_its_own_is_never_moved_into_a_group()
    {
        Assert.Equal(ErrorCodes.DeploymentGroupInvalid, DeploymentGroupRules.Validate(Put(alone: [B])).Error!.Code);
    }

    [Fact]
    public void FR190_Link_needs_a_project_and_parts_of_the_group()
    {
        Assert.Equal(ErrorCodes.Validation, DeploymentGroupRules.Validate(Put(create: false, name: null, link: true)).Error!.Code);
        Assert.True(DeploymentGroupRules.Validate(Put(create: false, name: null, link: true, resource: "p1", linkFrontend: "p1:s1")).IsSuccess);

        var withoutDatabase = Valid(With(RailwayX3(), DatabaseOnly));
        Assert.Equal(
            ErrorCodes.DeploymentGroupInvalid,
            DeploymentGroupRules.Validate(Put(composition: withoutDatabase, create: false, name: null, link: true, resource: "p1", linkDatabase: "s9")).Error!.Code);
    }

    [Fact]
    public void FR192_Vercel_link_needs_a_project_per_part_and_no_shared_resource()
    {
        var vercel = Valid(Sel("fe-nextjs", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-vercel", "deploy-vercel", "supabase"));

        Assert.Equal(ErrorCodes.Validation, DeploymentGroupRules.Validate(Put(DeploymentProvider.Vercel, vercel, create: false, name: null, link: true, linkFrontend: "prj_web")).Error!.Code);
    }

    [Fact]
    public void FR191_A_rebind_needs_a_group_and_no_body_but_the_connection()
    {
        Assert.Equal(ErrorCodes.Validation, DeploymentGroupRules.Validate(Put(create: false, name: null)).Error!.Code);
        Assert.True(DeploymentGroupRules.Validate(Put(create: false, name: null, hasGroup: true)).IsSuccess);
    }

    [Fact]
    public void FR190_A_name_must_leave_room_for_the_part_suffixes()
    {
        Assert.Equal(ErrorCodes.Validation, DeploymentGroupRules.Validate(Put(name: new string('a', DeploymentGroupRules.NameMaxLength + 1))).Error!.Code);
    }

    [Fact]
    public void FR192_Part_names_come_from_the_group()
    {
        Assert.Equal("todo-web", DeploymentGroupRules.PartName("todo", F));
        Assert.Equal("todo-api", DeploymentGroupRules.PartName("todo", B));
        Assert.Equal("todo-db", DeploymentGroupRules.PartName("todo", D));
    }

    [Fact]
    public void FR190_A_failed_part_makes_the_group_error_while_the_others_stay_ready()
    {
        Assert.Equal(DeploymentGroupStatus.Error, DeploymentGroupRules.StatusOf([DeploymentGroupPartState.Ready, DeploymentGroupPartState.Error, DeploymentGroupPartState.Ready]));
        Assert.Equal(DeploymentGroupStatus.Configuring, DeploymentGroupRules.StatusOf([DeploymentGroupPartState.Error, DeploymentGroupPartState.Configuring]));
        Assert.Equal(DeploymentGroupStatus.Attention, DeploymentGroupRules.StatusOf([DeploymentGroupPartState.Ready, DeploymentGroupPartState.Attention]));
        Assert.Equal(DeploymentGroupStatus.Ready, DeploymentGroupRules.StatusOf([DeploymentGroupPartState.Ready, DeploymentGroupPartState.Ready]));
        Assert.Equal(DeploymentGroupStatus.Attention, DeploymentGroupRules.StatusOf([]));
    }

    [Fact]
    public void FR190_Retry_takes_only_failed_or_missing_parts()
    {
        Assert.True(DeploymentGroupRules.Retries((DeploymentTargetStatus?)null));
        Assert.True(DeploymentGroupRules.Retries(DeploymentTargetStatus.Error));
        Assert.False(DeploymentGroupRules.Retries(DeploymentTargetStatus.Ready));
        Assert.True(DeploymentGroupRules.Retries(ManagedDatabaseStatus.Error));
        Assert.False(DeploymentGroupRules.Retries(ManagedDatabaseStatus.Available));
    }

    [Fact]
    public void FR191_A_part_joins_a_group_explicitly_only_where_the_composition_keeps_it()
    {
        var group = new DeploymentGroup { Provider = DeploymentProvider.Railway, SharedResourceId = "p1" };
        var composition = Valid(With(RailwayX3(), DatabaseOnly));

        Assert.True(DeploymentGroupRules.CanJoin(composition, B, DeploymentProvider.Railway, group, false).IsSuccess);
        Assert.Equal(ErrorCodes.DeploymentGroupInvalid, DeploymentGroupRules.CanJoin(composition, D, DeploymentProvider.Railway, group, false).Error!.Code);
        Assert.Equal(ErrorCodes.DeploymentGroupInvalid, DeploymentGroupRules.CanJoin(composition, B, DeploymentProvider.Railway, null, false).Error!.Code);
        Assert.Equal(ErrorCodes.DeploymentGroupInvalid, DeploymentGroupRules.CanJoin(composition, B, DeploymentProvider.Render, group, false).Error!.Code);
        Assert.Equal(
            ErrorCodes.DeploymentGroupInvalid,
            DeploymentGroupRules.CanJoin(composition, B, DeploymentProvider.Railway, new DeploymentGroup { Provider = DeploymentProvider.Railway }, false).Error!.Code);
    }

    [Fact]
    public void FR190_A_Railway_part_is_linked_as_project_and_service()
    {
        Assert.Equal("p1:s1", DeploymentGroupRules.LinkId(DeploymentProvider.Railway, "p1", "s1"));
        Assert.Equal("p1:s1", DeploymentGroupRules.LinkId(DeploymentProvider.Railway, "p1", "p1:s1"));
        Assert.Equal("srv-1", DeploymentGroupRules.LinkId(DeploymentProvider.Render, "prj-1", "srv-1"));
    }

    [Fact]
    public void FR191_The_shared_project_link()
    {
        Assert.Equal("https://railway.com/project/p1", DeploymentGroupRules.SharedProjectUrl(DeploymentProvider.Railway, "p1"));
        Assert.Equal("https://dashboard.render.com/project/prj-1", DeploymentGroupRules.SharedProjectUrl(DeploymentProvider.Render, "prj-1"));
        Assert.Null(DeploymentGroupRules.SharedProjectUrl(DeploymentProvider.Vercel, null));
    }

    [Fact]
    public void FR192_A_standalone_Railway_database_stands_alone_only_when_no_group_holds_it()
    {
        Assert.False(DeploymentGrouping.RailwayDatabaseStandsAlone(Valid(RailwayX3())));
        Assert.True(DeploymentGrouping.RailwayDatabaseStandsAlone(Valid(With(RailwayX3(), DatabaseOnly))));
        Assert.True(DeploymentGrouping.RailwayDatabaseStandsAlone(Valid(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-render", "deploy-render", "railway-postgres"))));
        Assert.False(DeploymentGrouping.RailwayDatabaseStandsAlone(Valid(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-render", "deploy-render", "render-postgres"))));
    }

    [Fact]
    public void FR192_The_app_decides_whether_a_Railway_database_is_private()
    {
        Assert.True(DeploymentGrouping.RailwayDatabaseSharesProjectWithApp(Valid(RailwayX3())));
        Assert.True(DeploymentGrouping.RailwayDatabaseSharesProjectWithApp(Valid(With(RailwayX3(), FrontendOnly) with { OriginModel = "cross-origin" })));

        // Railway x3 with the backend split: frontend and database stay grouped, but the backend can't resolve the reference.
        var split = Valid(With(RailwayX3(), BackendOnly) with { OriginModel = "cross-origin" });
        Assert.Equal([F, D], Assert.Single(DeploymentGrouping.Groups(split)).Parts);
        Assert.False(DeploymentGrouping.RailwayDatabaseStandsAlone(split));
        Assert.False(DeploymentGrouping.RailwayDatabaseSharesProjectWithApp(split));

        // Frontend and database on Railway, the backend elsewhere.
        var elsewhere = Valid(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-railway", "deploy-render", "railway-postgres"));
        Assert.Equal([F, D], Assert.Single(DeploymentGrouping.Groups(elsewhere)).Parts);
        Assert.False(DeploymentGrouping.RailwayDatabaseSharesProjectWithApp(elsewhere));

        var fullStack = Sel("fe-nextjs", "none", "data-prisma", "vcs-github", "deploy-railway", null, "railway-postgres");
        Assert.True(DeploymentGrouping.RailwayDatabaseSharesProjectWithApp(Valid(fullStack)));
        Assert.False(DeploymentGrouping.RailwayDatabaseSharesProjectWithApp(Valid(With(fullStack, FrontendOnly))));
    }

    [Fact]
    public void FR188_A_lone_Render_database_has_two_windows_and_a_lone_Railway_one_has_one()
    {
        var render = Valid(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", "deploy-netlify", "deploy-railway", "render-postgres"));

        Assert.Equal("netlify:frontend · railway:backend · render:database(production) · render:database(preview)", Describe(render));
        Assert.Equal("railway:frontend+backend · railway:database", Describe(Valid(With(RailwayX3(), DatabaseOnly))));
    }

    [Fact]
    public void FR192_Environment_derivation_records_the_separate_project_origin()
    {
        var composition = Valid(With(RailwayX3(), DatabaseOnly));

        var vars = DerivedEnvRules.Derive(new DerivedEnvInput(
            composition, "VITE_", [], [new DerivedEnvDatabase(DeploymentEnvironment.Production, ManagedDatabaseProvider.Railway, "postgresql://postgres:x@a.proxy.rlwy.net:1/railway", SeparateProject: true)]));

        var url = Assert.Single(vars, v => v.Key == DerivedEnvRules.DatabaseUrl);
        Assert.Equal("from Railway Postgres (separate project)", url.DerivedFrom);
        Assert.Equal("postgresql://postgres:x@a.proxy.rlwy.net:1/railway", url.Values[DeploymentEnvironment.Production]);
    }

    [Fact]
    public void FR192_A_separate_Railway_database_summarises_its_public_host_not_a_reference()
    {
        var connection = new ManagedDatabaseConnection { Url = "postgresql://postgres:x@a.proxy.rlwy.net:15432/railway", RailwaySeparateProject = true };

        Assert.Equal("a.proxy.rlwy.net:15432/railway", ManagedDatabaseRules.Summary(ManagedDatabaseProvider.Railway, connection));
    }
}
