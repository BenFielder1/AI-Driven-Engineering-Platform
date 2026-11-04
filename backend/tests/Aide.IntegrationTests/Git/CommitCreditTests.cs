using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Auth;
using Aide.Api.Features.Git;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Tokens;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Git;

/// <summary>FR-174–FR-177: the settings, reading who a commit credits, and redelivery with and without the trailer.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommitCreditTests(AideApiFactory factory)
{
    private const string Branch = "aide/credit-test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object GitHubPat => new { provider = "github_pat", repoUrl = "https://github.com/acme/widgets", token = "ghp_commitCreditToken0123456789" };

    // ---------- PATCH /api/auth/me

    [Fact]
    public async Task FR177_A_user_can_opt_out_and_back_in()
    {
        using var session = await ApiSession.RegisterAsync(factory);

        var off = await ProjectApi.ReadAsync<UserDto>(await ProjectApi.PatchAsync(session.Client, "/api/auth/me", new { commitCreditOptOut = true }));
        var me = await session.Client.GetFromJsonAsync<UserDto>("/api/auth/me", ApiSession.Json, Ct);
        var on = await ProjectApi.ReadAsync<UserDto>(await ProjectApi.PatchAsync(session.Client, "/api/auth/me", new { commitCreditOptOut = false }));

        Assert.True(off.CommitCreditOptOut);
        Assert.True(me!.CommitCreditOptOut);
        Assert.False(on.CommitCreditOptOut);
    }

    [Fact]
    public async Task FR177_The_opt_out_is_required_400_VALIDATION()
    {
        using var session = await ApiSession.RegisterAsync(factory);

        var response = await ProjectApi.PatchAsync(session.Client, "/api/auth/me", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FR177_Anonymous_is_401()
    {
        using var session = new ApiSession(factory);

        var response = await ProjectApi.PatchAsync(session.Client, "/api/auth/me", new { commitCreditOptOut = true });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FR177_An_API_token_can_not_change_it_403()
    {
        using var session = await ApiSession.RegisterAsync(factory);
        var created = await session.Client.PostAsJsonAsync("/api/me/tokens", new { name = "cli" }, Ct);
        var token = await ProjectApi.ReadAsync<CreatedApiTokenDto>(created, HttpStatusCode.Created);
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await ProjectApi.PatchAsync(cli, "/api/auth/me", new { commitCreditOptOut = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- PATCH settings/git

    [Fact]
    public async Task FR177_The_Owner_turns_credit_off_with_an_event_that_holds_no_email()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var before = await ProjectApi.ReadAsync<ProjectSettingsDto>(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct));

        var off = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false }));
        await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false });

        Assert.True(before.Git.CommitCreditEnabled);
        Assert.False(off.CommitCreditEnabled);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var stored = await db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == fixture.Project.Id, Ct);
            Assert.False(stored.CommitCreditEnabled);

            // The same value again changes nothing, so there is one event.
            var changed = await db.Events.AsNoTracking().SingleAsync(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.CommitCreditChanged, Ct);
            Assert.DoesNotContain("@", changed.Payload, StringComparison.Ordinal);
            Assert.Contains("false", changed.Payload, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FR177_Only_an_Owner_changes_the_project_setting()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var viewer = await ProjectApi.PatchAsync(fixture.Viewer.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false });
        var contributor = await ProjectApi.PatchAsync(fixture.Contributor.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false });
        var outsider = await ProjectApi.PatchAsync(fixture.Outsider.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false });

        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, contributor.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    // ---------- Resolution against the database

    [Fact]
    public async Task FR176_A_GitHub_step_commit_uses_the_noreply_address_and_never_the_sign_in_email()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: GitHubPat);
        var owner = fixture.Owner.User!;
        await ConnectGitHubAsync(owner.Id, 917400, "owner-login");

        var line = await ForStepAsync(fixture.Project.Id);

        Assert.Equal($"Co-authored-by: {owner.Name} <917400+owner-login@users.noreply.github.com>", line);
        Assert.DoesNotContain(owner.Email, line!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FR177_An_opted_out_user_or_the_project_setting_off_gives_no_trailer()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: GitHubPat);
        await ConnectGitHubAsync(fixture.Owner.User!.Id, 917401, "owner-optout");
        Assert.NotNull(await ForStepAsync(fixture.Project.Id));

        await ProjectApi.PatchAsync(fixture.Owner.Client, "/api/auth/me", new { commitCreditOptOut = true });
        Assert.Null(await ForStepAsync(fixture.Project.Id));

        await ProjectApi.PatchAsync(fixture.Owner.Client, "/api/auth/me", new { commitCreditOptOut = false });
        await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false });
        Assert.Null(await ForStepAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task FR175_The_plan_approver_comes_before_the_person_who_advanced()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: GitHubPat);
        await ConnectGitHubAsync(fixture.Owner.User!.Id, 917402, "owner-order");
        await ConnectGitHubAsync(fixture.Contributor.User!.Id, 917403, "approver-order");
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var versionId = await db.ReleaseVersions.Where(v => v.ProjectId == fixture.Project.Id).Select(v => v.Id).SingleAsync(Ct);
            db.ApprovalGates.Add(new ApprovalGate
            {
                ProjectId = fixture.Project.Id,
                ReleaseVersionId = versionId,
                Type = GateType.PlanApproval,
                Status = GateStatus.Approved,
                ReviewerId = fixture.Contributor.User!.Id,
                DecidedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.StateTransitions.Add(new StateTransition
            {
                ProjectId = fixture.Project.Id,
                ReleaseVersionId = versionId,
                ToPhase = Phase.Execution,
                Actor = fixture.Owner.User!.Id.ToString(),
                Reason = "test",
                Checkpoint = 2,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Contains("917403+approver-order@", await ForStepAsync(fixture.Project.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR174_A_Local_project_has_no_trailer()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await ConnectGitHubAsync(fixture.Owner.User!.Id, 917404, "owner-local");

        Assert.Null(await ForStepAsync(fixture.Project.Id));
    }

    [Fact]
    public async Task FR176_A_Bitbucket_person_is_credited_with_the_stored_email_and_skipped_without_one()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        using var session = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, session.User!.Id, login: "ana-bb");
        await using var scope = app.Services.CreateAsyncScope();
        var credit = scope.ServiceProvider.GetRequiredService<CommitCredit>();

        Assert.Null(await credit.ForBitbucketInitialCommitAsync(session.User!.Id, connection, Ct));

        var encrypted = app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.GitHostCommitEmail).Protect("ana@bitbucket.example");
        await ProjectApi.WithDbAsync(app, db => db.GitConnections.Where(c => c.Id == connection).ExecuteUpdateAsync(s => s.SetProperty(c => c.CommitEmailEncrypted, encrypted), Ct));

        var line = await credit.ForBitbucketInitialCommitAsync(session.User!.Id, connection, Ct);
        Assert.Equal($"Co-authored-by: {session.User!.Name} <ana@bitbucket.example>", line);
        Assert.DoesNotContain(session.User!.Email, line!, StringComparison.OrdinalIgnoreCase);

        await ProjectApi.PatchAsync(session.Client, "/api/auth/me", new { commitCreditOptOut = true });
        Assert.Null(await credit.ForBitbucketInitialCommitAsync(session.User!.Id, connection, Ct));
    }

    [Fact]
    public async Task FR176_A_commit_email_that_can_not_be_read_skips_the_person_and_never_fails()
    {
        var app = BitbucketHarness.App(factory, new FakeGitHubApi());
        using var session = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, session.User!.Id);
        await ProjectApi.WithDbAsync(app, db => db.GitConnections.Where(c => c.Id == connection).ExecuteUpdateAsync(s => s.SetProperty(c => c.CommitEmailEncrypted, "not-ciphertext"), Ct));
        await using var scope = app.Services.CreateAsyncScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<CommitCredit>().ForBitbucketInitialCommitAsync(session.User!.Id, connection, Ct));
    }

    // ---------- Release merge and tag: who is credited

    [Fact]
    public async Task FR175_A_release_credits_the_pre_release_approver_first()
    {
        using var fixture = await ReleaseFixtureAsync(917420);
        var now = DateTimeOffset.UtcNow;
        await SeedReleaseGateAsync(fixture, fixture.Contributor, GateStatus.Approved);
        await SeedReleaseStartedAsync(fixture, fixture.Viewer.User!.Id.ToString(), now);

        Assert.Contains("917421+contributor@", await ForReleaseAsync(fixture, now), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR175_A_release_without_an_approver_credits_who_started_it_and_a_rejection_is_no_approval()
    {
        using var fixture = await ReleaseFixtureAsync(917430);
        var now = DateTimeOffset.UtcNow;
        await SeedReleaseGateAsync(fixture, fixture.Contributor, GateStatus.Rejected);
        await SeedReleaseStartedAsync(fixture, fixture.Viewer.User!.Id.ToString(), now);

        Assert.Contains("917432+viewer@", await ForReleaseAsync(fixture, now), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR175_A_release_reads_its_own_attempt_and_a_system_starter_falls_to_the_binding_Owner_then_the_creator()
    {
        using var fixture = await ReleaseFixtureAsync(917440);
        var now = DateTimeOffset.UtcNow;

        // An earlier attempt started by the viewer doesn't count for this one, which the system started.
        await SeedReleaseStartedAsync(fixture, fixture.Viewer.User!.Id.ToString(), now.AddHours(-1));
        await SeedReleaseStartedAsync(fixture, Aide.Domain.Access.SystemActor.Name, now);
        Assert.Contains("917440+owner@", await ForReleaseAsync(fixture, now), StringComparison.Ordinal);

        await ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.Where(s => s.ProjectId == fixture.Project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.BoundByUserId, fixture.Contributor.User!.Id), Ct));
        Assert.Contains("917441+contributor@", await ForReleaseAsync(fixture, now), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR175_A_release_with_credit_off_has_no_trailer()
    {
        using var fixture = await ReleaseFixtureAsync(917450);
        var now = DateTimeOffset.UtcNow;
        await SeedReleaseGateAsync(fixture, fixture.Contributor, GateStatus.Approved);
        await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { commitCreditEnabled = false });

        Assert.Null(await ForReleaseAsync(fixture, now));
    }

    // ---------- The Bitbucket commit email: connect, re-test, refresh

    [Fact]
    public async Task FR176_Re_testing_an_API_token_fills_the_commit_email_from_the_token_email()
    {
        var app = BitbucketHarness.App(factory, BitbucketHarness.TokenBitbucket(BitbucketHarness.NewUuid()));
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, "bb-token", authKind: GitConnectionAuthKind.Token, email: "ana@bitbucket.example");

        await user.Client.PostAsync(new Uri($"/api/me/connections/bitbucket/{connection}/test", UriKind.Relative), null, Ct);

        Assert.Equal("ana@bitbucket.example", await CommitEmailAsync(app, connection));
    }

    [Fact]
    public async Task FR176_Re_testing_OAuth_fills_the_commit_email_with_the_primary_confirmed_address_only()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Get, "/2.0/user", HttpStatusCode.OK, BitbucketHarness.UserJson(BitbucketHarness.NewUuid(), "ana"))
            .On(HttpMethod.Get, "/2.0/user/emails", HttpStatusCode.OK, """{"values":[{"email":"other@x.example","is_primary":false,"is_confirmed":true},{"email":"primary@x.example","is_primary":true,"is_confirmed":true}]}""");
        var app = BitbucketHarness.App(factory, bitbucket);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id);

        await user.Client.PostAsync(new Uri($"/api/me/connections/bitbucket/{connection}/test", UriKind.Relative), null, Ct);

        Assert.Equal("primary@x.example", await CommitEmailAsync(app, connection));
    }

    [Fact]
    public async Task FR176_A_token_refresh_backfills_the_commit_email()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Post, "/site/oauth2/access_token", HttpStatusCode.OK, """{"access_token":"bb-access-2","expires_in":7200,"refresh_token":"bb-refresh-2","scopes":"account email"}""")
            .On(HttpMethod.Get, "/2.0/user/emails", HttpStatusCode.OK, """{"values":[{"email":"primary@x.example","is_primary":true,"is_confirmed":true}]}""")
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK, BitbucketHarness.WorkspacesJson("acme"))
            .On(HttpMethod.Get, "/2.0/repositories/acme", HttpStatusCode.OK, BitbucketHarness.ReposJson(("acme", "todo")));
        var app = BitbucketHarness.App(factory, bitbucket, configuredConsumer: true);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, "bb-access-1", "bb-refresh-1", DateTimeOffset.UtcNow.AddMinutes(1));

        await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}", UriKind.Relative), Ct);

        Assert.Equal("primary@x.example", await CommitEmailAsync(app, connection));
    }

    [Fact]
    public async Task FR176_A_refused_email_read_never_fails_the_refresh_or_the_call_behind_it()
    {
        var bitbucket = new FakeGitHubApi()
            .On(HttpMethod.Post, "/site/oauth2/access_token", HttpStatusCode.OK, """{"access_token":"bb-access-2","expires_in":7200,"refresh_token":"bb-refresh-2","scopes":"account"}""")
            .On(HttpMethod.Get, "/2.0/user/emails", HttpStatusCode.Forbidden, BitbucketHarness.ScopeError)
            .On(HttpMethod.Get, "/2.0/user/workspaces", HttpStatusCode.OK, BitbucketHarness.WorkspacesJson("acme"))
            .On(HttpMethod.Get, "/2.0/repositories/acme", HttpStatusCode.OK, BitbucketHarness.ReposJson(("acme", "todo")));
        var app = BitbucketHarness.App(factory, bitbucket, configuredConsumer: true);
        using var user = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, user.User!.Id, "bb-access-1", "bb-refresh-1", DateTimeOffset.UtcNow.AddMinutes(1));

        var response = await user.Client.GetAsync(new Uri($"/api/me/git/bitbucket/repos?connectionId={connection}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await CommitEmailAsync(app, connection));
    }

    // ---------- Redelivery (D17)

    [Fact]
    public async Task FR174_A_commit_with_the_trailer_is_still_found_by_its_step_and_keeps_the_trailer_block()
    {
        var workingCopies = factory.Services.GetRequiredService<IWorkingCopies>();
        var id = Guid.CreateVersion7();
        var step = Guid.CreateVersion7();
        const string Line = "Co-authored-by: Ana Lee <42+ana@users.noreply.github.com>";
        await workingCopies.InitAsync(id, "main", Ct);
        await workingCopies.CheckoutBranchAsync(id, "main", Branch, Ct);
        var message = CodeGeneration.CommitMessage(3, "Credit", ["a.txt"], step, Line);

        var first = await workingCopies.CommitStepAsync(id, Branch, step, [new ProducedFile("a.txt", "a")], message, Ct);
        var found = await workingCopies.FindStepCommitAsync(id, Branch, step, Ct);
        var again = await workingCopies.CommitStepAsync(id, Branch, step, [new ProducedFile("a.txt", "b")], message, Ct);

        Assert.Equal(first.Sha, found?.Sha);
        Assert.True(again.AlreadyCommitted);
        Assert.Equal(first.Sha, again.Sha);
        var trailers = await GitAsync(id, "log", "-1", "--format=%(trailers:only,unfold)", first.Sha);
        Assert.Equal($"{CodeGeneration.StepTrailerLine(step)}\n{Line}", trailers.Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task FR174_A_redelivered_step_whose_commit_has_no_trailer_makes_no_second_commit()
    {
        var workingCopies = factory.Services.GetRequiredService<IWorkingCopies>();
        var id = Guid.CreateVersion7();
        var step = Guid.CreateVersion7();
        await workingCopies.InitAsync(id, "main", Ct);
        await workingCopies.CheckoutBranchAsync(id, "main", Branch, Ct);
        var first = await workingCopies.CommitStepAsync(
            id, Branch, step, [new ProducedFile("a.txt", "a")], CodeGeneration.CommitMessage(3, "Credit", ["a.txt"], step), Ct);

        var redelivered = await workingCopies.CommitStepAsync(
            id, Branch, step, [new ProducedFile("a.txt", "a")], CodeGeneration.CommitMessage(3, "Credit", ["a.txt"], step, "Co-authored-by: A <1+a@users.noreply.github.com>"), Ct);

        Assert.True(redelivered.AlreadyCommitted);
        Assert.Equal(first.Sha, redelivered.Sha);
    }

    private static async Task<string?> CommitEmailAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, Guid connection)
    {
        var encrypted = await ProjectApi.WithDbAsync(app, db => db.GitConnections.AsNoTracking().Where(c => c.Id == connection).Select(c => c.CommitEmailEncrypted).SingleAsync(Ct));
        return encrypted is null ? null : BitbucketHarness.Decrypt(app, encrypted, SecretPurposes.GitHostCommitEmail);
    }

    /// <summary>A GitHub project whose Owner, Contributor and Viewer are connected as ids <paramref name="firstId"/>, +1 and +2.</summary>
    private async Task<ProjectFixture> ReleaseFixtureAsync(long firstId)
    {
        var fixture = await ProjectApi.FixtureAsync(factory, git: GitHubPat);
        await ConnectGitHubAsync(fixture.Owner.User!.Id, firstId, "owner");
        await ConnectGitHubAsync(fixture.Contributor.User!.Id, firstId + 1, "contributor");
        await ConnectGitHubAsync(fixture.Viewer.User!.Id, firstId + 2, "viewer");
        return fixture;
    }

    private Task<Guid> VersionIdAsync(ProjectFixture fixture) =>
        ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.Where(v => v.ProjectId == fixture.Project.Id).Select(v => v.Id).SingleAsync(Ct));

    private async Task SeedReleaseGateAsync(ProjectFixture fixture, ApiSession reviewer, GateStatus status)
    {
        var versionId = await VersionIdAsync(fixture);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            db.ApprovalGates.Add(new ApprovalGate
            {
                ProjectId = fixture.Project.Id,
                ReleaseVersionId = versionId,
                Type = GateType.PreRelease,
                Status = status,
                ReviewerId = reviewer.User!.Id,
                DecidedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });
    }

    private Task SeedReleaseStartedAsync(ProjectFixture fixture, string actor, DateTimeOffset at) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            db.AddEvent(EventTypes.ReleaseStarted, fixture.Project.Id, new { attempt = 1 }, actor, at);
            await db.SaveChangesAsync(Ct);
        });

    private async Task<string?> ForReleaseAsync(ProjectFixture fixture, DateTimeOffset createdAt)
    {
        var release = new Aide.Domain.Entities.Release { ProjectId = fixture.Project.Id, ReleaseVersionId = await VersionIdAsync(fixture), Tag = "v0.1.0", CreatedAt = createdAt };
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommitCredit>().ForReleaseAsync(release, Ct);
    }

    private async Task<string?> ForStepAsync(Guid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommitCredit>().ForStepAsync(projectId, Guid.CreateVersion7(), Ct);
    }

    private Task ConnectGitHubAsync(Guid userId, long githubUserId, string login) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            db.GitHubConnections.Add(new GitHubConnection
            {
                UserId = userId,
                GithubUserId = githubUserId,
                Login = login,
                AccessTokenEncrypted = "unused",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        });

    private async Task<string> GitAsync(Guid projectId, params string[] arguments)
    {
        var result = await factory.Services.GetRequiredService<IGitCli>()
            .RunAsync(factory.Services.GetRequiredService<IWorkingCopies>().PathFor(projectId), arguments, Ct);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput;
    }
}
