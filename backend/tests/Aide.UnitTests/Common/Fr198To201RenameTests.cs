using System.Reflection;
using System.Text.RegularExpressions;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.GitHub;
using Aide.Domain.Notifications;
using Aide.Domain.Releases;
using Aide.Domain.Stack;
using Aide.Infrastructure;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Security;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Common;

/// <summary>
/// 0.6.0 rename (§4.29): what people and Git hosts see says Tevrin (FR-198, FR-199), and the identifiers users already hold keep
/// their <c>aide</c> names (FR-201).
/// </summary>
public sealed partial class Fr198To201RenameTests
{
    /// <summary>
    /// The old product name as a word: not part of an identifier such as <c>AIDE_RUN_MIGRATIONS</c>, <c>X-AIDE-Signature</c>,
    /// <c>AIDE-Step-Id</c> or a path or host (<c>.aide/</c>, <c>aide.test</c>).
    /// </summary>
    [GeneratedRegex(@"(?<![-\w/.])AIDE(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex OldProductName();

    // ---------- FR-198: server-generated text ----------

    [Fact]
    public void FR198_An_email_subject_starts_with_Tevrin()
    {
        var subject = ChannelText.EmailSubject("Pre-release gate waiting: Todo API");

        Assert.StartsWith("[Tevrin] ", subject, StringComparison.Ordinal);
        Assert.Equal("[Tevrin] Pre-release gate waiting: Todo API", subject);
    }

    [Fact]
    public void FR198_Email_Slack_and_Teams_link_back_with_Open_in_Tevrin() =>
        Assert.Equal("Open in Tevrin", ChannelText.OpenLinkLabel);

    [Fact]
    public void FR198_A_new_GitHub_App_is_named_Tevrin_host() =>
        Assert.Equal("Tevrin (aide.example.com)", GitHubRules.ManifestAppName("aide.example.com"));

    [Fact]
    public void FR198_Pull_request_merge_and_tag_text_name_Tevrin()
    {
        Assert.Equal("[Tevrin] Todo API — Implementation (2 steps)", PullRequestDescription.Title("Todo API", 2));
        Assert.Equal("[Tevrin] Release v1.2.0: Todo API", ReleaseRules.MergeTitle("Todo API", "v1.2.0"));
        Assert.Equal("Tevrin release v1.2.0: Todo API (version 1.2.0)", ReleaseRules.TagMessage("Todo API", "v1.2.0", "1.2.0"));
        Assert.StartsWith("[Tevrin][Phase:execution][Step:1] ", CodeGeneration.CommitMessage(1, "Add the API", ["src/api.ts"], Guid.NewGuid()).Subject, StringComparison.Ordinal);
    }

    /// <summary>Every string constant the domain and infrastructure ship (messages, titles, names) is free of the old name.</summary>
    [Fact]
    public void FR198_No_shipped_string_constant_names_the_product_AIDE()
    {
        var offenders = new[] { typeof(ChannelText).Assembly, typeof(DependencyInjection).Assembly }
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (Field: $"{f.DeclaringType!.FullName}.{f.Name}", Value: (string)f.GetRawConstantValue()!))
            .Where(c => OldProductName().IsMatch(c.Value))
            .Select(c => c.Field)
            .ToList();

        Assert.Empty(offenders);
        Assert.Equal("The deployment didn't finish within 30 minutes; Tevrin stopped polling it.", DeploymentPollRules.TimeoutMessage);
    }

    // ---------- FR-199: agent prompts ----------

    [Theory]
    [InlineData(AgentKind.IdeaRefinement, "6")]
    [InlineData(AgentKind.SpecGenerator, "7")]
    [InlineData(AgentKind.PlanGenerator, "6")]
    [InlineData(AgentKind.CodeGenerator, "5")]
    [InlineData(AgentKind.CodeReview, "8")]
    [InlineData(AgentKind.RepositoryAnalyst, "3")]
    [InlineData(AgentKind.StackAnalyst, "3")]
    public void FR199_Each_rendered_system_prompt_names_Tevrin_not_AIDE_at_the_new_version(AgentKind agent, string version)
    {
        var template = new EmbeddedPromptLibrary().Template(agent);
        var rendered = template.Render(template.RequiredInputs.ToDictionary(n => n, n => (string?)$"<{n}>", StringComparer.Ordinal)).Value;

        Assert.Equal(version, template.Version);
        Assert.Contains("agent of Tevrin", rendered.System, StringComparison.Ordinal);
        Assert.DoesNotMatch(OldProductName(), rendered.System);
        Assert.DoesNotMatch(OldProductName(), rendered.User);
    }

    [Fact]
    public void FR199_A_specialist_prompt_composed_from_the_stack_catalog_names_Tevrin_not_AIDE()
    {
        var template = new EmbeddedPromptLibrary().Template(AgentKind.CodeGenerator);
        var basePrompt = template.Render(template.RequiredInputs.ToDictionary(n => n, n => (string?)$"<{n}>", StringComparer.Ordinal)).Value;
        var resolution = StackResolverTests.Resolve(StackResolverTests.AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render", databaseHost: "render-postgres"));

        Assert.NotEmpty(resolution.AgentKeys);
        foreach (var key in resolution.AgentKeys)
        {
            var composed = StackPrompt.Compose(basePrompt, resolution, key).Value;
            Assert.Contains("Tevrin", composed.System, StringComparison.Ordinal);
            Assert.DoesNotMatch(OldProductName(), composed.System);
        }
    }

    [Fact]
    public void FR199_No_stack_catalog_file_names_the_product_AIDE()
    {
        var offenders = StackTestCatalog.ShippedFiles().Where(f => OldProductName().IsMatch(f.Value)).Select(f => f.Key).ToList();

        Assert.Empty(offenders);
    }

    // ---------- FR-201: identifiers users already hold keep their names ----------

    [Fact]
    public void FR201_API_tokens_keep_the_aide_pat_prefix()
    {
        Assert.Equal("aide_pat_", ApiTokenRules.Prefix);
        Assert.StartsWith("aide_pat_", ApiTokenRules.Format(new byte[32]), StringComparison.Ordinal);
    }

    [Fact]
    public void FR201_Webhook_deliveries_keep_the_X_AIDE_headers() =>
        Assert.Equal(
            ["X-AIDE-Signature", "X-AIDE-Timestamp", "X-AIDE-Delivery", "X-AIDE-Event"],
            [WebhookSignature.HeaderName, WebhookSignature.TimestampHeaderName, WebhookSignature.DeliveryHeaderName, WebhookSignature.EventHeaderName]);

    [Fact]
    public void FR201_Stored_formats_branches_and_paths_keep_their_aide_names()
    {
        Assert.Equal("aide/todo-api/v1.2.0", ReleaseVersionRules.BranchName("todo-api", Guid.NewGuid(), SemVer.Parse("1.2.0")));
        Assert.Equal(".aide/", PathRules.AideDirectory);
        Assert.Equal("aide-plan", PlanDocument.BlockLanguage);
        Assert.Equal("aide-stack-profile", StackProfile.FenceTag);
        Assert.Equal("aide_merges", MergeStrategy.AideMerges.ToWire());
        Assert.Equal("AIDE-Step-Id", CodeGeneration.StepTrailer);
        Assert.Equal("AIDE_RUN_MIGRATIONS", DerivedEnvRules.RunMigrations);
    }

    [Fact]
    public void FR201_Server_identifiers_keep_their_aide_names()
    {
        Assert.Equal("aide", DataProtectionSetup.DefaultApplicationName);
        Assert.Equal("Aide", DependencyInjection.ConnectionStringName);
    }
}
