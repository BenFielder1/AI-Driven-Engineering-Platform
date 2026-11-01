using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Auth;

/// <summary>Account creation and cookie sign-in, shared by password registration and GitHub sign-in.</summary>
public sealed class AccountService(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    AideDbContext db,
    TimeProvider time)
{
    /// <summary>Transaction-scoped advisory lock serialising account creation and system-role changes, so
    /// "the first account becomes Admin" (FR-3) and "the last Admin stays" hold under concurrency.</summary>
    public const long AccountsLockKey = 0x41_49_44_45_00_01; // "AIDE" 0001

    /// <summary>
    /// Creates an account. Its system role is Admin if it is the first account (FR-3), otherwise User. Inside a caller's
    /// transaction (e.g. a Git sign-in that creates, links and connects at once) it joins it and the caller commits or rolls
    /// back; otherwise it runs in its own.
    /// </summary>
    public async Task<IdentityResult> CreateAsync(AppUser user, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await LockAccountsAsync(cancellationToken);

        user.SystemRole = SystemRoleRules.ForNewAccount(await db.Users.AnyAsync(cancellationToken));
        user.CreatedAt = time.GetUtcNow();
        var result = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>Takes <see cref="AccountsLockKey"/> for the current transaction.</summary>
    public Task LockAccountsAsync(CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AccountsLockKey})", cancellationToken);

    /// <summary>Signs the user in with the persistent (sliding 14-day) auth cookie and refreshes the antiforgery
    /// tokens for the new identity.</summary>
    public async Task SignInAsync(HttpContext httpContext, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var principal = await signIn.CreateUserPrincipalAsync(user);
        await httpContext.SignInAsync(IdentityConstants.ApplicationScheme, principal, new AuthenticationProperties { IsPersistent = true });
        httpContext.User = principal;
        AntiforgeryProtection.IssueTokens(httpContext);
    }

    public async Task SignOutAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        AntiforgeryProtection.IssueTokens(httpContext);
    }
}

/// <summary>Password policy: at least 8 characters with a digit (Identity options) and a letter (this validator).
/// Symbols and mixed case are not required.</summary>
public sealed class LetterPasswordValidator : IPasswordValidator<AppUser>
{
    public const string Code = "PasswordRequiresLetter";

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password) =>
        Task.FromResult(password is not null && password.Any(char.IsLetter)
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = Code, Description = "Passwords must contain at least one letter." }));
}

/// <summary>Only same-site relative return URLs are followed after sign-in (no open redirects).</summary>
public static class ReturnUrls
{
    public static string Sanitize(string? returnUrl) => IsLocal(returnUrl) ? returnUrl! : "/";

    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return false;
        }

        if (url.Length == 1)
        {
            return true;
        }

        // "//host" and "/\host" are protocol-relative; control characters can smuggle either.
        return url[1] is not ('/' or '\\') && !url.Any(char.IsControl);
    }
}
