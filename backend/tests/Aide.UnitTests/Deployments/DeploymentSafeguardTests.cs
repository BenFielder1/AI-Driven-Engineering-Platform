using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-88, FR-90, §9, NFR-SEC2: binding use by co-Owners, Vercel project id shapes, build-log masking, the Fake guard.</summary>
public class DeploymentSafeguardTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Ben = Guid.CreateVersion7();
    private static readonly Guid BensConnection = Guid.CreateVersion7();

    [Fact]
    public void FR88_A_co_Owner_cant_change_the_deployment_with_another_Owners_bound_connection()
    {
        Assert.Equal(ErrorCodes.CredentialNotOwner, DeploymentSettingsRules.CanUseBinding(Ben, Ana, null, turningOff: false).Error!.Code);
        Assert.Equal(403, ErrorCodes.StatusCodeFor(ErrorCodes.CredentialNotOwner));

        Assert.True(DeploymentSettingsRules.CanUseBinding(Ana, Ana, null, turningOff: false).IsSuccess);
        Assert.True(DeploymentSettingsRules.CanUseBinding(Ben, Ana, BensConnection, turningOff: false).IsSuccess);
        Assert.True(DeploymentSettingsRules.CanUseBinding(Ben, Ana, null, turningOff: true).IsSuccess);
        Assert.True(DeploymentSettingsRules.CanUseBinding(Ben, null, null, turningOff: false).IsSuccess);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("prj/abc")]
    [InlineData("prj\\abc")]
    [InlineData("../prj")]
    public void FR88_Vercel_project_ids_and_names_of_only_dots_or_with_separators_are_refused(string value)
    {
        Assert.False(DeploymentSettingsRules.IsVercelProjectId(value));
        Assert.False(DeploymentSettingsRules.IsVercelProjectName(value));
    }

    [Theory]
    [InlineData("prj_abc123")]
    [InlineData("todo.app")]
    [InlineData(".todo")]
    public void FR88_Ordinary_project_ids_are_accepted(string value) => Assert.True(DeploymentSettingsRules.IsVercelProjectId(value));

    [Fact]
    public void FR90_NFR_SEC2_Env_values_are_masked_from_4_characters_with_their_base64_and_url_encoded_forms()
    {
        const string value = "p@ss word";
        var log = string.Join('\n',
            $"plain {value}",
            $"b64 {Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}",
            $"b64-unpadded {Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=')}",
            $"url {Uri.EscapeDataString(value)}",
            $"form {System.Net.WebUtility.UrlEncode(value)}",
            "short abc stays, abcd goes");

        var masked = BuildLogMasking.MaskEnvValues(log, [value, "abc", "abcd", null]);

        Assert.DoesNotContain("p@ss", masked, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('='), masked, StringComparison.Ordinal);
        Assert.DoesNotContain("p%40ss", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd", masked, StringComparison.Ordinal);
        Assert.Contains("short abc stays", masked, StringComparison.Ordinal);
        Assert.Equal(6, masked.Split('\n').Length);
    }

    [Fact]
    public void FR90_NFR_SEC2_A_value_straddling_the_tail_cut_leaves_no_fragment()
    {
        const string value = "TOPSECRETVALUE";
        // The tail cut lands inside the value: its last 5 characters would remain if the tail were cut before masking.
        var log = new string('x', 100) + value + new string('y', Deployment.BuildLogTailMaxLength - 5);
        Assert.EndsWith("VALUE", Deployment.TailOf(log)[..5], StringComparison.Ordinal);

        var tail = Deployment.TailOf(BuildLogMasking.MaskEnvValues(log, [value]));

        Assert.DoesNotContain("VALUE", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("ALUE", tail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "Production", false, FakeProviderDecision.NotUsed)]
    [InlineData(true, "Development", false, FakeProviderDecision.Allowed)]
    [InlineData(true, "Testing", false, FakeProviderDecision.Allowed)]
    [InlineData(true, "Production", false, FakeProviderDecision.Refused)]
    [InlineData(true, "Staging", true, FakeProviderDecision.AllowedWithWarning)]
    public void S9_The_Fake_provider_runs_only_in_Development_Testing_or_with_AllowFake(bool useFake, string environment, bool allowFake, FakeProviderDecision expected) =>
        Assert.Equal(expected, FakeProviderPolicy.Decide(useFake, environment, allowFake));
}
