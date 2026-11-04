using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Connections;
using Aide.Api.Features.Trackers;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Security;
using Aide.Infrastructure.Trackers;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Trackers;

/// <summary>
/// FR-165–FR-173 end to end on the Fake tracker (no test reaches a real one): connections, browsing, imports as snapshots, entry points,
/// agents reading the snapshot, re-import, remove and drift, with success, 400, 401, 403, 404, each error code and 409.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class TrackerImportTests(AideApiFactory factory)
{
    private const string Connections = "/api/me/connections/platforms";

    private static readonly string[] OneKey = ["FAKE-1"];
    private static readonly string[] NotAKey = ["not a key"];
    private static readonly string[] DoneOnly = ["done"];

    private readonly WebApplicationFactory<Program> _app = factory.WithWebHostBuilder(b => b.UseSetting("Trackers:Provider", "Fake"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private static async Task<PlatformConnectionDto> JiraAsync(ApiSession user, string token = "atlassian_token_1234") =>
        await ProjectApi.ReadAsync<PlatformConnectionDto>(
            await user.Client.PostAsJsonAsync(Connections, new { provider = "jira", label = "Acme", token, site = "acme.atlassian.net", email = "ana@example.com" }, Ct),
            HttpStatusCode.Created);

    private static object Keys(Guid? connectionId, params string[] keys) =>
        new { provider = "jira", connectionId, selection = new { keys }, options = new { includeChildren = false } };

    private static string Sources(Guid projectId) => $"/api/projects/{projectId}/sources";

    private static async Task<SourceImportDto> ImportAsync(ApiSession user, Guid projectId, object body) =>
        await ProjectApi.ReadAsync<SourceImportDto>(await user.Client.PostAsJsonAsync(Sources(projectId), body, Ct), HttpStatusCode.Accepted);

    private static string[] FakeKeys(int count) => [.. Enumerable.Range(1, count).Select(n => $"FAKE-{n}")];

    private static async Task<ProjectSourcesDto> SourcesAsync(ApiSession user, Guid projectId) =>
        await ProjectApi.ReadAsync<ProjectSourcesDto>(await user.Client.GetAsync(U(Sources(projectId)), Ct));

    private Task<List<Artifact>> SnapshotsAsync(Guid projectId) => AgentJobs.ArtifactsAsync(_app, projectId, ArtifactType.SourceItems);

    // ---------------------------------------------------------------- FR-165, FR-166 connections

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("evil.example.com")]
    public async Task FR165_Given_a_bad_site_then_TRACKER_SITE_NOT_ALLOWED_and_nothing_is_saved(string site)
    {
        using var user = await ApiSession.RegisterAsync(_app);

        var response = await user.Client.PostAsJsonAsync(Connections, new { provider = "jira", label = "x", token = "tok_1234", site, email = "a@b.co" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.TrackerSiteNotAllowed);
        Assert.Empty(await ProjectApi.ReadAsync<List<PlatformConnectionDto>>(await user.Client.GetAsync(U(Connections), Ct)));
    }

    [Fact]
    public async Task FR165_Given_a_valid_scoped_token_then_the_connection_shows_the_site_account_read_scope_and_valid_and_the_secret_is_encrypted()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        var expires = DateTimeOffset.UtcNow.AddDays(5);

        var response = await user.Client.PostAsJsonAsync(
            Connections,
            new { provider = "jira", label = "Acme", token = "atlassian_secret_9999", site = "acme.atlassian.net", email = "ana@example.com", expiresAt = expires, acceptanceCriteriaField = "customfield_10042" },
            Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var dto = JsonSerializer.Deserialize<PlatformConnectionDto>(text, ApiSession.Json)!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(("acme.atlassian.net", "fake-cloud", "customfield_10042"), (dto.Metadata.Site, dto.Metadata.CloudId, dto.Metadata.AcceptanceCriteriaField));
        Assert.Equal(("Fake Account", PlatformConnectionStatus.Valid, "read"), (dto.AccountName, dto.Status, dto.Scopes[0]));
        Assert.True(dto.ExpiryWarning);
        Assert.NotNull(dto.ExpiresAt);
        Assert.DoesNotContain("atlassian_secret_9999", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@example.com", text, StringComparison.Ordinal);

        var row = await ProjectApi.WithDbAsync(_app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == dto.Id, Ct));
        Assert.DoesNotContain("atlassian_secret_9999", row.SecretEncrypted, StringComparison.Ordinal);
        var plain = _app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.TrackerToken).Unprotect(row.SecretEncrypted);
        Assert.Equal(("ana@example.com", "atlassian_secret_9999"), TrackerSecrets.Decode(PlatformProvider.Jira, plain));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains("atlassian_secret_9999", StringComparison.Ordinal));

        var fields = await ProjectApi.ReadAsync<List<PlatformResourceDto>>(await user.Client.GetAsync(U($"{Connections}/{dto.Id}/resources?type=fields"), Ct));
        Assert.Equal("customfield_10042", Assert.Single(fields).Id);

        var retested = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsync(U($"{Connections}/{dto.Id}/test"), null, Ct));
        Assert.NotNull(retested.ExpiresAt);
        Assert.Equal("customfield_10042", retested.Metadata.AcceptanceCriteriaField);

        var patched = await ProjectApi.ReadAsync<PlatformConnectionDto>(await ProjectApi.PatchAsync(user.Client, $"{Connections}/{dto.Id}", new { acceptanceCriteriaField = "", clearExpiresAt = true }));
        Assert.Equal((null, "Acme"), (patched.Metadata.AcceptanceCriteriaField, patched.Label));
        Assert.Null(patched.ExpiresAt);
    }

    [Fact]
    public async Task FR166_Given_a_rejected_key_then_CREDENTIAL_INVALID_and_nothing_is_saved()
    {
        using var user = await ApiSession.RegisterAsync(_app);

        var response = await user.Client.PostAsJsonAsync(Connections, new { provider = "linear", label = "L", token = "fake_invalid_key" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.CredentialInvalid);
        Assert.Empty(await ProjectApi.ReadAsync<List<PlatformConnectionDto>>(await user.Client.GetAsync(U(Connections), Ct)));
    }

    [Fact]
    public async Task FR166_A_Linear_key_shows_its_organisation()
    {
        using var user = await ApiSession.RegisterAsync(_app);

        var dto = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsJsonAsync(Connections, new { provider = "linear", label = "L", token = "lin_api_1234" }, Ct), HttpStatusCode.Created);

        Assert.Equal("Fake Org", dto.Metadata.Organization);
    }

    [Fact]
    public async Task FR165_A_token_without_read_access_saves_as_insufficient_scope_and_browsing_it_is_CONNECTION_SCOPE_INSUFFICIENT()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        var dto = await JiraAsync(user, "fake_scope_token");

        Assert.Equal(PlatformConnectionStatus.InsufficientScope, dto.Status);
        await Problems.AssertAsync(
            await user.Client.GetAsync(U($"/api/me/trackers/jira/containers?connectionId={dto.Id}"), Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.ConnectionScopeInsufficient);
    }

    // ---------------------------------------------------------------- FR-165, FR-169 browsing

    [Fact]
    public async Task FR165_The_trackers_list_says_which_are_ready()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        await JiraAsync(user);

        var trackers = await ProjectApi.ReadAsync<List<TrackerStatusDto>>(await user.Client.GetAsync(U("/api/me/trackers"), Ct));

        Assert.Equal([TrackerProvider.Jira, TrackerProvider.Linear, TrackerProvider.Github, TrackerProvider.Gitlab], trackers.Select(t => t.Provider));
        Assert.True(trackers[0].Ready);
        Assert.Equal("acme.atlassian.net", Assert.Single(trackers[0].Connections).Detail);
        Assert.Equal((false, TrackerReadiness.NotConnected), (trackers[1].Ready, trackers[1].Reason));
    }

    [Fact]
    public async Task FR169_Browsing_lists_containers_and_item_summaries_and_hides_done_items()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        var connection = await JiraAsync(user);

        var containers = await ProjectApi.ReadAsync<TrackerContainerPageDto>(await user.Client.GetAsync(U($"/api/me/trackers/jira/containers?connectionId={connection.Id}"), Ct));
        var page = await ProjectApi.ReadAsync<TrackerSearchResultDto>(await user.Client.PostAsJsonAsync(
            "/api/me/trackers/jira/search",
            new { connectionId = connection.Id, containerId = containers.Items[0].Id },
            Ct));
        var withDone = await ProjectApi.ReadAsync<TrackerSearchResultDto>(await user.Client.PostAsJsonAsync(
            "/api/me/trackers/jira/search",
            new { connectionId = connection.Id, filter = new { statusCategories = DoneOnly } },
            Ct));

        Assert.Equal(11, page.Items.Count);
        Assert.DoesNotContain(page.Items, i => i.StatusCategory == SourceItemStatusCategory.Done);
        Assert.Equal(12, withDone.Items.Count);
    }

    [Fact]
    public async Task FR169_Given_invalid_JQL_then_TRACKER_QUERY_INVALID_shows_the_message_and_Linear_refuses_a_native_query()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        await JiraAsync(user);

        var body = await Problems.AssertAsync(
            await user.Client.PostAsJsonAsync("/api/me/trackers/jira/search", new { query = FakeIssueTrackerProvider.InvalidQuery }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.TrackerQueryInvalid);
        Assert.Contains("The query is invalid in the Fake tracker.", body.GetProperty("title").GetString(), StringComparison.Ordinal);

        await Problems.AssertAsync(
            await user.Client.PostAsJsonAsync("/api/me/trackers/linear/search", new { query = "anything" }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR169_No_connection_is_TRACKER_NOT_CONNECTED_someone_elses_is_CREDENTIAL_NOT_OWNER_an_unknown_provider_is_VALIDATION_and_a_rate_limit_is_429()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        using var other = await ApiSession.RegisterAsync(_app);
        var theirs = await JiraAsync(other);

        await Problems.AssertAsync(await user.Client.GetAsync(U("/api/me/trackers/linear/containers"), Ct), HttpStatusCode.Conflict, ErrorCodes.TrackerNotConnected);
        await Problems.AssertAsync(await user.Client.GetAsync(U($"/api/me/trackers/jira/containers?connectionId={theirs.Id}"), Ct), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(await user.Client.GetAsync(U($"/api/me/trackers/jira/containers?connectionId={Guid.NewGuid()}"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await user.Client.GetAsync(U("/api/me/trackers/nonsense/containers"), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        _app.Services.GetRequiredService<FakeTrackerScript>().RateLimitNext(TimeSpan.FromSeconds(30));
        var limited = await other.Client.GetAsync(U($"/api/me/trackers/jira/containers?connectionId={theirs.Id}"), Ct);
        await Problems.AssertAsync(limited, HttpStatusCode.TooManyRequests, ErrorCodes.UpstreamRateLimited);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task FR169_Browsing_needs_a_session()
    {
        using var anonymous = _app.CreateClient();

        await Problems.AssertAsync(await anonymous.GetAsync(U("/api/me/trackers"), Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    // ---------------------------------------------------------------- FR-170 snapshots

    [Fact]
    public async Task FR170_Given_a_completed_import_of_12_items_then_source_items_v1_is_locked_with_a_hash_and_the_event_has_no_body_or_token()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner, "atlassian_secret_1212");

        var queued = await ImportAsync(fixture.Owner, fixture.Project.Id, new { provider = "jira", connectionId = connection.Id, selection = new { query = "any" }, options = new { includeChildren = false } });
        Assert.Equal(TrackerImportStatus.Queued, queued.Status);
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var snapshot = Assert.Single(await SnapshotsAsync(fixture.Project.Id));
        Assert.Equal((1, ArtifactStatus.Locked), (snapshot.Version, snapshot.Status));
        Assert.Equal(ContentHash.Compute(snapshot.Content), snapshot.ContentHash);
        Assert.Contains("FAKE-12", snapshot.Content, StringComparison.Ordinal);

        var sources = await SourcesAsync(fixture.Owner, fixture.Project.Id);
        var import = Assert.Single(sources.Imports);
        Assert.Equal((TrackerImportStatus.Completed, 12, snapshot.Id), (import.Status, import.ItemCount, import.SourceArtifactId!.Value));
        Assert.Equal(snapshot.Id, sources.CurrentSnapshot!.ArtifactId);
        Assert.True(sources.CanChange);

        var completed = Assert.Single(await LifecycleSeed.EventsAsync(_app, fixture.Project.Id, EventTypes.TrackerImportCompleted));
        Assert.Equal(12, LifecycleSeed.Payload(completed.Payload).GetProperty("itemCount").GetInt32());
        Assert.DoesNotContain("description of fake", completed.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("atlassian_secret_1212", completed.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", completed.Payload, StringComparison.Ordinal);

        var row = await ProjectApi.WithDbAsync(_app, db => db.TrackerImports.AsNoTracking().SingleAsync(i => i.Id == import.Id, Ct));
        Assert.DoesNotContain("atlassian_secret_1212", JsonSerializer.Serialize(row.Items), StringComparison.Ordinal);
        Assert.All(row.Items, i => Assert.Null(i.Assignee));
    }

    [Fact]
    public async Task FR170_Given_a_429_with_Retry_After_60_then_the_job_re_queues_for_60_s_without_using_an_attempt()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        _app.Services.GetRequiredService<FakeTrackerScript>().RateLimitNext(TimeSpan.FromSeconds(60));
        var job = (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Single(j => j.Type == JobTypes.TrackerImport);
        var before = DateTimeOffset.UtcNow;

        Assert.True(await AgentJobs.RunCoreAsync(_app, job));

        var after = (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Single(j => j.Id == job.Id);
        Assert.Equal((JobStatus.Queued, 0), (after.Status, after.Attempts));
        Assert.InRange(after.RunAfter, before.AddSeconds(55), DateTimeOffset.UtcNow.AddSeconds(65));
        var import = Assert.Single((await SourcesAsync(fixture.Owner, fixture.Project.Id)).Imports);
        Assert.Equal((TrackerImportStatus.Queued, ErrorCodes.TrackerRateLimited), (import.Status, import.ErrorCode));
        Assert.Empty(await SnapshotsAsync(fixture.Project.Id));
    }

    [Theory]
    [InlineData(FakeIssueTrackerProvider.HugeContainer)]
    [InlineData(FakeIssueTrackerProvider.BigContainer)]
    public async Task FR170_Given_an_import_over_a_limit_then_it_fails_with_SOURCE_ITEMS_TOO_LARGE_and_no_artifact_is_written(string container)
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, new { provider = "jira", connectionId = connection.Id, selection = new { containerId = container }, options = new { includeChildren = false } });

        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var import = Assert.Single((await SourcesAsync(fixture.Owner, fixture.Project.Id)).Imports);
        Assert.Equal((TrackerImportStatus.Failed, ErrorCodes.SourceItemsTooLarge), (import.Status, import.ErrorCode));
        Assert.Empty(await SnapshotsAsync(fixture.Project.Id));
        Assert.Single(await LifecycleSeed.EventsAsync(_app, fixture.Project.Id, EventTypes.TrackerImportFailed));
    }

    [Fact]
    public async Task FR193_A_failed_import_records_the_importer_with_no_token_email_or_body_and_notifies_tracker_import_failed()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, new { provider = "jira", connectionId = connection.Id, selection = new { containerId = FakeIssueTrackerProvider.HugeContainer } });

        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var failed = Assert.Single(await LifecycleSeed.EventsAsync(_app, fixture.Project.Id, EventTypes.TrackerImportFailed));
        Assert.Equal(fixture.Owner.User!.Id, LifecycleSeed.Payload(failed.Payload).GetProperty("importedBy").GetGuid());
        Assert.DoesNotContain(fixture.Owner.User!.Email, failed.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fake_", failed.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("description", failed.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(_app, fixture.Project.Id), j => j.Payload.Contains("tracker_import_failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR170_A_second_import_writes_a_version_holding_both_and_a_rejected_token_marks_the_connection_invalid()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-2"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var versions = await SnapshotsAsync(fixture.Project.Id);
        var latest = versions.Single(a => a.Version == 2);
        Assert.Equal(1, latest.ParentVersion);
        Assert.Contains("FAKE-1", latest.Content, StringComparison.Ordinal);
        Assert.Contains("FAKE-2", latest.Content, StringComparison.Ordinal);

        // The tracker now rejects the stored token.
        var rejected = _app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.TrackerToken)
            .Protect(TrackerSecrets.Encode(PlatformProvider.Jira, "ana@example.com", "fake_invalid_now"));
        await ProjectApi.WithDbAsync(_app, db => db.PlatformConnections.Where(c => c.Id == connection.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SecretEncrypted, rejected), Ct));
        var third = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-3"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var row = await ProjectApi.WithDbAsync(_app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.Id == connection.Id, Ct));
        Assert.Equal(PlatformConnectionStatus.Invalid, row.Status);
        var failed = (await SourcesAsync(fixture.Owner, fixture.Project.Id)).Imports.Single(i => i.Id == third.Id);
        Assert.Equal((TrackerImportStatus.Failed, ErrorCodes.TrackerNotConnected), (failed.Status, failed.ErrorCode));
    }

    [Fact]
    public async Task FR170_Text_holding_NUL_is_stored_without_it()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        _app.Services.GetRequiredService<FakeTrackerScript>().Describe("FAKE-1", "be\0fore");
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));

        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        Assert.Contains("before", Assert.Single(await SnapshotsAsync(fixture.Project.Id)).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR170_A_save_failure_that_isnt_a_conflict_fails_the_import_instead_of_deferring_forever()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        var import = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        var suffix = import.Id.ToString("N");
        var install = $"""
            CREATE FUNCTION refuse_{suffix}() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''refused''; END';
            CREATE TRIGGER refuse_{suffix} BEFORE UPDATE ON tracker_imports FOR EACH ROW
                WHEN (NEW.id = '{import.Id}' AND NEW.status = 'completed') EXECUTE FUNCTION refuse_{suffix}();
            """;
        await ProjectApi.WithDbAsync(_app, db => db.Database.ExecuteSqlRawAsync(install, Ct));
        try
        {
            var job = (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Single(j => j.Type == JobTypes.TrackerImport);

            Assert.False(await AgentJobs.RunCoreAsync(_app, job));

            var after = (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Single(j => j.Id == job.Id);
            Assert.Equal(JobStatus.Failed, after.Status);
            var failed = Assert.Single((await SourcesAsync(fixture.Owner, fixture.Project.Id)).Imports);
            Assert.Equal((TrackerImportStatus.Failed, ErrorCodes.TrackerImportFailed), (failed.Status, failed.ErrorCode));
            Assert.Empty(await SnapshotsAsync(fixture.Project.Id));
        }
        finally
        {
            var uninstall = $"DROP TRIGGER refuse_{suffix} ON tracker_imports; DROP FUNCTION refuse_{suffix}();";
            await ProjectApi.WithDbAsync(_app, db => db.Database.ExecuteSqlRawAsync(uninstall, CancellationToken.None));
        }
    }

    [Fact]
    public void FR170_Only_a_concurrency_conflict_or_unique_violation_defers()
    {
        Assert.True(TrackerImportJobHandler.IsConflict(new DbUpdateConcurrencyException("x")));
        Assert.True(TrackerImportJobHandler.IsConflict(new DbUpdateException("x", new Npgsql.PostgresException("m", "ERROR", "ERROR", "23505"))));
        Assert.False(TrackerImportJobHandler.IsConflict(new DbUpdateException("x", new Npgsql.PostgresException("m", "ERROR", "ERROR", "22021"))));
        Assert.False(TrackerImportJobHandler.IsConflict(new DbUpdateException("x")));
    }

    // ---------------------------------------------------------------- FR-171 entry points

    [Fact]
    public async Task FR171_Given_a_wizard_with_an_idea_and_5_Linear_issues_when_the_project_is_created_then_one_import_job_is_queued()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        var connection = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsJsonAsync(Connections, new { provider = "linear", label = "L", token = "lin_api_1234" }, Ct), HttpStatusCode.Created);

        var created = await ProjectApi.ReadAsync<Aide.Api.Features.Projects.ProjectDto>(
            await user.Client.PostAsJsonAsync(
                "/api/projects",
                new
                {
                    name = ProjectApi.UniqueName(),
                    idea = "Build a todo API.",
                    git = new { provider = "local" },
                    sourceImports = new[] { new { provider = "linear", connectionId = connection.Id, selection = new { keys = FakeKeys(5) } } },
                },
                Ct),
            HttpStatusCode.Created);

        Assert.Single(await LifecycleSeed.JobsAsync(_app, created.Id), j => j.Type == JobTypes.TrackerImport);
        await AgentJobs.RunQueuedAsync(_app, created.Id);
        Assert.Equal(5, Assert.Single((await SourcesAsync(user, created.Id)).Imports).ItemCount);
    }

    [Fact]
    public async Task FR171_D_V5_6_The_idea_stays_required_and_a_failed_import_never_blocks_creation()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        var noIdea = await user.Client.PostAsJsonAsync(
            "/api/projects",
            new { name = ProjectApi.UniqueName(), idea = " ", git = new { provider = "local" }, sourceImports = new[] { new { provider = "jira", selection = new { keys = OneKey } } } },
            Ct);
        await Problems.AssertAsync(noIdea, HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // No Jira connection: the project is created, and the import fails where the Refinement page shows it.
        var created = await ProjectApi.ReadAsync<Aide.Api.Features.Projects.ProjectDto>(
            await user.Client.PostAsJsonAsync(
                "/api/projects",
                new { name = ProjectApi.UniqueName(), idea = "An idea", git = new { provider = "local" }, sourceImports = new[] { new { provider = "jira", selection = new { keys = OneKey } } } },
                Ct),
            HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(_app, created.Id);

        var import = Assert.Single((await SourcesAsync(user, created.Id)).Imports);
        Assert.Equal((TrackerImportStatus.Failed, ErrorCodes.TrackerNotConnected), (import.Status, import.ErrorCode));
    }

    [Fact]
    public async Task FR171_Another_users_connection_on_creation_is_CREDENTIAL_NOT_OWNER_and_a_bad_selection_VALIDATION()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        using var other = await ApiSession.RegisterAsync(_app);
        var theirs = await JiraAsync(other);

        await Problems.AssertAsync(
            await user.Client.PostAsJsonAsync(
                "/api/projects",
                new { name = ProjectApi.UniqueName(), idea = "An idea", git = new { provider = "local" }, sourceImports = new[] { Keys(theirs.Id, "FAKE-1") } },
                Ct),
            HttpStatusCode.Forbidden,
            ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(
            await user.Client.PostAsJsonAsync(
                "/api/projects",
                new { name = ProjectApi.UniqueName(), idea = "An idea", git = new { provider = "local" }, sourceImports = new[] { new { provider = "jira", selection = new { keys = NotAKey } } } },
                Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR171_In_a_later_phase_POST_sources_is_WRONG_PHASE()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await LifecycleSeed.SetProjectAsync(_app, fixture.Project.Id, Phase.Specification, 2, fixture.Project.ApprovalMode);

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(Sources(fixture.Project.Id), Keys(connection.Id, "FAKE-1"), Ct), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
    }

    [Fact]
    public async Task FR171_Permissions_on_sources()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var mine = await JiraAsync(fixture.Contributor);
        var owners = await JiraAsync(fixture.Owner);
        var viewers = await JiraAsync(fixture.Viewer);
        var path = Sources(fixture.Project.Id);

        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsJsonAsync(path, Keys(viewers.Id, "FAKE-1"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsJsonAsync(path, Keys(null, "FAKE-1"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(U(path), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsJsonAsync(path, Keys(owners.Id, "FAKE-1"), Ct), HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        await Problems.AssertAsync(await fixture.Viewer.Client.DeleteAsync(U($"{path}/{Guid.NewGuid()}"), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.DeleteAsync(U($"{path}/{Guid.NewGuid()}"), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(U($"{path}/{Guid.NewGuid()}/reimport"), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        await ImportAsync(fixture.Contributor, fixture.Project.Id, Keys(mine.Id, "FAKE-1"));
        Assert.Single((await SourcesAsync(fixture.Viewer, fixture.Project.Id)).Imports);
        await ProjectApi.ReadAsync<SourcesCheckDto>(await fixture.Viewer.Client.PostAsync(U($"{path}/check"), null, Ct));
    }

    [Fact]
    public async Task FR171_Ticking_too_many_keys_is_SOURCE_ITEMS_TOO_LARGE_and_no_connection_on_POST_is_TRACKER_NOT_CONNECTED()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var path = Sources(fixture.Project.Id);

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(path, Keys(null, "FAKE-1"), Ct), HttpStatusCode.Conflict, ErrorCodes.TrackerNotConnected);

        var connection = await JiraAsync(fixture.Owner);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(path, Keys(connection.Id, FakeKeys(201)), Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.SourceItemsTooLarge);
    }

    // ---------------------------------------------------------------- FR-172 agents

    private async Task<AgentRun> RefineAsync(ApiSession user, Guid projectId)
    {
        await ProjectApi.ReadAsync<Aide.Api.Features.Refinement.RefinementRoundDto>(await user.Client.PostAsJsonAsync($"/api/projects/{projectId}/refinement/run", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(_app, projectId);
        return (await AgentJobs.RunsAsync(_app, projectId)).Last(r => r.Agent == AgentKind.IdeaRefinement);
    }

    [Fact]
    public async Task FR172_Given_source_items_v2_when_refinement_runs_then_its_input_artifacts_list_v2_and_the_draft_is_not_behind()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-2"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var run = await RefineAsync(fixture.Owner, fixture.Project.Id);

        Assert.Contains(run.InputArtifacts, a => a.Type == ArtifactType.SourceItems && a.Version == 2);
        var draft = (await SourcesAsync(fixture.Owner, fixture.Project.Id)).Draft!;
        Assert.Equal((2, false), (draft.BuiltFromSnapshotVersion, draft.BehindSnapshot));

        // FR-173: a new snapshot invalidates nothing, and the draft now shows it's behind.
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-3"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var behind = (await SourcesAsync(fixture.Owner, fixture.Project.Id)).Draft!;
        Assert.Equal((2, true), (behind.BuiltFromSnapshotVersion, behind.BehindSnapshot));
        Assert.Empty(await LifecycleSeed.EventsAsync(_app, fixture.Project.Id, EventTypes.ArtifactInvalidated));
    }

    [Fact]
    public async Task FR172_A_refinement_run_with_a_queued_import_waits_without_using_an_attempt()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await ProjectApi.ReadAsync<Aide.Api.Features.Refinement.RefinementRoundDto>(await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{fixture.Project.Id}/refinement/run", new { }, Ct), HttpStatusCode.Accepted);
        var job = (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Single(j => j.Type == JobTypes.IdeaRefinement);

        Assert.True(await AgentJobs.RunCoreAsync(_app, job));

        var after = (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Single(j => j.Id == job.Id);
        Assert.Equal((JobStatus.Queued, 0), (after.Status, after.Attempts));
    }

    [Fact]
    public async Task FR172_A_stale_pending_import_gives_the_warning_and_the_run_continues()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        var import = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await ProjectApi.WithDbAsync(_app, db => db.TrackerImports.Where(i => i.Id == import.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-11)), Ct));
        await ProjectApi.WithDbAsync(_app, db => db.Jobs.Where(j => j.ProjectId == fixture.Project.Id && j.Type == JobTypes.TrackerImport)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), Ct));

        var run = await RefineAsync(fixture.Owner, fixture.Project.Id);

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Contains(Aide.Domain.Agents.AgentRunWarnings.SourceItemsUnavailable, await WarningsOfAsync(fixture.Owner, fixture.Project.Id, run.Id));
    }

    private static async Task<IReadOnlyList<string>> WarningsOfAsync(ApiSession user, Guid projectId, Guid runId)
    {
        var dto = await ProjectApi.ReadAsync<JsonElement>(await user.Client.GetAsync(U($"/api/projects/{projectId}/runs/{runId}"), Ct));
        return [.. dto.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)];
    }

    [Fact]
    public async Task FR172_Over_the_budget_for_the_model_at_run_time_the_run_fails_with_SOURCE_ITEMS_TOO_LARGE_and_nothing_is_drafted()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        await LifecycleSeed.AddArtifactAsync(_app, fixture.Project.Id, ArtifactType.SourceItems, version: 1, content: new string('x', 900_000));

        await ProjectApi.ReadAsync<Aide.Api.Features.Refinement.RefinementRoundDto>(await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{fixture.Project.Id}/refinement/run", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var run = Assert.Single(await AgentJobs.RunsAsync(_app, fixture.Project.Id), r => r.Agent == AgentKind.IdeaRefinement);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.StartsWith(ErrorCodes.SourceItemsTooLarge, run.Error, StringComparison.Ordinal);
        Assert.Empty(await AgentJobs.ArtifactsAsync(_app, fixture.Project.Id, ArtifactType.ProjectRequest));
    }

    [Fact]
    public async Task FR172_A_spec_warns_UNCOVERED_SOURCE_ITEMS_listing_not_done_items_no_requirement_cites()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1", "FAKE-2", "FAKE-11"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        await LifecycleSeed.AddArtifactAsync(_app, fixture.Project.Id, ArtifactType.TechnicalSpecification, version: 1, content: "### FR-1 Thing\nSource: FAKE-1\n");

        var artifact = await ProjectApi.ReadAsync<ArtifactDto>(await fixture.Owner.Client.GetAsync(U($"/api/projects/{fixture.Project.Id}/artifacts/technical_specification/versions/1"), Ct));

        var warning = Assert.Single(artifact.Warnings);
        Assert.Equal((ErrorCodes.UncoveredSourceItems, "FAKE-2"), (warning.Code, Assert.Single(warning.Ids)));
    }

    // ---------------------------------------------------------------- FR-173 re-import, remove, drift

    [Fact]
    public async Task FR173_Re_import_repeats_the_query_supersedes_and_writes_a_new_version_earlier_versions_stay_readable()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        var first = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var path = Sources(fixture.Project.Id);

        var second = await ProjectApi.ReadAsync<SourceImportDto>(await fixture.Owner.Client.PostAsync(U($"{path}/{first.Id}/reimport"), null, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var sources = await SourcesAsync(fixture.Owner, fixture.Project.Id);
        Assert.Equal(TrackerImportStatus.Completed, sources.Imports.Single(i => i.Id == second.Id).Status);
        Assert.Equal(TrackerImportStatus.Superseded, sources.Imports.Single(i => i.Id == first.Id).Status);
        Assert.Equal(["FAKE-1"], second.Selection.Keys);
        var versions = await SnapshotsAsync(fixture.Project.Id);
        Assert.Equal(2, versions.Count);
        Assert.All(versions, a => Assert.Equal(ArtifactStatus.Locked, a.Status));
        await ProjectApi.ReadAsync<ArtifactDto>(await fixture.Owner.Client.GetAsync(U($"/api/projects/{fixture.Project.Id}/artifacts/source_items/versions/1"), Ct));

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U($"{path}/{first.Id}/reimport"), null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR173_A_duplicate_retry_returns_the_pending_re_import_and_the_items_count_once()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        _app.Services.GetRequiredService<FakeTrackerScript>().FailNext(1);
        var first = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var path = Sources(fixture.Project.Id);

        var retry = await ProjectApi.ReadAsync<SourceImportDto>(await fixture.Owner.Client.PostAsync(U($"{path}/{first.Id}/reimport"), null, Ct), HttpStatusCode.Accepted);
        var duplicate = await ProjectApi.ReadAsync<SourceImportDto>(await fixture.Owner.Client.PostAsync(U($"{path}/{first.Id}/reimport"), null, Ct), HttpStatusCode.Accepted);
        var ofRetry = await ProjectApi.ReadAsync<SourceImportDto>(await fixture.Owner.Client.PostAsync(U($"{path}/{retry.Id}/reimport"), null, Ct), HttpStatusCode.Accepted);

        Assert.Equal(retry.Id, duplicate.Id);
        Assert.Equal(retry.Id, ofRetry.Id);
        Assert.Equal(2, (await LifecycleSeed.JobsAsync(_app, fixture.Project.Id)).Count(j => j.Type == JobTypes.TrackerImport));

        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var sources = await SourcesAsync(fixture.Owner, fixture.Project.Id);
        Assert.Single(sources.Imports, i => i.Status == TrackerImportStatus.Completed);

        // Removing the one active row takes the items out of the snapshot.
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.DeleteAsync(U($"{path}/{retry.Id}"), Ct)).StatusCode);
        Assert.Equal(SourceItemsRenderer.Empty, (await SnapshotsAsync(fixture.Project.Id)).OrderBy(a => a.Version).Last().Content);
    }

    [Fact]
    public async Task FR173_Completing_a_retry_supersedes_the_whole_chain_and_no_later_run_warns()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        _app.Services.GetRequiredService<FakeTrackerScript>().FailNext(2);
        var a = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var path = Sources(fixture.Project.Id);
        var b = await ProjectApi.ReadAsync<SourceImportDto>(await fixture.Owner.Client.PostAsync(U($"{path}/{a.Id}/reimport"), null, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var c = await ProjectApi.ReadAsync<SourceImportDto>(await fixture.Owner.Client.PostAsync(U($"{path}/{b.Id}/reimport"), null, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var imports = (await SourcesAsync(fixture.Owner, fixture.Project.Id)).Imports;
        Assert.Equal(TrackerImportStatus.Superseded, imports.Single(i => i.Id == a.Id).Status);
        Assert.Equal(TrackerImportStatus.Superseded, imports.Single(i => i.Id == b.Id).Status);
        Assert.Equal(TrackerImportStatus.Completed, imports.Single(i => i.Id == c.Id).Status);

        var run = await RefineAsync(fixture.Owner, fixture.Project.Id);
        Assert.DoesNotContain(AgentRunWarnings.SourceItemsUnavailable, await WarningsOfAsync(fixture.Owner, fixture.Project.Id, run.Id));
    }

    [Fact]
    public async Task FR165_Expiry_and_the_acceptance_field_on_a_connection_that_has_none_are_VALIDATION()
    {
        using var user = await ApiSession.RegisterAsync(_app);
        var netlify = await DeploymentHarness.SeedConnectionAsync(_app, user.User!.Id, provider: PlatformProvider.Netlify);
        var linear = await ProjectApi.ReadAsync<PlatformConnectionDto>(await user.Client.PostAsJsonAsync(Connections, new { provider = "linear", label = "L", token = "lin_api_1234" }, Ct), HttpStatusCode.Created);

        await Problems.AssertAsync(await ProjectApi.PatchAsync(user.Client, $"{Connections}/{netlify}", new { expiresAt = DateTimeOffset.UtcNow.AddDays(9) }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(user.Client, $"{Connections}/{netlify}", new { clearExpiresAt = true }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(user.Client, $"{Connections}/{linear.Id}", new { acceptanceCriteriaField = "customfield_1" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR173_Remove_writes_a_new_version_without_the_import_and_records_no_body()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        var first = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-2"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);

        var response = await fixture.Owner.Client.DeleteAsync(U($"{Sources(fixture.Project.Id)}/{first.Id}"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var latest = (await SnapshotsAsync(fixture.Project.Id)).OrderBy(a => a.Version).Last();
        Assert.Equal(3, latest.Version);
        Assert.DoesNotContain("FAKE-1:", latest.Content, StringComparison.Ordinal);
        Assert.Contains("FAKE-2:", latest.Content, StringComparison.Ordinal);
        var removed = Assert.Single(await LifecycleSeed.EventsAsync(_app, fixture.Project.Id, EventTypes.SourceItemsRemoved));
        Assert.DoesNotContain("description", removed.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FR173_In_a_later_phase_reimport_and_remove_are_WRONG_PHASE()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        var import = await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        await LifecycleSeed.SetProjectAsync(_app, fixture.Project.Id, Phase.Planning, 3, fixture.Project.ApprovalMode);

        var path = Sources(fixture.Project.Id);
        await Problems.AssertAsync(await fixture.Owner.Client.DeleteAsync(U($"{path}/{import.Id}"), Ct), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(U($"{path}/{import.Id}/reimport"), null, Ct), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
        Assert.False((await SourcesAsync(fixture.Owner, fixture.Project.Id)).CanChange);
    }

    [Fact]
    public async Task FR173_Given_an_item_edited_after_import_when_the_page_opens_then_it_shows_changed_and_the_hash_is_the_same()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Owner);
        await ImportAsync(fixture.Owner, fixture.Project.Id, Keys(connection.Id, "FAKE-1", "FAKE-2", "FAKE-3"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        var before = Assert.Single(await SnapshotsAsync(fixture.Project.Id)).ContentHash;
        var script = _app.Services.GetRequiredService<FakeTrackerScript>();
        script.Touch("FAKE-1", DateTimeOffset.UtcNow);
        script.Remove("FAKE-2");

        var check = await ProjectApi.ReadAsync<SourcesCheckDto>(await fixture.Owner.Client.PostAsync(U($"{Sources(fixture.Project.Id)}/check"), null, Ct));

        Assert.Equal(SourceItemDrift.Changed, check.Items.Single(i => i.Key == "FAKE-1").Drift);
        Assert.Equal(SourceItemDrift.Removed, check.Items.Single(i => i.Key == "FAKE-2").Drift);
        Assert.Equal(SourceItemDrift.Unchanged, check.Items.Single(i => i.Key == "FAKE-3").Drift);
        Assert.Equal(before, check.SnapshotContentHash);
        Assert.Equal(before, Assert.Single(await SnapshotsAsync(fixture.Project.Id)).ContentHash);

        // Cached for 5 minutes: another change isn't seen yet.
        script.Touch("FAKE-3", DateTimeOffset.UtcNow);
        var again = await ProjectApi.ReadAsync<SourcesCheckDto>(await fixture.Owner.Client.PostAsync(U($"{Sources(fixture.Project.Id)}/check"), null, Ct));
        Assert.Equal(SourceItemDrift.Unchanged, again.Items.Single(i => i.Key == "FAKE-3").Drift);
    }

    [Fact]
    public async Task FR173_Given_the_importer_left_and_the_viewer_has_no_Jira_connection_then_items_show_unknown_and_the_page_still_works()
    {
        using var fixture = await ProjectApi.FixtureAsync(_app);
        var connection = await JiraAsync(fixture.Contributor);
        await ImportAsync(fixture.Contributor, fixture.Project.Id, Keys(connection.Id, "FAKE-1"));
        await AgentJobs.RunQueuedAsync(_app, fixture.Project.Id);
        await ProjectApi.WithDbAsync(_app, db => db.ProjectMembers.Where(m => m.ProjectId == fixture.Project.Id && m.UserId == fixture.Contributor.User!.Id).ExecuteDeleteAsync(Ct));

        var check = await ProjectApi.ReadAsync<SourcesCheckDto>(await fixture.Viewer.Client.PostAsync(U($"{Sources(fixture.Project.Id)}/check"), null, Ct));

        Assert.Equal(SourceItemDrift.Unknown, Assert.Single(check.Items).Drift);

        // The viewer's own connection is used once they have one (an unknown result isn't cached).
        await JiraAsync(fixture.Viewer);
        var own = await ProjectApi.ReadAsync<SourcesCheckDto>(await fixture.Viewer.Client.PostAsync(U($"{Sources(fixture.Project.Id)}/check"), null, Ct));
        Assert.Equal(SourceItemDrift.Unchanged, Assert.Single(own.Items).Drift);
    }
}
