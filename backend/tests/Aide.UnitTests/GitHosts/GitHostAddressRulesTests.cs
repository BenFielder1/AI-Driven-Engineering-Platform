using System.Net;
using Aide.Domain.Common;
using Aide.Domain.GitHosts;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-130, §8 (SSRF guard): GitLab base URL validation and address classification.</summary>
public class GitHostAddressRulesTests
{
    [Fact]
    public void FR130_Metadata_IP_base_URL_is_GIT_HOST_NOT_ALLOWED()
    {
        var result = GitHostAddressRules.NormalizeBaseUrl("https://169.254.169.254", allowInsecureHttp: false, allowPrivateNetworks: true);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.GitHostNotAllowed, result.Error!.Code);
        Assert.Equal(400, ErrorCodes.StatusCodeFor(ErrorCodes.GitHostNotAllowed));
    }

    [Theory]
    [InlineData("169.254.169.254", GitHostAddressClass.Metadata)]
    [InlineData("169.254.10.1", GitHostAddressClass.LinkLocal)]
    [InlineData("127.0.0.1", GitHostAddressClass.Loopback)]
    [InlineData("127.10.0.1", GitHostAddressClass.Loopback)]
    [InlineData("::1", GitHostAddressClass.Loopback)]
    [InlineData("fe80::1", GitHostAddressClass.LinkLocal)]
    [InlineData("febf::1", GitHostAddressClass.LinkLocal)]
    [InlineData("fd00:ec2::254", GitHostAddressClass.Metadata)]
    [InlineData("0.0.0.0", GitHostAddressClass.Unspecified)]
    [InlineData("::", GitHostAddressClass.Unspecified)]
    [InlineData("::ffff:169.254.169.254", GitHostAddressClass.Metadata)]
    [InlineData("::ffff:127.0.0.1", GitHostAddressClass.Loopback)]
    [InlineData("100.100.100.200", GitHostAddressClass.Reserved)]
    [InlineData("224.0.0.1", GitHostAddressClass.Reserved)]
    public void S8_Always_refused_even_with_private_networks_allowed(string address, GitHostAddressClass expected)
    {
        foreach (var allowPrivate in new[] { false, true })
        {
            var decision = GitHostAddressRules.Classify(IPAddress.Parse(address), allowPrivate);

            Assert.False(decision.Allowed);
            Assert.Equal(expected, decision.Class);
            Assert.False(string.IsNullOrEmpty(decision.Reason));
        }
    }

    [Theory]
    [InlineData("10.0.4.7")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.65.3")]
    [InlineData("fd12:3456::1")]
    [InlineData("fc00::5")]
    [InlineData("::ffff:10.0.4.7")]
    public void FR130_Private_ranges_are_allowed_only_with_AllowPrivateNetworks(string address)
    {
        var ip = IPAddress.Parse(address);

        Assert.True(GitHostAddressRules.Classify(ip, allowPrivateNetworks: true).Allowed);
        var refused = GitHostAddressRules.Classify(ip, allowPrivateNetworks: false);
        Assert.False(refused.Allowed);
        Assert.Equal(GitHostAddressClass.Private, refused.Class);
    }

    [Theory]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void S8_Public_addresses_are_allowed(string address)
    {
        var decision = GitHostAddressRules.Classify(IPAddress.Parse(address), allowPrivateNetworks: false);

        Assert.True(decision.Allowed);
        Assert.Equal(GitHostAddressClass.Public, decision.Class);
    }

    [Fact]
    public void FR130_gitlab_internal_resolving_to_10_0_4_7_is_accepted_on_Docker_Desktop_and_refused_on_Railway()
    {
        var url = GitHostAddressRules.NormalizeBaseUrl("https://gitlab.internal.example/", false, allowPrivateNetworks: false);
        Assert.True(url.IsSuccess);
        Assert.Equal("https://gitlab.internal.example/", url.Value.ToString());

        IPAddress[] resolved = [IPAddress.Parse("10.0.4.7")];
        Assert.True(GitHostAddressRules.CheckResolved(resolved, allowPrivateNetworks: true).Allowed);
        Assert.False(GitHostAddressRules.CheckResolved(resolved, allowPrivateNetworks: false).Allowed);
    }

    [Fact]
    public void S8_DNS_rebinding_guard_refuses_when_any_resolved_address_is_refused_or_none_resolved()
    {
        IPAddress[] mixed = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1")];

        var decision = GitHostAddressRules.CheckResolved(mixed, allowPrivateNetworks: true);

        Assert.False(decision.Allowed);
        Assert.Equal(GitHostAddressClass.Loopback, decision.Class);
        Assert.False(GitHostAddressRules.CheckResolved([], allowPrivateNetworks: true).Allowed);
    }

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://[fd00:ec2::254]")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://localhost")]
    [InlineData("https://gitlab.localhost:8443")]
    [InlineData("https://[::ffff:169.254.169.254]")]
    public void S8_Refused_literal_and_loopback_hosts_are_GIT_HOST_NOT_ALLOWED(string url)
    {
        var result = GitHostAddressRules.NormalizeBaseUrl(url, allowInsecureHttp: true, allowPrivateNetworks: true);

        Assert.Equal(ErrorCodes.GitHostNotAllowed, result.Error?.Code);
    }

    [Theory]
    [InlineData("https://10.0.4.7")]
    [InlineData("https://gitlab.railway.internal")]
    [InlineData("https://GitLab.Railway.Internal.")]
    public void FR130_Private_literal_and_railway_internal_hosts_need_AllowPrivateNetworks(string url)
    {
        Assert.True(GitHostAddressRules.NormalizeBaseUrl(url, false, allowPrivateNetworks: true).IsSuccess);
        Assert.Equal(ErrorCodes.GitHostNotAllowed, GitHostAddressRules.NormalizeBaseUrl(url, false, allowPrivateNetworks: false).Error?.Code);
    }

    [Fact]
    public void S8_Railway_internal_host_name_is_private()
    {
        Assert.Equal(GitHostAddressClass.Private, GitHostAddressRules.ClassifyHostName("api.railway.internal", false).Class);
        Assert.False(GitHostAddressRules.ClassifyHostName("api.railway.internal", false).Allowed);
        Assert.True(GitHostAddressRules.ClassifyHostName("api.railway.internal", true).Allowed);
        Assert.True(GitHostAddressRules.ClassifyHostName("railway.internal.example.com", false).Allowed);
    }

    [Fact]
    public void FR130_Http_needs_allow_insecure_http()
    {
        Assert.Equal(ErrorCodes.GitHostNotAllowed, GitHostAddressRules.NormalizeBaseUrl("http://gitlab.lab.example", false, false).Error?.Code);

        var allowed = GitHostAddressRules.NormalizeBaseUrl("http://gitlab.lab.example", allowInsecureHttp: true, allowPrivateNetworks: false);
        Assert.True(allowed.IsSuccess);
        Assert.Equal("http", allowed.Value.Scheme);
        Assert.Equal(ErrorCodes.GitHostNotAllowed, GitHostAddressRules.NormalizeBaseUrl("ftp://gitlab.example", true, true).Error?.Code);
    }

    [Theory]
    [InlineData("https://GitLab.Example.com:443/", "https://gitlab.example.com/")]
    [InlineData("https://gitlab.example.com:8443/gitlab//", "https://gitlab.example.com:8443/gitlab")]
    [InlineData("  https://gitlab.example.com/gitlab/  ", "https://gitlab.example.com/gitlab")]
    public void FR130_Base_URL_is_normalised(string input, string expected)
    {
        var result = GitHostAddressRules.NormalizeBaseUrl(input, false, false);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("gitlab.example.com")]
    [InlineData("https://user:pass@gitlab.example.com")]
    [InlineData("https://gitlab.example.com/?x=1")]
    [InlineData("https://gitlab.example.com/#top")]
    [InlineData("https://gitlab.example.com/a/../b")]
    [InlineData("https://gitlab.example.com\\evil")]
    public void FR130_Malformed_base_URLs_are_VALIDATION(string input)
    {
        var result = GitHostAddressRules.NormalizeBaseUrl(input, false, false);

        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.All(result.Error!.Details, d => Assert.StartsWith("baseUrl:", d, StringComparison.Ordinal));
    }

    [Fact]
    public void S8_Cross_host_redirects_are_not_followed_and_tokens_stay_on_their_instance()
    {
        var baseUrl = new Uri("https://gitlab.example.com/gitlab");

        Assert.True(GitHostAddressRules.IsRedirectAllowed(baseUrl, new Uri("https://gitlab.example.com/gitlab/users/sign_in")));
        Assert.False(GitHostAddressRules.IsRedirectAllowed(baseUrl, new Uri("https://evil.example.com/")));
        Assert.False(GitHostAddressRules.IsRedirectAllowed(baseUrl, new Uri("http://gitlab.example.com/gitlab")));
        Assert.False(GitHostAddressRules.IsRedirectAllowed(baseUrl, new Uri("https://gitlab.example.com:8443/")));
        Assert.False(GitHostAddressRules.SameOrigin(baseUrl, new Uri("https://gitlab.com/api/v4/user")));
    }
}
