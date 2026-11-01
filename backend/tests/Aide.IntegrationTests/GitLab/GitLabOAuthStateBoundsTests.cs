using Aide.Api.Features.GitLab;
using Aide.Domain.GitHosts;
using Microsoft.AspNetCore.DataProtection;

namespace Aide.IntegrationTests.GitLab;

/// <summary>
/// FR-131, §8: the anonymous OAuth start holds its flows in memory, bounded: at most <see cref="GitLabOAuthStates.MaxPending"/>
/// at once (more starts are refused), with expired flows swept periodically (no database, no app).
/// </summary>
public sealed class GitLabOAuthStateBoundsTests
{
    [Fact]
    public void FR131_S8_Starts_beyond_the_cap_are_refused_until_flows_are_used()
    {
        var states = new GitLabOAuthStates(new EphemeralDataProtectionProvider(), new Clock(DateTimeOffset.UtcNow));
        (string State, string CookieValue, GitLabOAuthFlow Flow)? first = null;
        for (var i = 0; i < GitLabOAuthStates.MaxPending; i++)
        {
            var issued = states.Issue(Guid.NewGuid(), GitOAuthMode.Signin, "/", null);
            Assert.NotNull(issued);
            first ??= issued;
        }

        Assert.Equal(GitLabOAuthStates.MaxPending, states.PendingCount);
        Assert.Null(states.Issue(Guid.NewGuid(), GitOAuthMode.Signin, "/", null));
        Assert.Equal(GitLabOAuthStates.MaxPending, states.PendingCount);

        // Using a flow frees its slot.
        Assert.NotNull(states.TryConsume(first!.Value.State, first.Value.CookieValue));
        Assert.Equal(GitLabOAuthStates.MaxPending - 1, states.PendingCount);
        Assert.NotNull(states.Issue(Guid.NewGuid(), GitOAuthMode.Signin, "/", null));
    }

    [Fact]
    public void FR131_S8_Expired_flows_are_swept_so_a_full_store_accepts_starts_again()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var states = new GitLabOAuthStates(new EphemeralDataProtectionProvider(), clock);
        for (var i = 0; i < GitLabOAuthStates.MaxPending; i++)
        {
            Assert.NotNull(states.Issue(Guid.NewGuid(), GitOAuthMode.Signin, "/", null));
        }

        Assert.Null(states.Issue(Guid.NewGuid(), GitOAuthMode.Signin, "/", null));

        clock.Advance(GitLabOAuthStates.Lifetime + GitLabOAuthStates.SweepInterval);
        var issued = states.Issue(Guid.NewGuid(), GitOAuthMode.Connect, "/settings", Guid.NewGuid());

        Assert.NotNull(issued);
        Assert.Equal(1, states.PendingCount);
        Assert.Equal(issued.Value.Flow, states.TryConsume(issued.Value.State, issued.Value.CookieValue));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
