using System.Security.Cryptography;
using System.Text;
using Aide.Domain.Access;
using Aide.Domain.Common;

namespace Aide.UnitTests.Access;

/// <summary>FR-3 system-role rules, FR-4 API token rules, §2 system actor.</summary>
public class AccountRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FR3_First_account_is_Admin() =>
        Assert.Equal(SystemRole.Admin, SystemRoleRules.ForNewAccount(anyAccountExists: false));

    [Fact]
    public void FR3_Later_accounts_are_User() =>
        Assert.Equal(SystemRole.User, SystemRoleRules.ForNewAccount(anyAccountExists: true));

    [Theory]
    [InlineData(SystemRole.User, SystemRole.Admin, 1, true)]
    [InlineData(SystemRole.Admin, SystemRole.User, 2, true)]
    [InlineData(SystemRole.Admin, SystemRole.Admin, 1, true)]
    [InlineData(SystemRole.User, SystemRole.User, 1, true)]
    [InlineData(SystemRole.Admin, SystemRole.User, 1, false)]
    public void FR3_Role_changes_keep_at_least_one_Admin(SystemRole current, SystemRole requested, int admins, bool allowed)
    {
        var result = SystemRoleRules.ChangeRole(current, requested, admins);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorCodes.Conflict, result.Error!.Code);
        }
    }

    [Fact]
    public void FR4_Token_is_prefixed_base64url_of_32_bytes()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 8)).ToArray();

        var token = ApiTokenRules.Format(secret);

        Assert.StartsWith("aide_pat_", token, StringComparison.Ordinal);
        Assert.Equal(9 + 43, token.Length);
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.True(ApiTokenRules.IsWellFormed(token));
    }

    [Fact]
    public void FR4_Token_secret_must_be_32_bytes() =>
        Assert.Throws<ArgumentException>(() => ApiTokenRules.Format(new byte[16]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aide_pat_")]
    [InlineData("aide_pat_tooshort")]
    [InlineData("ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aide_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("aide_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void FR4_Malformed_tokens_are_rejected_before_lookup(string? token) =>
        Assert.False(ApiTokenRules.IsWellFormed(token));

    [Fact]
    public void FR4_Only_the_SHA256_hex_hash_is_stored()
    {
        const string token = "aide_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        var hash = ApiTokenRules.Hash(token);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), hash);
        Assert.Equal(64, hash.Length);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, 60, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, -60, false)]
    [InlineData(true, null, false)]
    [InlineData(true, 60, false)]
    public void FR4_Revoked_or_expired_tokens_are_unusable(bool revoked, int? expiresInSeconds, bool usable)
    {
        DateTimeOffset? revokedAt = revoked ? Now.AddDays(-1) : null;
        DateTimeOffset? expiresAt = expiresInSeconds is { } s ? Now.AddSeconds(s) : null;

        Assert.Equal(usable, ApiTokenRules.IsUsable(revokedAt, expiresAt, Now));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(30, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(600, true)]
    public void FR4_Last_used_is_recorded_at_most_once_a_minute(int? secondsSinceLastUse, bool record)
    {
        DateTimeOffset? lastUsed = secondsSinceLastUse is { } s ? Now.AddSeconds(-s) : null;

        Assert.Equal(record, ApiTokenRules.ShouldRecordUse(lastUsed, Now));
    }

    [Fact]
    public void FR4_New_token_needs_a_name_and_a_future_expiry()
    {
        Assert.True(ApiTokenRules.ValidateNew("cli", null, Now).IsSuccess);
        Assert.True(ApiTokenRules.ValidateNew("cli", Now.AddMinutes(1), Now).IsSuccess);

        var invalid = ApiTokenRules.ValidateNew("  ", Now, Now);

        Assert.Equal(ErrorCodes.Validation, invalid.Error!.Code);
        Assert.Contains(invalid.Error.Details, d => d.StartsWith("name:", StringComparison.Ordinal));
        Assert.Contains(invalid.Error.Details, d => d.StartsWith("expiresAt:", StringComparison.Ordinal));
        Assert.True(ApiTokenRules.ValidateNew(new string('n', 101), null, Now).IsFailure);
    }

    [Fact]
    public void S2_System_actor_is_aide_system() => Assert.Equal("aide-system", SystemActor.Name);

    [Fact]
    public void S2_Current_user_actor_is_the_user_id_or_the_system_actor()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id.ToString(), ((ICurrentUser)new FakeCurrentUser(id, false)).Actor);
        Assert.Equal("aide-system", ((ICurrentUser)new FakeCurrentUser(null, true)).Actor);
        Assert.False(((ICurrentUser)new FakeCurrentUser(null, false)).IsAuthenticated);
        Assert.Throws<InvalidOperationException>(() => ((ICurrentUser)new FakeCurrentUser(null, false)).RequiredUserId);
    }

    private sealed record FakeCurrentUser(Guid? UserId, bool IsSystem) : ICurrentUser;
}
