using Aide.Domain.Entities;
using Aide.Domain.Projects;
using static Aide.UnitTests.Projects.ProjectRulesTests;

namespace Aide.UnitTests.Projects;

/// <summary>FR-9, FR-10, FR-22, FR-47: Git, LLM and notification settings rules.</summary>
public class ProjectSettingsRulesTests
{
    private const string SlackUrl = "https://hooks.slack.com/services/T000/B000/XXXXSECRET";

    // ------------------------------------------------------------------ Git

    [Theory]
    [InlineData("https://github.com/acme/widgets", "acme", "widgets")]
    [InlineData("https://github.com/acme/widgets.git", "acme", "widgets")]
    [InlineData("https://github.com/acme/widgets/", "acme", "widgets")]
    [InlineData("https://github.com/a-b/my.repo_x", "a-b", "my.repo_x")]
    public void FR9_GitHub_repo_URL_is_parsed(string url, string owner, string name)
    {
        Assert.True(ProjectSettingsRules.TryParseGitHubRepo(url, out var repo));
        Assert.Equal(new GitHubRepo(owner, name), repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://github.com/acme/widgets")]
    [InlineData("https://gitlab.com/acme/widgets")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/widgets/tree/main")]
    [InlineData("https://user:pw@github.com/acme/widgets")]
    [InlineData("https://github.com/acme/widgets?x=1")]
    [InlineData("https://github.com/-acme/widgets")]
    [InlineData("https://github.com/ac--me/widgets")]
    [InlineData("https://github.com/acme/..")]
    [InlineData("https://github.com/acme/.git")]
    public void FR9_Non_GitHub_repo_URLs_are_rejected(string? url) =>
        Assert.False(ProjectSettingsRules.TryParseGitHubRepo(url, out _));

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.2")]
    [InlineData("feature/x-y_z")]
    public void FR9_Valid_branch_names(string name) => Assert.True(ProjectSettingsRules.IsValidBranchName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("-rf")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("main.")]
    [InlineData("main.lock")]
    [InlineData("a..b")]
    [InlineData("a@{b")]
    [InlineData("a//b")]
    [InlineData("@")]
    [InlineData("a/.hidden")]
    [InlineData("a~1")]
    [InlineData("a^")]
    [InlineData("a:b")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("a[b")]
    [InlineData("a\\b")]
    public void FR9_Unsafe_branch_names_are_rejected(string? name) => Assert.False(ProjectSettingsRules.IsValidBranchName(name));

    [Fact]
    public void FR9_Branch_name_is_at_most_255_characters()
    {
        Assert.True(ProjectSettingsRules.IsValidBranchName(new string('a', 255)));
        Assert.False(ProjectSettingsRules.IsValidBranchName(new string('a', 256)));
    }

    [Fact]
    public void FR9_ValidateGit_github_needs_a_GitHub_URL_and_local_needs_none()
    {
        Assert.True(ProjectSettingsRules.ValidateGit(GitProvider.Local, null, "main").IsSuccess);
        Assert.True(ProjectSettingsRules.ValidateGit(GitProvider.Github, "https://github.com/a/b", "main").IsSuccess);
        AssertProblem(ProjectSettingsRules.ValidateGit(GitProvider.Github, null, "main"), "repoUrl");
        AssertProblem(ProjectSettingsRules.ValidateGit(GitProvider.Github, "https://example.com/a/b", "main", prefix: "git."), "git.repoUrl");
    }

    [Fact]
    public void FR9_ValidateGit_branch_and_token_rules()
    {
        AssertProblem(ProjectSettingsRules.ValidateGit(GitProvider.Local, null, "a b"), "defaultBranch");
        AssertProblem(ProjectSettingsRules.ValidateGit(GitProvider.Local, null, "main", tokenSent: "t", clearToken: true), "token");
        AssertProblem(ProjectSettingsRules.ValidateGit(GitProvider.Local, null, "main", tokenSent: "  "), "token");
        Assert.True(ProjectSettingsRules.ValidateGit(GitProvider.Local, null, "main", clearToken: true).IsSuccess);
    }

    [Fact]
    public void FR9_NormalizeRepoUrl_trims_for_github_and_drops_it_for_local()
    {
        Assert.Equal("https://github.com/a/b", ProjectSettingsRules.NormalizeRepoUrl(GitProvider.Github, "  https://github.com/a/b "));
        Assert.Null(ProjectSettingsRules.NormalizeRepoUrl(GitProvider.Local, "https://github.com/a/b"));
        Assert.Null(ProjectSettingsRules.NormalizeRepoUrl(GitProvider.Github, " "));
    }

    [Theory]
    [InlineData(GitProvider.Github, GitProvider.Github, false, true)]
    [InlineData(GitProvider.Local, GitProvider.Github, false, true)]
    [InlineData(GitProvider.Local, GitProvider.Local, false, true)]
    [InlineData(GitProvider.Github, GitProvider.Local, false, false)]
    [InlineData(GitProvider.Github, GitProvider.Github, true, false)]
    public void FR9_Saved_token_is_dropped_when_cleared_or_switching_to_local(GitProvider previous, GitProvider next, bool clear, bool keeps) =>
        Assert.Equal(keeps, ProjectSettingsRules.KeepsSavedToken(previous, next, clear));

    // ------------------------------------------------------------------ LLM

    [Theory]
    [InlineData(LlmProvider.Anthropic, true)]
    [InlineData(LlmProvider.Openai, true)]
    [InlineData(LlmProvider.Ollama, false)]
    [InlineData(LlmProvider.Fake, false)]
    public void FR9_Only_hosted_providers_need_an_API_key(LlmProvider provider, bool needsKey) =>
        Assert.Equal(needsKey, ProjectSettingsRules.RequiresApiKey(provider));

    [Theory]
    [InlineData(LlmProvider.Anthropic, false, false, false)]
    [InlineData(LlmProvider.Anthropic, true, false, true)]
    [InlineData(LlmProvider.Openai, false, true, true)]
    [InlineData(LlmProvider.Ollama, false, false, true)]
    [InlineData(LlmProvider.Fake, false, false, true)]
    public void FR9_Provider_is_configured_with_a_system_or_project_key(LlmProvider provider, bool systemKey, bool projectKey, bool configured) =>
        Assert.Equal(configured, ProjectSettingsRules.IsConfigured(provider, systemKey, projectKey));

    [Fact]
    public void FR9_New_projects_fall_back_to_Fake_when_the_default_has_no_system_key()
    {
        Assert.Equal(LlmProvider.Fake, ProjectSettingsRules.DefaultProviderForNewProject(LlmProvider.Anthropic, defaultHasSystemKey: false));
        Assert.Equal(LlmProvider.Anthropic, ProjectSettingsRules.DefaultProviderForNewProject(LlmProvider.Anthropic, defaultHasSystemKey: true));
        Assert.Equal(LlmProvider.Ollama, ProjectSettingsRules.DefaultProviderForNewProject(LlmProvider.Ollama, defaultHasSystemKey: false));
    }

    [Fact]
    public void FR9_ValidateLlm_accepts_a_configured_provider_with_models() =>
        Assert.True(Llm(LlmProvider.Fake, "fake-1", new Dictionary<string, string?> { ["codeReview"] = "fake-2", ["planGenerator"] = null }).IsSuccess);

    [Fact]
    public void FR9_ValidateLlm_model_rules()
    {
        AssertProblem(Llm(LlmProvider.Fake, " "), "defaultModel");
        AssertProblem(Llm(LlmProvider.Fake, "fake-1", new Dictionary<string, string?> { ["codeReview"] = " " }), "modelsByAgent.codeReview");
    }

    [Fact]
    public void FR79_ValidateLlm_choosing_a_keyed_provider_needs_a_binding_or_an_allowed_system_key()
    {
        AssertProblem(Llm(LlmProvider.Anthropic, "m", providerChanged: true), "provider");
        Assert.True(Llm(LlmProvider.Anthropic, "m", hasBindingAfter: true, providerChanged: true).IsSuccess);
        Assert.True(Llm(LlmProvider.Anthropic, "m", hasSystemKey: true, providerChanged: true).IsSuccess);
        Assert.True(Llm(LlmProvider.Ollama, "m", providerChanged: true).IsSuccess);
    }

    [Fact]
    public void FR79_ValidateLlm_allows_unbinding_or_editing_models_on_the_same_provider() =>
        Assert.True(Llm(LlmProvider.Anthropic, "m", hasBindingAfter: false, providerChanged: false).IsSuccess);

    [Fact]
    public void FR9_ValidateLlm_unconfigured_provider_is_refused()
    {
        AssertProblem(Llm(LlmProvider.Openai, "gpt-5", providerChanged: true), "provider");
        Assert.True(Llm(LlmProvider.Openai, "gpt-5", hasSystemKey: true, providerChanged: true).IsSuccess);
    }

    // ------------------------------------------------------------------ Notifications

    [Fact]
    public void FR9_Email_and_webhook_channels_validate_their_target()
    {
        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Email, "team@example.com")).IsSuccess);
        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com/x", "hmac")).IsSuccess);
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Email, "not-an-email")), "channels[0].target");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Email, null)), "channels[0].target");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Email, "a@example.com", "s")), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "http://h.example.com")), "channels[0].target");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://u:p@h.example.com")), "channels[0].target");
    }

    [Fact]
    public void FR47_NFR_SEC_Webhook_channels_need_a_signing_secret_sent_or_kept_by_id()
    {
        var saved = new SavedChannel(NotificationChannelType.Webhook, "https://h.example.com", HasSecret: true);
        var savedWithout = new SavedChannel(NotificationChannelType.Webhook, "https://h.example.com", HasSecret: false);
        var fromSlack = new SavedChannel(NotificationChannelType.Slack, "hooks.slack.com/…", HasSecret: true);

        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", "k")).IsSuccess);
        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", Saved: saved)).IsSuccess);
        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", "new", Saved: saved)).IsSuccess);
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com")), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", ClearSecret: true, Saved: saved)), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", Saved: savedWithout)), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", Saved: fromSlack)), "channels[0].secret");
    }

    [Fact]
    public void FR9_Secret_cannot_be_blank_or_sent_with_clearSecret()
    {
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", " ")), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", "s", ClearSecret: true)), "channels[0].secret");
    }

    [Theory]
    [InlineData(NotificationChannelType.Slack)]
    [InlineData(NotificationChannelType.Teams)]
    public void FR9_Slack_and_Teams_URL_is_a_secret_https_URL(NotificationChannelType type)
    {
        Assert.True(Channels(new ChannelCandidate(type, null, SlackUrl)).IsSuccess);
        Assert.True(Channels(new ChannelCandidate(type, SlackUrl)).IsSuccess); // older clients send it as the target
        AssertProblem(Channels(new ChannelCandidate(type, null, "s")), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(type, null, "http://hooks.slack.com/x")), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(type, "http://hooks.slack.com/x")), "channels[0].target");
        AssertProblem(Channels(new ChannelCandidate(type, null)), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(type, "hooks.slack.com/…")), "channels[0].secret");
    }

    [Fact]
    public void FR9_Slack_URL_is_kept_by_id_only_for_the_same_type_and_not_when_cleared()
    {
        var saved = new SavedChannel(NotificationChannelType.Slack, "hooks.slack.com/…", HasSecret: true);

        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Slack, "hooks.slack.com/…", Saved: saved)).IsSuccess);
        Assert.True(Channels(new ChannelCandidate(NotificationChannelType.Slack, null, Saved: saved)).IsSuccess);
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Slack, null, ClearSecret: true, Saved: saved)), "channels[0].secret");
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Teams, null, Saved: saved)), "channels[0].secret");
        var webhook = new SavedChannel(NotificationChannelType.Webhook, "https://h.example.com", HasSecret: true);
        AssertProblem(Channels(new ChannelCandidate(NotificationChannelType.Slack, null, Saved: webhook)), "channels[0].secret");
    }

    [Fact]
    public void FR9_Slack_plan_stores_the_URL_as_a_secret_and_only_a_masked_target()
    {
        var sent = ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Slack, "ignored", $" {SlackUrl} "));
        var legacy = ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Teams, "https://acme.webhook.office.com/webhookb2/abc"));
        var kept = ProjectSettingsRules.PlanChannel(new ChannelCandidate(
            NotificationChannelType.Slack, "hooks.slack.com/…", Saved: new SavedChannel(NotificationChannelType.Slack, "hooks.slack.com/…", true)));

        Assert.Equal(new ChannelPlan("hooks.slack.com/…", ChannelSecretChange.Set, SlackUrl), sent);
        Assert.Equal(new ChannelPlan("acme.webhook.office.com/…", ChannelSecretChange.Set, "https://acme.webhook.office.com/webhookb2/abc"), legacy);
        Assert.Equal(new ChannelPlan("hooks.slack.com/…", ChannelSecretChange.Keep, null), kept);
        Assert.DoesNotContain("XXXXSECRET", sent.StoredTarget, StringComparison.Ordinal);
    }

    [Fact]
    public void FR9_Webhook_and_email_plans()
    {
        var saved = new SavedChannel(NotificationChannelType.Webhook, "https://h.example.com", HasSecret: true);

        Assert.Equal(new ChannelPlan("a@example.com", ChannelSecretChange.Clear, null), ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Email, " a@example.com ")));
        Assert.Equal(new ChannelPlan("https://h.example.com", ChannelSecretChange.Set, "k"), ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", "k")));
        Assert.Equal(ChannelSecretChange.Keep, ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", Saved: saved)).Secret);
        Assert.Equal(ChannelSecretChange.Clear, ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", ClearSecret: true, Saved: saved)).Secret);
        Assert.Equal(ChannelSecretChange.Clear, ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com")).Secret);
        var fromSlack = new SavedChannel(NotificationChannelType.Slack, "hooks.slack.com/…", HasSecret: true);
        Assert.Equal(ChannelSecretChange.Clear, ProjectSettingsRules.PlanChannel(new ChannelCandidate(NotificationChannelType.Webhook, "https://h.example.com", Saved: fromSlack)).Secret);
    }

    [Theory]
    [InlineData(SlackUrl, "hooks.slack.com/…")]
    [InlineData("https://Acme.Webhook.Office.com/a?b=c", "acme.webhook.office.com/…")]
    [InlineData("hooks.slack.com/…", "hooks.slack.com/…")]
    public void FR9_MaskUrl_keeps_only_the_host(string value, string masked) =>
        Assert.Equal(masked, ProjectSettingsRules.MaskUrl(value));

    [Fact]
    public void FR9_Errors_are_reported_per_channel_index()
    {
        var result = Channels(
            new ChannelCandidate(NotificationChannelType.Email, "ok@example.com"),
            new ChannelCandidate(NotificationChannelType.Slack, null));

        AssertProblem(result, "channels[1].secret");
        Assert.Single(result.Error!.Details);
    }

    private static Aide.Domain.Common.Result Channels(params ChannelCandidate[] channels) => ProjectSettingsRules.ValidateChannels(channels);

    private static Aide.Domain.Common.Result Llm(
        LlmProvider provider,
        string? defaultModel,
        IReadOnlyDictionary<string, string?>? models = null,
        bool hasSystemKey = false,
        bool hasBindingAfter = false,
        bool providerChanged = false) =>
        ProjectSettingsRules.ValidateLlm(provider, defaultModel, models, hasSystemKey, hasBindingAfter, providerChanged);
}
