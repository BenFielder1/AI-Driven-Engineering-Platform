using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;
using Aide.Infrastructure.Git;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-174–FR-176, §4.25: who a commit credits and how the trailer reads, with the spec's worked examples.</summary>
public sealed class CommitAttributionTests
{
    private static readonly CommitHost GitHub = new(CommitHostKind.GitHub);
    private static readonly Guid Ana = Guid.Parse("0199a1b2-0000-7000-8000-00000000000a");
    private static readonly Guid Ben = Guid.Parse("0199a1b2-0000-7000-8000-00000000000b");
    private static readonly Guid Cy = Guid.Parse("0199a1b2-0000-7000-8000-00000000000c");
    private static readonly Guid Gone = Guid.Parse("0199a1b2-0000-7000-8000-00000000000d");

    private static CommitPerson OnGitHub(string name, long id, string login, bool optedOut = false) =>
        new(name, optedOut, new CommitIdentity(login, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static Dictionary<Guid, CommitPerson> People(params (Guid Id, CommitPerson Person)[] people) =>
        people.ToDictionary(p => p.Id, p => p.Person);

    private static CreditedPerson? Resolve(IReadOnlyList<Guid?> candidates, Dictionary<Guid, CommitPerson> people, bool enabled = true, CommitHost? host = null) =>
        CommitAttribution.Resolve(new CommitCreditContext(enabled, host ?? GitHub, candidates, people));

    // ---------- §4.25 worked examples

    [Fact]
    public void FR175_Example_1_Strict_the_plan_approver_is_credited_with_their_noreply_address()
    {
        var credited = Resolve(
            CommitAttribution.StepCandidates(Ana, Ben.ToString(), Cy, Ben, Ben.ToString()),
            People((Ana, OnGitHub("Ana Lee", 42, "ana")), (Ben, OnGitHub("Ben", 7, "ben"))));

        Assert.Equal("Co-authored-by: Ana Lee <42+ana@users.noreply.github.com>", credited?.CoAuthorLine);
    }

    [Fact]
    public void FR175_Example_2_Advisory_has_no_plan_gate_so_whoever_advanced_is_credited()
    {
        var credited = Resolve(
            CommitAttribution.StepCandidates(null, Ben.ToString(), Cy, null, null),
            People((Ben, OnGitHub("Ben", 7, "ben")), (Cy, OnGitHub("Cy", 8, "cy"))));

        Assert.Equal(Ben, credited?.UserId);
    }

    [Fact]
    public void FR175_Example_3_Autonomous_a_system_actor_never_counts_so_the_version_starter_is_credited()
    {
        var credited = Resolve(
            CommitAttribution.StepCandidates(null, SystemActor.Name, Cy, null, null),
            People((Cy, OnGitHub("Cy", 8, "cy"))));

        Assert.Equal(Cy, credited?.UserId);
    }

    [Fact]
    public void FR176_Example_4_A_person_with_no_connection_on_the_host_is_skipped()
    {
        var credited = Resolve(
            CommitAttribution.StepCandidates(Ana, null, null, Ben, null),
            People((Ana, new CommitPerson("Ana Lee", false, null)), (Ben, OnGitHub("Ben", 7, "ben"))));

        Assert.Equal(Ben, credited?.UserId);
    }

    [Fact]
    public void FR174_Example_5_A_name_can_not_inject_a_trailer()
    {
        var line = CommitTrailers.CoAuthor("Eve>\nCo-authored-by: X <x@y>", new CommitIdentity("eve", "5"), GitHub);

        Assert.Equal("Co-authored-by: Eve Co-authored-by: X x@y <5+eve@users.noreply.github.com>", line);
        Assert.DoesNotContain('\n', line!);
    }

    [Fact]
    public void FR176_Example_6_GitLab_uses_the_instance_host()
    {
        var host = CommitHost.From(GitProvider.Gitlab, "https://gitlab.acme.io");

        Assert.Equal("7-sam@users.noreply.gitlab.acme.io", CommitTrailers.Address(new CommitIdentity("sam", "7"), host));
    }

    [Fact]
    public void FR174_Example_7_Local_Git_has_no_trailer()
    {
        var credited = Resolve([Ana], People((Ana, OnGitHub("Ana Lee", 42, "ana"))), host: CommitHost.From(GitProvider.Local));

        Assert.Null(credited);
    }

    // ---------- Resolution

    [Fact]
    public void FR177_Project_setting_off_means_no_trailer() =>
        Assert.Null(Resolve([Ana], People((Ana, OnGitHub("Ana Lee", 42, "ana"))), enabled: false));

    [Fact]
    public void FR177_An_opted_out_person_is_skipped_and_the_next_is_credited()
    {
        var credited = Resolve([Ana, Ben], People((Ana, OnGitHub("Ana Lee", 42, "ana", optedOut: true)), (Ben, OnGitHub("Ben", 7, "ben"))));

        Assert.Equal(Ben, credited?.UserId);
    }

    [Fact]
    public void FR175_A_deleted_user_is_skipped() =>
        Assert.Equal(Ben, Resolve([Gone, Ben], People((Ben, OnGitHub("Ben", 7, "ben"))))?.UserId);

    [Fact]
    public void FR174_No_eligible_candidate_means_no_trailer() =>
        Assert.Null(Resolve([null, Gone], People()));

    [Fact]
    public void FR175_Release_candidates_are_in_the_spec_order()
    {
        var candidates = CommitAttribution.ReleaseCandidates(Ana, Ben.ToString(), Cy, Gone.ToString());

        Assert.Equal<Guid?>([Ana, Ben, Cy, Gone], candidates);
    }

    [Fact]
    public void FR175_Step_candidates_are_in_the_spec_order()
    {
        var candidates = CommitAttribution.StepCandidates(Ana, Ben.ToString(), Cy, Gone, Ana.ToString());

        Assert.Equal<Guid?>([Ana, Ben, Cy, Gone, Ana], candidates);
    }

    [Fact]
    public void FR175_Initial_commit_credits_the_creator() =>
        Assert.Equal<Guid?>([Ana], CommitAttribution.InitialCandidates(Ana));

    // ---------- Addresses and names

    [Fact]
    public void FR176_Bitbucket_uses_the_stored_account_email_and_nothing_without_one()
    {
        var host = CommitHost.From(GitProvider.Bitbucket);

        Assert.Equal("Co-authored-by: Ana <ana@bitbucket.example>", CommitTrailers.CoAuthor("Ana", new CommitIdentity("ana", "{uuid}", "ana@bitbucket.example"), host));
        Assert.Null(CommitTrailers.CoAuthor("Ana", new CommitIdentity("ana", "{uuid}"), host));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b>\nCo-authored-by: X <x@y>")]
    [InlineData("a b@c")]
    public void FR174_A_malformed_Bitbucket_email_skips_the_person_rather_than_breaking_the_message(string email) =>
        Assert.Null(CommitTrailers.Address(new CommitIdentity("ana", null, email), CommitHost.From(GitProvider.Bitbucket)));

    [Theory]
    [InlineData("ana\nx", "42")]
    [InlineData("ana@x", "42")]
    [InlineData("ana", "not-a-number")]
    [InlineData("ana", null)]
    public void FR176_An_unusable_GitHub_identity_gives_no_address(string login, string? id) =>
        Assert.Null(CommitTrailers.Address(new CommitIdentity(login, id), GitHub));

    [Fact]
    public void FR176_GitLab_com_is_used_when_the_instance_has_no_base_url() =>
        Assert.Equal("gitlab.com", CommitHost.From(GitProvider.Gitlab).InstanceHost);

    [Theory]
    [InlineData(GitProvider.GithubApp, CommitHostKind.GitHub)]
    [InlineData(GitProvider.GithubPat, CommitHostKind.GitHub)]
    [InlineData(GitProvider.Gitlab, CommitHostKind.GitLab)]
    [InlineData(GitProvider.Bitbucket, CommitHostKind.Bitbucket)]
    [InlineData(GitProvider.Local, CommitHostKind.Local)]
    public void FR176_Every_provider_maps_to_a_host(GitProvider provider, CommitHostKind kind) =>
        Assert.Equal(kind, CommitHost.From(provider).Kind);

    [Fact]
    public void FR174_An_empty_name_falls_back_to_the_login() =>
        Assert.Equal("ana", CommitTrailers.CleanName(" <>\r\n ", "ana"));

    [Fact]
    public void FR174_A_long_name_is_cut_to_100_characters() =>
        Assert.Equal(100, CommitTrailers.CleanName(new string('a', 300), "ana").Length);

    [Fact]
    public void FR176_The_commit_email_is_only_the_primary_confirmed_address()
    {
        Assert.Equal("p@x.io", BitbucketRules.CommitEmail([new("o@x.io", false, true), new("p@x.io", true, true)]));
        Assert.Null(BitbucketRules.CommitEmail([new("o@x.io", false, true), new("p@x.io", true, false)]));
    }

    [Fact]
    public void FR176_An_identity_never_shows_its_email_in_a_log() =>
        Assert.DoesNotContain("secret@x.io", new CommitIdentity("ana", null, "secret@x.io").ToString(), StringComparison.Ordinal);

    // ---------- Messages

    [Fact]
    public void FR174_The_step_commit_matches_the_worked_message_format()
    {
        var stepId = Guid.Parse("0192aaaa-0000-7000-8000-000000000001");

        var message = CodeGeneration.CommitMessage(
            3, "Add the projects endpoint", ["backend/src/Api/Projects.cs"], stepId, "Co-authored-by: Ana Lee <42+ana@users.noreply.github.com>");

        Assert.Equal("[AIDE][Phase:execution][Step:3] Add the projects endpoint", message.Subject);
        Assert.Equal(
            $"Files:\n- backend/src/Api/Projects.cs\n\nAIDE-Step-Id: {stepId:D}\nCo-authored-by: Ana Lee <42+ana@users.noreply.github.com>",
            message.Body);
    }

    [Fact]
    public void FR174_With_no_credited_person_the_step_message_is_unchanged()
    {
        var stepId = Guid.Parse("0192aaaa-0000-7000-8000-000000000001");

        Assert.Equal(CodeGeneration.CommitMessage(3, "T", ["a.txt"], stepId), CodeGeneration.CommitMessage(3, "T", ["a.txt"], stepId, null));
        Assert.Equal($"Files:\n- a.txt\n\nAIDE-Step-Id: {stepId:D}", CodeGeneration.CommitMessage(3, "T", ["a.txt"], stepId).Body);
    }

    [Fact]
    public void FR174_A_merge_and_tag_get_the_trailer_as_their_last_paragraph()
    {
        const string Line = "Co-authored-by: Ana <42+ana@users.noreply.github.com>";
        var request = new ReleaseRequest("aide/x", "v1.2.0", "1.2.0", "Todo API", null) { CoAuthorLine = Line };

        Assert.Equal($"{ReleaseRules.MergeTitle("Todo API", "v1.2.0")}\n\n{Line}", request.MergeMessage);
        Assert.Equal($"{ReleaseRules.TagMessage("Todo API", "v1.2.0", "1.2.0")}\n\n{Line}", request.TagMessage);
    }

    [Fact]
    public void FR174_A_merge_and_tag_without_a_credited_person_are_unchanged()
    {
        var request = new ReleaseRequest("aide/x", "v1.2.0", "1.2.0", "Todo API", null);

        Assert.Equal(ReleaseRules.MergeTitle("Todo API", "v1.2.0"), request.MergeMessage);
        Assert.Equal(ReleaseRules.TagMessage("Todo API", "v1.2.0", "1.2.0"), request.TagMessage);
    }
}
