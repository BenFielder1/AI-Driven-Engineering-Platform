namespace Aide.Domain.Access;

/// <summary>§2: the identity background workers act as in audit records. It can't sign in.</summary>
public static class SystemActor
{
    /// <summary>Actor name recorded on transitions, events and decisions made by background work.</summary>
    public const string Name = "aide-system";
}

/// <summary>
/// Who is making the current request (or doing the current background work). Implemented by the API from the
/// authenticated principal; background workers use the <see cref="SystemActor"/>.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The signed-in user's id; <c>null</c> when anonymous or the system actor.</summary>
    Guid? UserId { get; }

    /// <summary><c>true</c> for background work acting as <see cref="SystemActor.Name"/>.</summary>
    bool IsSystem { get; }

    bool IsAuthenticated => UserId is not null;

    /// <summary>The user id, for code paths that already require an authenticated user.</summary>
    Guid RequiredUserId => UserId ?? throw new InvalidOperationException("No authenticated user.");

    /// <summary>The actor string for audit records: the user id, or <see cref="SystemActor.Name"/>.</summary>
    string Actor => IsSystem ? SystemActor.Name : UserId?.ToString() ?? "anonymous";
}
