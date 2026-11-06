using System.Net.Http.Json;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Activity;
using Aide.Api.Features.Bitbucket;
using Aide.Api.Features.GitLab;
using Aide.Domain.Common;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Auth;

/// <summary>
/// FR-201: the 0.6.0 rename keeps every identifier a user already holds (§4.29), so a browser signed in before the deploy stays
/// signed in. FR-198: the server's own text names Tevrin, with unchanged error codes and statuses.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class Fr201CompatibilityTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FR201_Cookie_names_keep_the_aide_prefix()
    {
        Assert.Equal(
            ["aide.auth", "aide.external", "aide.af", "XSRF-TOKEN"],
            [AuthConstants.AuthCookieName, AuthConstants.ExternalCookieName, AuthConstants.AntiforgeryCookieName, AuthConstants.XsrfCookieName]);
        Assert.Equal(
            ["aide.bitbucket.oauth", "aide.bitbucket.link", "aide.gitlab.oauth", "aide.gitlab.link"],
            [BitbucketOAuthStates.CookieName, BitbucketPendingLinks.CookieName, GitLabOAuthStates.CookieName, GitLabPendingLinks.CookieName]);
    }

    [Fact]
    public async Task FR201_Signing_in_still_issues_the_aide_auth_cookie()
    {
        using var session = new ApiSession(factory);

        var response = await session.Client.PostAsJsonAsync(
            "/api/auth/register", new { email = ApiSession.NewEmail(), name = "Before 0.6.0", password = AideApiFactory.Password }, Ct);

        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("aide.auth=", StringComparison.Ordinal));
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
