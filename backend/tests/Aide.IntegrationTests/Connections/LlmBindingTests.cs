using System.Net;
using System.Text.Json;
using Aide.Api.Agents;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Connections;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Runs;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Connections;

/// <summary>FR-79, D-V2-5, FR-78 (run failures): binding an Owner's connection to a project, and the runs that use it.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class LlmBindingTests(AideApiFactory factory) : IDisposable
{
    private const string OwnerKey = "sk-proj-ownerOpenAiKey-0001-e5f6";
    private const string OtherKey = "sk-proj-otherOwnerKey-0002-g7h8";
    private const string AnthropicKey = "sk-ant-api03-ownerAnthropic-i9j0";
    private const string ProjectRequest = "# Todo API\n\n## Summary\nA small todo API.";

    private readonly LlmStubHandler _anthropic = new();
    private readonly LlmStubHandler _openai = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR79_An_Owner_binds_their_own_connection_and_members_see_connected_by_without_the_key()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey, "Ana's key");

        var patch = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", credentialId = credential.Id });
        var bound = await ProjectApi.ReadAsync<LlmSettingsDto>(patch);
        var viewerResponse = await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/settings/llm", UriKind.Relative), Ct);
        var viewerText = await viewerResponse.Content.ReadAsStringAsync(Ct);
        var viewer = await ProjectApi.ReadAsync<LlmSettingsDto>(viewerResponse);
        var contributor = await ProjectApi.ReadAsync<LlmSettingsDto>(await fixture.Contributor.Client.GetAsync(new Uri($"{fixture.Path}/settings/llm", UriKind.Relative), Ct));
        var owner = await ProjectApi.ReadAsync<ProjectSettingsDto>(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct));

        var expected = new LlmBindingDto(credential.Id, LlmProvider.Openai, "Ana's key", "e5f6", LlmCredentialStatus.Valid, new LlmBindingUserDto(fixture.Owner.User!.Id, fixture.Owner.User.Name));
        Assert.Equal((LlmProvider.Openai, true, true), (bound.Provider, bound.HasApiKey, bound.CanBind));
        Assert.Equal(expected, bound.Binding);
        Assert.Equal((expected, false), (viewer.Binding, viewer.CanBind));
        Assert.Equal((expected, false), (contributor.Binding, contributor.CanBind));
        Assert.Equal((expected, true), (owner.Llm.Binding, owner.Llm.CanBind));
        Assert.True(viewer.Providers.Single(p => p.Provider == LlmProvider.Openai).Configured);
        Assert.False(viewer.Providers.Single(p => p.Provider == LlmProvider.Anthropic).Configured);
        Assert.DoesNotContain(OwnerKey, viewerText, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerKey, await patch.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var stored = await SettingsAsync(app, fixture.Project.Id);
        Assert.Equal((credential.Id, fixture.Owner.User.Id), (stored.CredentialId!.Value, stored.BoundByUserId!.Value));
        var listed = await ProjectApi.ReadAsync<LlmConnectionsDto>(await fixture.Owner.Client.GetAsync(new Uri("/api/me/connections/llm", UriKind.Relative), Ct));
        Assert.Equal(new LlmBoundProjectDto(fixture.Project.Id, fixture.Project.Name), Assert.Single(Assert.Single(listed.Credentials).BoundProjects));
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/settings/llm", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR79_A_Contributor_sending_a_credentialId_is_403()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var own = await LlmStubs.CreateAsync(fixture.Contributor.Client, "openai", OtherKey);

        var response = await ProjectApi.PatchAsync(fixture.Contributor.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", credentialId = own.Id });

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Null((await SettingsAsync(app, fixture.Project.Id)).CredentialId);
    }

    [Fact]
    public async Task FR79_An_Owner_sending_another_users_credentialId_is_CREDENTIAL_NOT_OWNER_403()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        using var coOwner = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, fixture.Project.Id, coOwner.User!.Id, ProjectRole.Owner);
        var theirs = await LlmStubs.CreateAsync(coOwner.Client, "openai", OtherKey);

        var response = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", credentialId = theirs.Id });

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.CredentialNotOwner);
        var stored = await SettingsAsync(app, fixture.Project.Id);
        Assert.Equal(((Guid?)null, LlmProvider.Fake), (stored.CredentialId, stored.Provider));
    }

    [Fact]
    public async Task FR79_Unknown_connections_are_NOT_FOUND_and_another_providers_connection_is_VALIDATION()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var anthropic = await LlmStubs.CreateAsync(fixture.Owner.Client, "anthropic", AnthropicKey);

        await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", credentialId = Guid.CreateVersion7() }),
            HttpStatusCode.NotFound,
            ErrorCodes.NotFound);
        var mismatch = await Problems.AssertAsync(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", credentialId = anthropic.Id }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
        Assert.True(mismatch.GetProperty("errors").TryGetProperty("credentialId", out _), mismatch.ToString());
        Assert.Null((await SettingsAsync(app, fixture.Project.Id)).CredentialId);
    }

    [Fact]
    public async Task FR79_Choosing_a_keyed_provider_without_a_binding_is_VALIDATION_and_unbinding_or_switching_drops_the_binding()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey);

        var refused = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "anthropic" });
        var body = await Problems.AssertAsync(refused, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty("provider", out _), body.ToString());

        await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", credentialId = credential.Id }));
        var models = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new
        {
            defaultModel = "gpt-5-mini",
            modelsByAgent = new { codeGenerator = "gpt-5" },
        }));
        var resent = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { credentialId = credential.Id }));
        var unbound = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { credentialId = (Guid?)null }));
        await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { credentialId = credential.Id }));
        var ollama = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "ollama" }));

        Assert.Equal(("gpt-5-mini", "gpt-5", credential.Id), (models.DefaultModel, models.ModelsByAgent.CodeGenerator, models.Binding!.CredentialId));
        Assert.Equal(credential.Id, resent.Binding!.CredentialId);
        Assert.Equal((LlmProvider.Openai, (LlmBindingDto?)null, false), (unbound.Provider, unbound.Binding, unbound.HasApiKey));
        Assert.Equal((LlmProvider.Ollama, (LlmBindingDto?)null, "qwen2.5-coder"), (ollama.Provider, ollama.Binding, ollama.DefaultModel));
        var changes = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.SettingsUpdated)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Payload)
            .ToListAsync(Ct));
        Assert.Equal(5, changes.Count); // bind, models, (re-sent: no change), unbind, bind, switch
        Assert.All(changes, p => Assert.DoesNotContain(OwnerKey, p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR79_Runs_use_the_bound_Owners_key_whoever_triggered_them_and_record_the_connection()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey, "Ana's key");
        await BindAsync(fixture, "openai", credential.Id, "gpt-5");
        _openai.Enqueue(HttpStatusCode.OK, """{"choices":[{"message":{"content":"spec text"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""");

        // The Contributor's action queues the job; the run never knows who asked, so the Owner's key pays (D-V2-5).
        var outcome = await RunAsync(app, Spec(fixture.Project.Id, NewJob(fixture.Project.Id)));

        var call = _openai.Requests[^1];
        Assert.Equal(("https://llm-api.invalid/chat/completions", $"Bearer {OwnerKey}"), (call.Uri, call.Headers["Authorization"]));
        Assert.Equal(credential.Id, (await RunRowAsync(app, outcome.RunId)).CredentialId);
        var run = await ProjectApi.ReadAsync<AgentRunDetailDto>(await fixture.Contributor.Client.GetAsync(new Uri($"{fixture.Path}/runs/{outcome.RunId}", UriKind.Relative), Ct));
        Assert.Equal("Ana's key (••••e5f6)", run.CredentialLabel);
        var list = await ProjectApi.ReadAsync<PagedResult<AgentRunDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/runs", UriKind.Relative), Ct));
        Assert.Equal("Ana's key (••••e5f6)", Assert.Single(list.Items).CredentialLabel);
    }

    [Fact]
    public async Task FR78_A_key_rejected_during_a_run_is_marked_invalid_and_the_run_fails_CREDENTIAL_INVALID_without_retrying()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey);
        await BindAsync(fixture, "openai", credential.Id, "gpt-5");
        _openai.Enqueue(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided: " + OwnerKey + "\"}}");
        var job = NewJob(fixture.Project.Id);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, job)));

        Assert.Equal((false, ErrorCodes.CredentialInvalid), (ex.Transient, ex.ErrorCode));
        var run = await RunRowAsync(app, job.Id);
        Assert.Equal((AgentRunStatus.Failed, 1), (run.Status, run.Attempts));
        Assert.StartsWith("CREDENTIAL_INVALID: ", run.Error, StringComparison.Ordinal);
        var stored = await ProjectApi.WithDbAsync(app, db => db.LlmCredentials.AsNoTracking().SingleAsync(c => c.Id == credential.Id, Ct));
        Assert.Equal(LlmCredentialStatus.Invalid, stored.Status);
        var events = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == fixture.Project.Id && (e.Type == EventTypes.ConnectionInvalid || e.Type == EventTypes.RunFailed))
            .Select(e => new { e.Type, e.Payload })
            .ToListAsync(Ct));
        var invalid = JsonDocument.Parse(Assert.Single(events, e => e.Type == EventTypes.ConnectionInvalid).Payload).RootElement;
        Assert.Equal((credential.Id, fixture.Owner.User!.Id), (invalid.GetProperty("credentialId").GetGuid(), invalid.GetProperty("userId").GetGuid()));
        Assert.Single(events, e => e.Type == EventTypes.RunFailed);

        // The owner is notified: the run failure queues an Owner notification (the dedicated "connection invalid" type is FR-92).
        var notifications = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.Notification && j.ProjectId == fixture.Project.Id)
            .Select(j => j.Payload)
            .ToListAsync(Ct));
        Assert.Contains(notifications, p => p.Contains("run_failed", StringComparison.Ordinal));
        Assert.All(events, e => Assert.DoesNotContain(OwnerKey, e.Payload, StringComparison.Ordinal));
        Assert.DoesNotContain(OwnerKey, run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.ToString().Contains(OwnerKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("demote")]
    [InlineData("leave")]
    [InlineData("remove")]
    [InlineData("delete")]
    public async Task FR79_The_binding_is_cleared_when_its_Owner_loses_the_role_leaves_or_deletes_the_connection_and_runs_then_fail_PROVIDER_NOT_CONFIGURED(string how)
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        using var binder = await ApiSession.RegisterAsync(app, name: "Ana");
        await ProjectApi.AddMemberInDbAsync(app, fixture.Project.Id, binder.User!.Id, ProjectRole.Owner);
        var credential = await LlmStubs.CreateAsync(binder.Client, "openai", OwnerKey);
        await BindAsync(fixture, "openai", credential.Id, "gpt-5", binder.Client);
        var earlier = NewJob(fixture.Project.Id);
        _openai.Enqueue(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""");
        await RunAsync(app, Spec(fixture.Project.Id, earlier));

        var response = how switch
        {
            "demote" => await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{binder.User.Id}", new { role = "contributor" }),
            "leave" => await binder.Client.DeleteAsync(new Uri($"{fixture.Path}/members/{binder.User.Id}", UriKind.Relative), Ct),
            "remove" => await fixture.Owner.Client.DeleteAsync(new Uri($"{fixture.Path}/members/{binder.User.Id}", UriKind.Relative), Ct),
            _ => await binder.Client.DeleteAsync(new Uri($"/api/me/connections/llm/{credential.Id}", UriKind.Relative), Ct),
        };
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));

        var settings = await ProjectApi.ReadAsync<LlmSettingsDto>(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/settings/llm", UriKind.Relative), Ct));
        Assert.Equal((LlmProvider.Openai, (LlmBindingDto?)null), (settings.Provider, settings.Binding));
        var stored = await SettingsAsync(app, fixture.Project.Id);
        Assert.Equal(((Guid?)null, (Guid?)null), (stored.CredentialId, stored.BoundByUserId));
        var cleared = await ProjectApi.WithDbAsync(app, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.LlmBindingCleared)
            .Select(e => e.Payload)
            .ToListAsync(Ct));
        var payload = JsonDocument.Parse(Assert.Single(cleared)).RootElement;
        var reason = how switch { "demote" => "owner_role_lost", "delete" => "connection_deleted", _ => "member_removed" };
        Assert.Equal((reason, credential.Id, binder.User.Id), (payload.GetProperty("reason").GetString(), payload.GetProperty("credentialId").GetGuid(), payload.GetProperty("boundByUserId").GetGuid()));

        var job = NewJob(fixture.Project.Id);
        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(app, Spec(fixture.Project.Id, job)));
        Assert.Equal(ErrorCodes.ProviderNotConfigured, ex.ErrorCode);

        // History is kept: the earlier run still exists (its credential_id is set null only when the connection is deleted).
        var earlierRun = await RunRowAsync(app, earlier.Id);
        Assert.Equal(how == "delete" ? null : credential.Id, earlierRun.CredentialId);
    }

    [Fact]
    public async Task FR79_A_bind_waiting_on_the_project_lock_sees_a_demotion_that_committed_first_and_is_403()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        using var binder = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, fixture.Project.Id, binder.User!.Id, ProjectRole.Owner);
        var credential = await LlmStubs.CreateAsync(binder.Client, "openai", OwnerKey);

        // A demotion holds the project row lock (as UpdateMemberRole does) while the bind arrives.
        await using var demotion = await LockProjectAsync(fixture.Project.Id);
        var bind = ProjectApi.PatchAsync(binder.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", defaultModel = "gpt-5", credentialId = credential.Id });
        await WaitForLockWaiterAsync(bind);
        await using (var update = new NpgsqlCommand("UPDATE project_members SET role = 'contributor' WHERE project_id = @p AND user_id = @u", demotion.Connection, demotion.Transaction))
        {
            update.Parameters.AddWithValue("p", fixture.Project.Id);
            update.Parameters.AddWithValue("u", binder.User.Id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(Ct));
        }

        await demotion.Transaction.CommitAsync(Ct);

        await Problems.AssertAsync(await bind, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var stored = await SettingsAsync(app, fixture.Project.Id);
        Assert.Equal(((Guid?)null, LlmProvider.Fake), (stored.CredentialId, stored.Provider));
    }

    [Fact]
    public async Task FR79_A_connection_deleted_while_a_bind_waits_is_NOT_FOUND_not_a_500()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey);

        await using var held = await LockProjectAsync(fixture.Project.Id);
        var bind = ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", defaultModel = "gpt-5", credentialId = credential.Id });
        await WaitForLockWaiterAsync(bind);
        await ProjectApi.WithDbAsync(app, db => db.LlmCredentials.Where(c => c.Id == credential.Id).ExecuteDeleteAsync(Ct));
        await held.Transaction.CommitAsync(Ct);

        await Problems.AssertAsync(await bind, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Null((await SettingsAsync(app, fixture.Project.Id)).CredentialId);
    }

    [Fact]
    public async Task FR79_Demoting_an_Owner_who_did_not_bind_keeps_the_binding()
    {
        var app = App();
        using var fixture = await ProjectApi.FixtureAsync(app);
        using var other = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, fixture.Project.Id, other.User!.Id, ProjectRole.Owner);
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey);
        await BindAsync(fixture, "openai", credential.Id, "gpt-5");

        await ProjectApi.ReadAsync<MemberDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{other.User.Id}", new { role = "viewer" }));

        Assert.Equal(credential.Id, (await SettingsAsync(app, fixture.Project.Id)).CredentialId);
    }

    [Fact]
    public async Task FR79_System_keys_are_used_only_when_allowed_and_nothing_is_bound()
    {
        const string systemKey = "sk-proj-systemKeyFromConfig-k1l2";
        var forbidden = LlmStubs.App(factory, _anthropic, _openai, ("Llm:Providers:OpenAI:ApiKey", systemKey));
        var allowed = LlmStubs.App(factory, _anthropic, _openai, ("Llm:Providers:OpenAI:ApiKey", systemKey), ("Llm:AllowSystemKeys", "true"));
        using var fixture = await ProjectApi.FixtureAsync(allowed);
        const string completion = """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

        // Allowed and nothing bound: OpenAI is configured by the system key, and runs use it without a connection.
        var settings = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "openai", defaultModel = "gpt-5" }));
        Assert.True(settings.Providers.Single(p => p.Provider == LlmProvider.Openai).Configured);
        _openai.Enqueue(HttpStatusCode.OK, completion);
        var system = await RunAsync(allowed, Spec(fixture.Project.Id, NewJob(fixture.Project.Id)));
        Assert.Equal($"Bearer {systemKey}", _openai.Requests[^1].Headers["Authorization"]);
        Assert.Null((await RunRowAsync(allowed, system.RunId)).CredentialId);

        // Not allowed (the default): the same project's runs are not configured, and the provider isn't called.
        var calls = _openai.Requests.Count;
        var ex = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(forbidden, Spec(fixture.Project.Id, NewJob(fixture.Project.Id))));
        Assert.Equal(ErrorCodes.ProviderNotConfigured, ex.ErrorCode);
        Assert.Equal(calls, _openai.Requests.Count);

        // Allowed but bound: the binding wins.
        var credential = await LlmStubs.CreateAsync(fixture.Owner.Client, "openai", OwnerKey);
        await BindAsync(fixture, "openai", credential.Id, "gpt-5");
        _openai.Enqueue(HttpStatusCode.OK, completion);
        await RunAsync(allowed, Spec(fixture.Project.Id, NewJob(fixture.Project.Id)));
        Assert.Equal($"Bearer {OwnerKey}", _openai.Requests[^1].Headers["Authorization"]);
    }

    public void Dispose()
    {
        _anthropic.Dispose();
        _openai.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private sealed record HeldLock(NpgsqlConnection Connection, NpgsqlTransaction Transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>Takes the project row lock in a separate transaction (what member changes hold while they run).</summary>
    private async Task<HeldLock> LockProjectAsync(Guid projectId)
    {
        var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT 1 FROM projects WHERE id = @p FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("p", projectId);
        await command.ExecuteNonQueryAsync(Ct);
        return new HeldLock(connection, transaction);
    }

    /// <summary>Waits until the API request is blocked on the project row lock. Polls from its own connection: inside a
    /// transaction, pg_stat_activity is a snapshot that never changes.</summary>
    private async Task WaitForLockWaiterAsync(Task<HttpResponseMessage> request)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (request.IsCompleted)
            {
                var response = await request;
                Assert.Fail($"The request finished without waiting for the project lock: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
            }

            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FROM projects%FOR UPDATE%'",
                connection);
            if ((long)(await command.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The request never waited for the project lock.");
    }

    private WebApplicationFactory<Program> App() => LlmStubs.App(factory, _anthropic, _openai);

    private static async Task BindAsync(ProjectFixture fixture, string provider, Guid credentialId, string model, HttpClient? client = null)
    {
        var response = await ProjectApi.PatchAsync(client ?? fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider, defaultModel = model, credentialId });
        Assert.Equal(credentialId, (await ProjectApi.ReadAsync<LlmSettingsDto>(response)).Binding!.CredentialId);
    }

    private static Task<ProjectLlmSettings> SettingsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.ProjectLlmSettings.AsNoTracking().SingleAsync(s => s.ProjectId == projectId, Ct));

    private static Task<AgentRun> RunRowAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static JobExecution NewJob(Guid projectId) => new(Guid.CreateVersion7(), JobTypes.SpecGeneration, "{}", projectId, 1);

    private static AgentRunRequest Spec(Guid projectId, JobExecution job) => new()
    {
        ProjectId = projectId,
        Agent = AgentKind.SpecGenerator,
        Job = job,
        Inputs = new Dictionary<string, string?> { ["PROJECT_REQUEST"] = ProjectRequest },
        InputArtifacts = [new InputArtifactRef(Guid.CreateVersion7(), ArtifactType.ProjectRequest, 1)],
    };

    private static async Task<AgentRunOutcome> RunAsync(WebApplicationFactory<Program> app, AgentRunRequest request)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AgentRunner>().RunAsync(request, Ct);
    }
}
