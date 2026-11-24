using System.Net.Http.Json;
using Tevrin.Api.Auth;
using Tevrin.Api.Errors;
using Tevrin.Api.Features.Activity;
using Tevrin.Api.Features.Bitbucket;
using Tevrin.Api.Features.GitLab;
using Tevrin.Domain.Common;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Auth;

/// <summary>
/// FR-201: the 0.6.0 rename keeps every identifier a user already holds (§4.29), so a browser signed in before the deploy stays
/// signed in. FR-198: the server's own text names Tevrin, with unchanged error codes and statuses.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Fr201CompatibilityTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FR201_The_aide_cookie_names_users_hold_are_still_read()
    {
        // FR-326 (§4.77) renames the cookies to tevrin.*; the aide.* names are read (and moved) until 0.9.0.
        Assert.Equal(
            ["aide.auth", "aide.external", "aide.af", "XSRF-TOKEN"],
            [AuthConstants.LegacyAuthCookieName, AuthConstants.LegacyExternalCookieName, AuthConstants.LegacyAntiforgeryCookieName, AuthConstants.XsrfCookieName]);
        Assert.Equal(
            ["aide.bitbucket.oauth", "aide.bitbucket.link", "aide.gitlab.oauth", "aide.gitlab.link"],
            [BitbucketOAuthStates.LegacyCookieName, BitbucketPendingLinks.LegacyCookieName, GitLabOAuthStates.LegacyCookieName, GitLabPendingLinks.LegacyCookieName]);
        Assert.All(LegacyCookies.Renames, r => Assert.StartsWith("aide.", r.Legacy, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR326_Signing_in_issues_the_tevrin_auth_cookie()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = ApiSession.NewEmail(), name = "On 0.8.0", password = TevrinApiFactory.Password }, Ct);

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, c => c.StartsWith("tevrin.auth=", StringComparison.Ordinal));
        Assert.DoesNotContain(setCookies, c => c.StartsWith("aide.auth=", StringComparison.Ordinal));
    }

    [Fact]
    public void FR198_The_GitHub_App_problem_names_Tevrin_with_the_same_code_and_status()
    {
        var problem = ApiProblems.GitHubAppNotInstalled(null).ProblemDetails;

        Assert.Equal("The Tevrin GitHub App isn't installed on this account or repository.", problem.Title);
        Assert.Equal((409, ErrorCodes.GitHubAppNotInstalled), (problem.Status, problem.Extensions["errorCode"]));
    }

    [Fact]
    public void FR198_The_system_actor_is_shown_as_Tevrin() =>
        Assert.Equal("Tevrin", ActorResolver.SystemDisplayName);
}
