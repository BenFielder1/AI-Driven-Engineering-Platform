using System.Globalization;
using System.Text;
using Aide.Domain.Entities;

namespace Aide.Domain.GitHosts;

/// <summary>The kind of Git host a commit lands on, for credit (§4.25).</summary>
public enum CommitHostKind
{
    Local = 0,
    GitHub = 1,
    GitLab = 2,
    Bitbucket = 3,
}

/// <summary>The project's Git host (§4.25). <see cref="InstanceHost"/> names a GitLab instance's host, e.g. <c>gitlab.acme.io</c>.</summary>
public sealed record CommitHost(CommitHostKind Kind, string? InstanceHost = null)
{
    /// <summary>The name of GitLab's hosted instance, whose base URL isn't stored.</summary>
    public const string GitLabComHost = "gitlab.com";

    /// <summary>The host of a project with <paramref name="provider"/>; <paramref name="instanceBaseUrl"/> is its GitLab instance (<c>null</c>: gitlab.com).</summary>
    public static CommitHost From(GitProvider provider, string? instanceBaseUrl = null) => provider switch
    {
        GitProvider.GithubApp or GitProvider.GithubPat => new CommitHost(CommitHostKind.GitHub),
        GitProvider.Gitlab => new CommitHost(
            CommitHostKind.GitLab,
            Uri.TryCreate(instanceBaseUrl, UriKind.Absolute, out var url) ? url.Host : GitLabComHost),
        GitProvider.Bitbucket => new CommitHost(CommitHostKind.Bitbucket),
        _ => new CommitHost(CommitHostKind.Local),
    };
}

/// <summary>
/// A person's identity on the project's Git host (FR-176), read from their connection to that host, never from their AIDE
/// sign-in. <see cref="ExternalId"/> is the host's numeric user id (GitHub, GitLab); <see cref="Email"/> is the account's
/// primary confirmed email (Bitbucket only).
/// </summary>
public sealed record CommitIdentity(string Login, string? ExternalId = null, string? Email = null)
{
    /// <summary>Redacted: a Bitbucket email never reaches a log.</summary>
    public override string ToString() => $"CommitIdentity {{ Login = {Login} }}";
}

/// <summary>A candidate as resolution sees them: <see cref="Identity"/> is <c>null</c> without a connection on the project's host.</summary>
public sealed record CommitPerson(string? DisplayName, bool OptedOut, CommitIdentity? Identity);

/// <summary>
/// The input of <see cref="CommitAttribution.Resolve"/> (§4.25): the project setting, the host, the candidates in order
/// (<c>null</c>: no person, such as a system actor) and the people who still exist, by user id.
/// </summary>
public sealed record CommitCreditContext(
    bool CreditEnabled,
    CommitHost Host,
    IReadOnlyList<Guid?> Candidates,
    IReadOnlyDictionary<Guid, CommitPerson> People);

/// <summary>Who a commit credits: the user and the <c>Co-authored-by</c> line for them.</summary>
public sealed record CreditedPerson(Guid UserId, string CoAuthorLine);

/// <summary>§4.25, FR-175: one accountable person per commit, or none.</summary>
public static class CommitAttribution
{
    /// <summary>
    /// The first candidate who exists, has a usable identity on the host and hasn't opted out; <c>null</c> when credit is
    /// off, the host is Local or nobody is eligible. An unusable identity only skips that person.
    /// </summary>
    public static CreditedPerson? Resolve(CommitCreditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.CreditEnabled || context.Host.Kind == CommitHostKind.Local)
        {
            return null;
        }

        foreach (var candidate in context.Candidates)
        {
            if (candidate is not { } userId
                || !context.People.TryGetValue(userId, out var person)
                || person.OptedOut
                || person.Identity is not { } identity
                || CommitTrailers.CoAuthor(person.DisplayName, identity, context.Host) is not { } line)
            {
                continue;
            }

            return new CreditedPerson(userId, line);
        }

