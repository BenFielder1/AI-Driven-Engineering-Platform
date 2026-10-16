using System.Net;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Projects;

/// <summary>FR-9: persisted, encrypted Git, LLM and notification settings (D10). Secrets are only set / not set.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectSettingsTests(AideApiFactory factory)
{
    private const string GitToken = "ghp_settingsSecretToken0123456789";
    private const string HmacSecret = "whsec_signingSecret0123456789";
    private const string SlackUrl = "https://hooks.slack.com/services/T0000/B0000/slackSecretPath0123";
    private const string SlackUrl2 = "https://hooks.slack.com/services/T0000/B0000/slackSecretPath4567";
    private const string TeamsUrl = "https://acme.webhook.office.com/webhookb2/teamsSecretPath0123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ Git

    [Fact]
    public async Task FR9_TokenNeverReturned()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var patch = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new
        {
            provider = "github_pat",
            repoUrl = "https://github.com/acme/widgets",
            defaultBranch = "trunk",
            token = GitToken,
        });
        var patchText = await patch.Content.ReadAsStringAsync(Ct);
        var get = await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct);
        var getText = await get.Content.ReadAsStringAsync(Ct);
        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(get);

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(new GitSettingsDto(GitProvider.GithubPat, "https://github.com/acme/widgets", "trunk", null, true, GitProvider.GithubPat, null, null, null, null, false), settings.Git);
        Assert.DoesNotContain(GitToken, patchText, StringComparison.Ordinal);
        Assert.DoesNotContain(GitToken, getText, StringComparison.Ordinal);
        await AssertStoredEncryptedAsync(fixture.Project.Id, GitToken, db => db.ProjectGitSettings.Where(s => s.ProjectId == fixture.Project.Id).Select(s => s.TokenEncrypted));
        await AssertSecretNowhereAsync(fixture.Project.Id, GitToken);
    }

    [Fact]
    public async Task FR9_Git_partial_update_keeps_the_token_and_clearToken_removes_it()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { provider = "github_pat", repoUrl = "https://github.com/acme/widgets.git", token = GitToken });

        var renamed = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { defaultBranch = "release" }));
        var cleared = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { clearToken = true }));
        var local = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { provider = "local" }));

        Assert.True(renamed.HasToken);
        Assert.Equal(("release", "https://github.com/acme/widgets.git"), (renamed.DefaultBranch, renamed.RepoUrl));
        Assert.False(cleared.HasToken);
        Assert.Equal((GitProvider.Local, (string?)null), (local.Provider, local.RepoUrl));
        await ProjectApi.WithDbAsync(factory, async db =>
            Assert.Equal(4, await db.Events.CountAsync(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.SettingsUpdated, Ct)));
    }

    [Fact]
    public async Task FR9_Switching_the_git_provider_to_local_drops_the_token_unless_a_new_one_is_sent()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { provider = "github_pat", repoUrl = "https://github.com/acme/widgets", token = GitToken });

        var local = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { provider = "local" }));
        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct));

        Assert.Equal((GitProvider.Local, false), (local.Provider, local.HasToken));
        Assert.False(settings.Git.HasToken);
        await ProjectApi.WithDbAsync(factory, async db =>
            Assert.Null(await db.ProjectGitSettings.Where(s => s.ProjectId == fixture.Project.Id).Select(s => s.TokenEncrypted).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData("""{"provider":"github_pat"}""", "repoUrl")]
    [InlineData("""{"provider":"github_pat","repoUrl":"http://github.com/a/b"}""", "repoUrl")]
    [InlineData("""{"provider":"github_pat","repoUrl":"https://github.com/a/b/tree/main"}""", "repoUrl")]
    [InlineData("""{"provider":"github_pat","repoUrl":"https://user:pw@github.com/a/b"}""", "repoUrl")]
    [InlineData("""{"defaultBranch":"-rf"}""", "defaultBranch")]
    [InlineData("""{"defaultBranch":"a b"}""", "defaultBranch")]
    [InlineData("""{"token":"x","clearToken":true}""", "token")]
    [InlineData("""{"token":""}""", "token")]
    [InlineData("""{"provider":"gitlab"}""", null)]
    public async Task FR9_Invalid_git_settings_are_VALIDATION(string json, string? field)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PatchAsync(
            new Uri($"{fixture.Path}/settings/git", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
        }
    }

    [Fact]
    public async Task FR9_Settings_are_Owner_only()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Problems.AssertAsync(await fixture.Contributor.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Viewer.Client, $"{fixture.Path}/settings/git", new { defaultBranch = "x" }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Contributor.Client, $"{fixture.Path}/settings/llm", new { provider = "ollama" }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Contributor.Client, $"{fixture.Path}/settings/notifications", new { channels = Array.Empty<object>() }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ------------------------------------------------------------------ LLM (keys and bindings: Connections/LlmBindingTests)

    [Fact]
    public async Task FR9_LLM_models_are_replaced_and_switching_provider_resets_the_models()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var models = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new
        {
            defaultModel = "fake-2",
            modelsByAgent = new { codeGenerator = "fake-3", codeReview = "fake-3" },
        }));
        var ollama = await ProjectApi.ReadAsync<LlmSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/llm", new { provider = "ollama" }));

        Assert.Equal(("fake-2", "fake-3", "fake-3", (string?)null, false), (models.DefaultModel, models.ModelsByAgent.CodeGenerator, models.ModelsByAgent.CodeReview, models.ModelsByAgent.IdeaRefinement, models.HasApiKey));
        Assert.Equal((LlmProvider.Ollama, "qwen2.5-coder", false, (LlmBindingDto?)null), (ollama.Provider, ollama.DefaultModel, ollama.HasApiKey, ollama.Binding));
        Assert.Null(ollama.ModelsByAgent.CodeGenerator);
        Assert.False(ollama.Providers.Single(p => p.Provider == LlmProvider.Openai).Configured);
    }

    [Theory]
    [InlineData("""{"provider":"anthropic"}""", "provider")]
    [InlineData("""{"provider":"openai"}""", "provider")]
    [InlineData("""{"modelsByAgent":{"codeReview":"  "}}""", "modelsByAgent.codeReview")]
    [InlineData("""{"defaultModel":""}""", null)]
    [InlineData("""{"provider":"copilot"}""", null)]
    [InlineData("""{"credentialId":"not-a-guid"}""", null)]
    public async Task FR9_Invalid_LLM_settings_are_VALIDATION(string json, string? field)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PatchAsync(
            new Uri($"{fixture.Path}/settings/llm", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
        }
    }

    // ------------------------------------------------------------------ Notifications

    [Fact]
    public async Task FR9_Notification_channels_are_replaced_and_secrets_kept_by_id()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var path = $"{fixture.Path}/settings/notifications";

        var first = await ProjectApi.ReadAsync<NotificationSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, path, new
        {
            channels = new object[]
            {
                new { type = "email", target = "team@example.com", events = (string[])["gate_required", "gate_required", "release_completed"] },
                new { type = "webhook", target = "https://hooks.example.com/aide", secret = HmacSecret, events = (string[])["run_failed"] },
            },
        }));
        var webhook = first.Channels.Single(c => c.Type == NotificationChannelType.Webhook);
        var kept = await ProjectApi.ReadAsync<NotificationSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, path, new
        {
            channels = new object[] { new { id = webhook.Id, type = "webhook", target = "https://hooks.example.com/v2", events = (string[])["rollback"] } },
        }));
        // NFR-SEC: webhook bodies are always signed, so the signing secret can be replaced but not cleared.
        var cleared = await ProjectApi.PatchAsync(fixture.Owner.Client, path, new
        {
            channels = new object[] { new { id = webhook.Id, type = "webhook", target = "https://hooks.example.com/v2", clearSecret = true, events = (string[])["rollback"] } },
        });
        var clearedProblem = await Problems.AssertAsync(cleared, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(clearedProblem.GetProperty("errors").TryGetProperty("channels[0].secret", out _), clearedProblem.ToString());

        var email = first.Channels.Single(c => c.Type == NotificationChannelType.Email);
        Assert.Equal([NotificationEvent.GateRequired, NotificationEvent.ReleaseCompleted], email.Events);
        Assert.False(email.HasSecret);
        Assert.True(webhook.HasSecret);
        var keptChannel = Assert.Single(kept.Channels);
        Assert.Equal((webhook.Id, "https://hooks.example.com/v2", true), (keptChannel.Id, keptChannel.Target, keptChannel.HasSecret));
        await AssertSecretNowhereAsync(fixture.Project.Id, HmacSecret);
    }

    [Fact]
    public async Task FR9_Slack_and_Teams_webhook_URLs_are_encrypted_secrets_returned_masked()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var path = $"{fixture.Path}/settings/notifications";

        var created = await ProjectApi.PatchAsync(fixture.Owner.Client, path, new
        {
            channels = new object[]
            {
                new { type = "slack", secret = SlackUrl, events = (string[])["gate_required"] },
                new { type = "teams", target = TeamsUrl, events = (string[])["run_failed"] }, // older clients: URL as target
            },
        });
        var createdText = await created.Content.ReadAsStringAsync(Ct);
        var first = await ProjectApi.ReadAsync<NotificationSettingsDto>(created);
        var getText = await (await fixture.Owner.Client.GetAsync(new Uri($"{fixture.Path}/settings", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        var slack = first.Channels.Single(c => c.Type == NotificationChannelType.Slack);
        var teams = first.Channels.Single(c => c.Type == NotificationChannelType.Teams);
        Assert.Equal(("hooks.slack.com/…", true), (slack.Target, slack.HasSecret));
        Assert.Equal(("acme.webhook.office.com/…", true), (teams.Target, teams.HasSecret));
        foreach (var text in new[] { createdText, getText })
        {
            Assert.DoesNotContain("slackSecretPath", text, StringComparison.Ordinal);
            Assert.DoesNotContain("teamsSecretPath", text, StringComparison.Ordinal);
        }

        Assert.Equal([SlackUrl, TeamsUrl], await StoredChannelSecretsAsync(fixture.Project.Id));

        // Kept by id (the masked target echoed back), then replaced with a new URL.
        var kept = await ProjectApi.ReadAsync<NotificationSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, path, new
        {
            channels = new object[]
            {
                new { id = slack.Id, type = "slack", target = slack.Target, events = (string[])["gate_required"] },
                new { id = teams.Id, type = "teams", events = (string[])["run_failed"] },
            },
        }));
        Assert.All(kept.Channels, c => Assert.True(c.HasSecret));
        Assert.Equal([SlackUrl, TeamsUrl], await StoredChannelSecretsAsync(fixture.Project.Id));

        var replaced = await ProjectApi.ReadAsync<NotificationSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, path, new
        {
            channels = new object[] { new { id = slack.Id, type = "slack", secret = SlackUrl2, events = (string[])["gate_required"] } },
        }));
        Assert.Equal(slack.Id, Assert.Single(replaced.Channels).Id);
        Assert.Equal([SlackUrl2], await StoredChannelSecretsAsync(fixture.Project.Id));

        await AssertSecretNowhereAsync(fixture.Project.Id, "slackSecretPath");
        await AssertSecretNowhereAsync(fixture.Project.Id, "teamsSecretPath");
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var channels = await db.ProjectNotificationSettings.Where(s => s.ProjectId == fixture.Project.Id).Select(s => s.Channels).SingleAsync(Ct);
            Assert.DoesNotContain(channels, c => c.Target.Contains("SecretPath", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("""{"channels":[{"type":"slack","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"type":"slack","target":"hooks.slack.com/…","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"type":"teams","secret":"http://x.webhook.office.com/a","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"type":"email","target":"a@example.com","secret":"s","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"type":"webhook","events":[]}]}""", "channels[0].target")]
    public async Task FR9_Slack_and_Teams_need_an_https_webhook_URL(string json, string field)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PatchAsync(
            new Uri($"{fixture.Path}/settings/notifications", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
    }

    [Theory]
    [InlineData("""{"channels":[{"type":"email","target":"not-an-email","events":[]}]}""", "channels[0].target")]
    [InlineData("""{"channels":[{"type":"slack","target":"http://hooks.slack.com/x","events":[]}]}""", "channels[0].target")]
    [InlineData("""{"channels":[{"type":"webhook","target":"http://localhost:9000/hook","events":[]}]}""", "channels[0].target")]
    [InlineData("""{"channels":[{"type":"teams","target":"https://x.webhook.office.com/a","secret":"s","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"id":"0199aaaa-0000-7000-8000-000000000000","type":"email","target":"a@example.com","events":[]}]}""", "channels[0].id")]
    [InlineData("""{"channels":[{"type":"webhook","target":"https://h.example.com","secret":" ","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"type":"webhook","target":"https://h.example.com","events":[]}]}""", "channels[0].secret")]
    [InlineData("""{"channels":[{"type":"email","target":"a@example.com","events":["everything"]}]}""", null)]
    [InlineData("""{"channels":[{"type":"sms","target":"+44","events":[]}]}""", null)]
    public async Task FR9_Invalid_notification_channels_are_VALIDATION(string json, string? field)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PatchAsync(
            new Uri($"{fixture.Path}/settings/notifications", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
        }
    }

    private async Task AssertStoredEncryptedAsync(Guid projectId, string secret, Func<Aide.Infrastructure.Persistence.AideDbContext, IQueryable<string?>> select)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Aide.Infrastructure.Persistence.AideDbContext>();
        var stored = await select(db).SingleAsync(Ct);
        Assert.NotNull(stored);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.Equal(secret, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(stored));
        _ = projectId;
    }

    /// <summary>The decrypted channel secrets of a project, in channel order.</summary>
    private async Task<List<string?>> StoredChannelSecretsAsync(Guid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Aide.Infrastructure.Persistence.AideDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var channels = await db.ProjectNotificationSettings.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.Channels).SingleAsync(Ct);
        return [.. channels.Select(c => c.SecretEncrypted is null ? null : protector.Unprotect(c.SecretEncrypted))];
    }

    /// <summary>NFR-SEC: the secret is in no log entry and no event payload.</summary>
    private async Task AssertSecretNowhereAsync(Guid projectId, string secret)
    {
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(secret, StringComparison.Ordinal));
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var payloads = await db.Events.Where(e => e.ProjectId == projectId).Select(e => e.Payload).ToListAsync(Ct);
            Assert.NotEmpty(payloads);
            Assert.DoesNotContain(payloads, p => p.Contains(secret, StringComparison.Ordinal));
            var channels = await db.ProjectNotificationSettings.Where(s => s.ProjectId == projectId).Select(s => s.Channels).SingleAsync(Ct);
            Assert.DoesNotContain(channels, c => c.SecretEncrypted != null && c.SecretEncrypted.Contains(secret, StringComparison.Ordinal));
        });
    }
}
