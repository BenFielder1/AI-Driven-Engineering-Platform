using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Tevrin.Api.Auth;
using Tevrin.Api.Json;
using Tevrin.Domain.Access;
using Tevrin.Domain.Common;
using Tevrin.Domain.Deployments;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Execution;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Infrastructure.Persistence.Conversion;
using Tevrin.Pack.Sdlc;

namespace Tevrin.UnitTests.Common;

/// <summary>
/// FR-326 (§4.77): wire and stored identifiers move from <c>aide</c> to <c>tevrin</c>, and the old forms are still read.
/// </summary>
public sealed class Fr326WireNameTests
{
    // ---------- tokens ----------

    [Fact]
    public void FR326_New_tokens_start_tevrin_pat()
    {
        Assert.StartsWith("tevrin_pat_", ApiTokenRules.Format(new byte[32]), StringComparison.Ordinal);
        Assert.True(ApiTokenRules.IsWellFormed(ApiTokenRules.Format(new byte[32])));
    }

    [Fact]
    public void FR326_A_0_7_0_aide_pat_token_is_still_well_formed() =>
        Assert.True(ApiTokenRules.IsWellFormed("aide_pat_" + new string('A', 43)));

    // ---------- plans ----------

    private const string PlanReply = """
        {"steps": [
          {"number": 1, "title": "Skeleton", "task": "Create the app.", "files": [{"path": "src/app.ts", "action": "create"}], "dependencies": []},
          {"number": 2, "title": "API", "task": "Add routes.", "files": [{"path": "src/routes.ts", "action": "create"}], "dependencies": [1]}
        ]}
        """;

    [Theory]
    [InlineData("aide-plan")]
    [InlineData("tevrin-plan")]
    public void FR326_A_stored_plan_block_parses_under_either_name(string language)
    {
        var steps = PlanDocument.ParseAgentOutput(PlanReply, 20).Value;
        var markdown = PlanDocument.Render(SdlcArtifacts.ImplementationPlan, steps)
            .Replace(Fence + PlanDocument.BlockLanguage + Lf, Fence + language + Lf, StringComparison.Ordinal);

        var parsed = PlanDocument.ParseMarkdown(markdown, 20);

        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(PlanDocument.ToJson(steps), PlanDocument.ToJson(parsed.Value));
    }

    [Fact]
    public void FR326_A_plan_with_a_tevrin_plan_and_an_aide_plan_block_is_INVALID_PLAN() =>
        Assert.Equal(
            ErrorCodes.InvalidPlan,
            PlanDocument.ParseMarkdown(
                Fence + "tevrin-plan" + Lf + "{}" + Lf + Fence + Lf + Lf + Fence + "aide-plan" + Lf + "{}" + Lf + Fence, null).Error!.Code);

    [Fact]
    public void FR326_Plan_block_names()
    {
        Assert.Equal(("tevrin-plan", "aide-plan"), (PlanDocument.TevrinBlockLanguage, PlanDocument.LegacyBlockLanguage));

        // FR-328: new plans write tevrin-plan from the re-baseline commit on.
        Assert.Equal(PlanDocument.TevrinBlockLanguage, PlanDocument.BlockLanguage);
    }

    private const string Fence = "```";

    private const string Lf = "\n";

    // ---------- cookies ----------

    [Fact]
    public void FR326_Cookies_have_tevrin_names_and_keep_their_aide_names_as_legacy()
    {
        Assert.Equal(
            ["tevrin.auth", "tevrin.external", "tevrin.af"],
            [AuthConstants.AuthCookieName, AuthConstants.ExternalCookieName, AuthConstants.AntiforgeryCookieName]);
        Assert.Equal(
            ["aide.auth", "aide.external", "aide.af"],
            [AuthConstants.LegacyAuthCookieName, AuthConstants.LegacyExternalCookieName, AuthConstants.LegacyAntiforgeryCookieName]);
        Assert.Equal("tevrin:token_id", AuthConstants.TokenIdClaim);
    }