        return null;
    }

    /// <summary>§4.25 step commit: plan approver, the actor who moved into execution, the version's starter, the binding Owner, the creator.</summary>
    public static IReadOnlyList<Guid?> StepCandidates(Guid? planApprover, string? executionActor, Guid? startedBy, Guid? bindingOwner, string? creatorActor) =>
        [planApprover, Person(executionActor), startedBy, bindingOwner, Person(creatorActor)];

    /// <summary>§4.25 release merge and tag: pre-release approver, the person who started the release, the binding Owner, the creator.</summary>
    public static IReadOnlyList<Guid?> ReleaseCandidates(Guid? preReleaseApprover, string? releaseActor, Guid? bindingOwner, string? creatorActor) =>
        [preReleaseApprover, Person(releaseActor), bindingOwner, Person(creatorActor)];

    /// <summary>§4.25 initial commit: the project creator.</summary>
    public static IReadOnlyList<Guid?> InitialCandidates(Guid? creator) => [creator];

    /// <summary>The user behind an audit actor (a user id); <c>null</c> for a system actor, which never counts as a person.</summary>
    public static Guid? Person(string? actor) => Guid.TryParse(actor, CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>§4.25, FR-174: the <c>Co-authored-by</c> trailer. AIDE alone writes it; agents can't set or change trailers.</summary>
public static class CommitTrailers
{
    public const string CoAuthorKey = "Co-authored-by";

    /// <summary>The longest name kept in a trailer.</summary>
    public const int MaxNameLength = 100;

    private const string GitHubNoReplyDomain = "users.noreply.github.com";

    /// <summary>
    /// <c>Co-authored-by: Name &lt;address&gt;</c>, always one line whatever the name holds; <c>null</c> when the identity
    /// gives no usable address on <paramref name="host"/>, or no name.
    /// </summary>
    public static string? CoAuthor(string? name, CommitIdentity identity, CommitHost host)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(host);
        var cleanName = CleanName(name, identity.Login);
        return cleanName.Length > 0 && Address(identity, host) is { } address
            ? $"{CoAuthorKey}: {cleanName} <{address}>"
            : null;
    }

    /// <summary>§4.25 emails: the host's noreply address, or Bitbucket's confirmed email; never an AIDE sign-in email.</summary>
    public static string? Address(CommitIdentity identity, CommitHost host)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(host);
        return host.Kind switch
        {
            CommitHostKind.GitHub when IsNumericId(identity.ExternalId) && IsSafeLogin(identity.Login) =>
                $"{identity.ExternalId}+{identity.Login}@{GitHubNoReplyDomain}",
            CommitHostKind.GitLab when IsNumericId(identity.ExternalId) && IsSafeLogin(identity.Login) && !string.IsNullOrWhiteSpace(host.InstanceHost) =>
                $"{identity.ExternalId}-{identity.Login}@users.noreply.{host.InstanceHost}",
            CommitHostKind.Bitbucket when IsUsableEmail(identity.Email) => identity.Email!.Trim(),
            _ => null,
        };
    }

    /// <summary>§4.25 name: the display name, else the login; CR, LF, <c>&lt;</c>, <c>&gt;</c> become spaces, whitespace collapses, cut to 100.</summary>
    public static string CleanName(string? name, string login)
    {
        var clean = Clean(name);
        return clean.Length > 0 ? clean : Clean(login);
    }

    /// <summary>
    /// <paramref name="message"/> with the trailer as its last paragraph; unchanged without one. Not for a message that already
    /// ends in trailers (a step commit adds its line to the block in <see cref="Execution.CodeGeneration.CommitMessage"/>).
    /// </summary>
    public static string Append(string message, string? coAuthorLine)
    {
        ArgumentNullException.ThrowIfNull(message);
        return coAuthorLine is null ? message : $"{message.TrimEnd()}\n\n{coAuthorLine}";
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (c is '<' or '>' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        if (sb.Length > MaxNameLength)
        {
            // Never leave half of a surrogate pair at the cut.
            sb.Length = char.IsHighSurrogate(sb[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsNumericId(string? id) => !string.IsNullOrEmpty(id) && id.All(char.IsAsciiDigit);

    private static bool IsSafeLogin(string login) =>
        !string.IsNullOrEmpty(login) && login.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsUsableEmail(string? email)
    {
        var trimmed = email?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.IndexOf('@', StringComparison.Ordinal) > 0
            && !trimmed.EndsWith('@')
            && !trimmed.Any(c => c is '<' or '>' || char.IsWhiteSpace(c) || char.IsControl(c));
    }
}
