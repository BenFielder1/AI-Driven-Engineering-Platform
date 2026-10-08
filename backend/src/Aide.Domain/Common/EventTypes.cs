namespace Aide.Domain.Common;

/// <summary>
/// Types of the append-only audit events (table <c>events</c>, FR-50, FR-51). Values are SCREAMING_SNAKE strings
/// and are part of the stored data and the live/activity feeds, so they never change once used.
/// </summary>
public static class EventTypes
{
    // Projects (FR-5, FR-7)
    public const string ProjectCreated = "PROJECT_CREATED";
    public const string ProjectUpdated = "PROJECT_UPDATED";

    /// <summary>Written with <c>project_id</c> null (the project's events cascade away); the id is in the payload.</summary>
    public const string ProjectDeleted = "PROJECT_DELETED";

    // Members (FR-8)
    public const string MemberAdded = "MEMBER_ADDED";
    public const string MemberRoleChanged = "MEMBER_ROLE_CHANGED";
    public const string MemberRemoved = "MEMBER_REMOVED";

    /// <summary>Git, LLM or notification settings changed (FR-9). The payload never contains secret values.</summary>
    public const string SettingsUpdated = "SETTINGS_UPDATED";

    /// <summary>DEP-2 seed marker: the dev seed created its demo project. Written with <c>project_id</c> null (so it
    /// survives deleting the project) and the project id in the payload; the seed never creates the demo again.</summary>
    public const string DemoProjectSeeded = "DEMO_PROJECT_SEEDED";

    // Lifecycle (FR-11)
    public const string PhaseChanged = "PHASE_CHANGED";
}