    [Fact]
    public void FR326_An_aide_auth_only_header_gets_a_tevrin_auth_copy()
    {
        var (header, moves) = LegacyCookies.Plan("XSRF-TOKEN=x; aide.auth=CfDJ8abc", LegacyCookies.Renames);

        Assert.Equal("XSRF-TOKEN=x; aide.auth=CfDJ8abc; tevrin.auth=CfDJ8abc", header);
        var move = Assert.Single(moves);
        Assert.Equal(LegacyCookieReissue.SignIn, move.Rename.Reissue);
        Assert.Equal(["aide.auth"], move.LegacyNames);
    }

    [Fact]
    public void FR326_Chunked_aide_auth_cookies_are_copied_chunk_by_chunk()
    {
        var (header, moves) = LegacyCookies.Plan("aide.auth=chunks-2; aide.authC1=part1; aide.authC2=part2", LegacyCookies.Renames);

        Assert.EndsWith("; tevrin.auth=chunks-2; tevrin.authC1=part1; tevrin.authC2=part2", header, StringComparison.Ordinal);
        Assert.Equal(["aide.auth", "aide.authC1", "aide.authC2"], Assert.Single(moves).LegacyNames);
    }

    [Theory]
    [InlineData("tevrin.auth=new; aide.auth=old")]
    [InlineData("tevrin.authC1=new; aide.auth=old")]
    [InlineData("other=1")]
    [InlineData("")]
    [InlineData("aide.authority=1; aide.authCx=2")]
    public void FR326_Nothing_moves_when_the_new_cookie_is_there_or_no_legacy_one_is(string header)
    {
        var (rewritten, moves) = LegacyCookies.Plan(header, LegacyCookies.Renames);

        Assert.Empty(moves);
        Assert.Equal(header, rewritten);
    }

    [Fact]
    public void FR326_The_antiforgery_and_flow_cookies_move_too()
    {
        var (header, moves) = LegacyCookies.Plan("aide.af=t; aide.external=e; aide.gitlab.oauth=s", LegacyCookies.Renames);

        Assert.Equal(
            [("tevrin.external", LegacyCookieReissue.SignIn), ("tevrin.af", LegacyCookieReissue.Copy), ("tevrin.gitlab.oauth", LegacyCookieReissue.RequestOnly)],
            moves.Select(m => (m.Rename.Current, m.Rename.Reissue)));
        Assert.Contains("tevrin.af=t", header, StringComparison.Ordinal);
    }

    // ---------- GitHub OAuth state items ----------

    [Theory]
    [InlineData("tevrin.mode")]
    [InlineData("aide.mode")]
    public void FR326_An_OAuth_state_item_is_read_under_either_name(string key)
    {
        var properties = new AuthenticationProperties();
        properties.Items[key] = GitHubAuth.ConnectMode;

        Assert.True(GitHubAuth.TryGetItem(properties, GitHubAuth.ModeItem, out var mode));
        Assert.Equal(GitHubAuth.ConnectMode, mode);
    }

    [Fact]
    public void FR326_OAuth_state_items_are_written_with_tevrin_names() =>
        Assert.Equal(
            ["tevrin.mode", "tevrin.link_user", "tevrin.github_app"],
            [GitHubAuth.ModeItem, GitHubAuth.LinkUserItem, GitHubAuth.AppClientItem]);

    // ---------- merge strategy (D-0.8.0-10) ----------

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new DomainEnumJsonConverter() } };

    private sealed record StrategyBody(MergeStrategy? MergeStrategy);

    [Fact]
    public void FR326_The_merge_strategy_is_written_tevrin_merges_everywhere()
    {
        Assert.Equal("tevrin_merges", MergeStrategy.TevrinMerges.ToWire());
        Assert.Equal(["tevrin_merges", "manual", "fast_forward", "squash"], EnumStrings.Values<MergeStrategy>());
        Assert.Equal("\"tevrin_merges\"", JsonSerializer.Serialize(MergeStrategy.TevrinMerges, ApiJson));
        Assert.Equal("""{"mergeStrategy":"tevrin_merges"}""", JsonSerializer.Serialize(new StrategyBody(MergeStrategy.TevrinMerges), ApiJson));
        Assert.Equal("\"tevrin_merges\"", TevrinJson.Serialize(MergeStrategy.TevrinMerges));
        Assert.Equal("tevrin_merges", new EnumWireConverter<MergeStrategy>().ConvertToProvider(MergeStrategy.TevrinMerges));
    }

    [Theory]
    [InlineData("tevrin_merges", MergeStrategy.TevrinMerges)]
    [InlineData("aide_merges", MergeStrategy.TevrinMerges)]
    [InlineData("manual", MergeStrategy.Manual)]
    [InlineData("fast_forward", MergeStrategy.FastForward)]
    [InlineData("squash", MergeStrategy.Squash)]
    public void FR326_The_merge_strategy_reads_tevrin_merges_and_the_aide_merges_alias(string wire, MergeStrategy expected)
    {
        var json = $"\"{wire}\"";

        Assert.Equal(expected, EnumStrings.Parse<MergeStrategy>(wire));
        Assert.Equal(expected, new EnumWireConverter<MergeStrategy>().ConvertFromProvider(wire));
        Assert.Equal(expected, JsonSerializer.Deserialize<MergeStrategy>(json, ApiJson));
        Assert.Equal(expected, JsonSerializer.Deserialize<StrategyBody>($$"""{"mergeStrategy":{{json}}}""", ApiJson)!.MergeStrategy);
        Assert.Equal(expected, TevrinJson.Deserialize<MergeStrategy>(json));
    }

    [Theory]
    [InlineData("aide")]
    [InlineData("merges")]
    [InlineData("aide_merge")]
    [InlineData("")]
    public void FR326_An_unknown_merge_strategy_is_still_rejected(string wire)
    {
        var json = $"\"{wire}\"";

        Assert.False(EnumStrings.TryParse<MergeStrategy>(wire, out _));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MergeStrategy>(json, ApiJson));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StrategyBody>($$"""{"mergeStrategy":{{json}}}""", ApiJson));
        Assert.Throws<JsonException>(() => TevrinJson.Deserialize<MergeStrategy>(json));
    }

    [Fact]
    public void FR326_Only_renamed_enum_members_have_aliases()
    {
        Assert.Equal(KeyValuePair.Create("aide_merges", MergeStrategy.TevrinMerges), Assert.Single(EnumStrings.Aliases<MergeStrategy>()));
        Assert.Empty(EnumStrings.Aliases<GitProvider>());
    }

    // ---------- system actor and Netlify titles ----------

    [Fact]
    public void FR326_The_system_actor_is_recognised_under_both_names()
    {
        Assert.True(SystemActor.IsSystemName("tevrin-system"));
        Assert.True(SystemActor.IsSystemName("aide-system"));
        Assert.True(SystemActor.IsSystemName(SystemActor.Name));
        Assert.False(SystemActor.IsSystemName("someone"));
        Assert.False(SystemActor.IsSystemName(null));
    }

    [Fact]
    public void FR326_Netlify_builds_are_titled_tevrin_and_found_under_either_title()
    {
        var id = Guid.Parse("0194d6a2-0000-7000-8000-000000000001");
        Assert.Equal(["tevrin:" + id.ToString("D"), "aide:" + id.ToString("D")], NetlifyRules.BuildTitles(id));
        Assert.Equal("tevrin:hook:abc1234", NetlifyRules.HookDeployTitle("abc1234"));
        Assert.Equal(["tevrin:hook:abc1234", "aide:hook:abc1234"], NetlifyRules.HookDeployTitles("abc1234"));
        Assert.Equal("tevrin previews ", NetlifyRules.HookTitlePrefix);
        Assert.Equal("aide previews ", NetlifyRules.LegacyHookTitlePrefix);
    }
}
