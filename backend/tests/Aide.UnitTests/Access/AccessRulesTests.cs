using Aide.Domain.Access;
using Aide.Domain.Common;

namespace Aide.UnitTests.Access;

/// <summary>§2 permission table, FR-6–FR-8, D9.</summary>
public class ProjectPermissionsTests
{
    private static readonly ProjectAction[] WriterActions =
        [ProjectAction.RunAgents, ProjectAction.ControlExecution, ProjectAction.CreateDraft, ProjectAction.TriggerReview, ProjectAction.AdvancePhase];

    private static readonly ProjectAction[] OwnerActions =
    [
        ProjectAction.DecideGate, ProjectAction.DecideArtifact, ProjectAction.RequestRelease, ProjectAction.Rollback,
        ProjectAction.ChangeSettings, ProjectAction.ManageMembers, ProjectAction.DeleteProject, ProjectAction.StartVersion,
    ];

    public static TheoryData<ProjectRole, ProjectAction> RoleActionMatrix()
    {
        var data = new TheoryData<ProjectRole, ProjectAction>();
        foreach (var role in Enum.GetValues<ProjectRole>())
        {
            foreach (var action in Enum.GetValues<ProjectAction>())
            {
                data.Add(role, action);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RoleActionMatrix))]
    public void S2_Role_capability_matrix(ProjectRole role, ProjectAction action)
    {
        var allowed = role switch
        {
            ProjectRole.Owner => true,
            ProjectRole.Contributor => !OwnerActions.Contains(action),
            ProjectRole.Viewer => action == ProjectAction.View,
            _ => false,
        };

        Assert.Equal(allowed ? AccessDecision.Allowed : AccessDecision.Forbidden, ProjectPermissions.Check(SystemRole.User, role, action));
    }

    [Fact]
    public void Every_action_is_classified() =>
        Assert.Equal(Enum.GetValues<ProjectAction>().Length, 1 + WriterActions.Length + OwnerActions.Length);

    [Theory]
    [MemberData(nameof(RoleActionMatrix))]
    public void S2_Admin_can_do_everything_on_every_project(ProjectRole role, ProjectAction action)
    {
        Assert.Equal(AccessDecision.Allowed, ProjectPermissions.Check(SystemRole.Admin, role, action));
        Assert.Equal(AccessDecision.Allowed, ProjectPermissions.Check(SystemRole.Admin, null, action));
    }

    [Fact]
    public void FR6_Non_members_get_not_found_for_any_action()
    {
        foreach (var action in Enum.GetValues<ProjectAction>())
        {
            Assert.Equal(AccessDecision.NotFound, ProjectPermissions.Check(SystemRole.User, null, action));
        }
    }

    [Fact]
    public void FR7_Contributor_cannot_patch_the_project() =>
        Assert.Equal(AccessDecision.Forbidden, ProjectPermissions.Check(SystemRole.User, ProjectRole.Contributor, ProjectAction.ChangeSettings));
}

public class MembershipRulesTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public void FR8_The_only_owner_cannot_demote_themselves()
    {
        var result = MembershipRules.ChangeRole([new(Owner, ProjectRole.Owner)], Owner, ProjectRole.Contributor);

        Assert.Equal(ErrorCodes.LastOwner, result.Error!.Code);
        Assert.Equal(409, ErrorCodes.StatusCodeFor(result.Error.Code));
    }

    [Fact]
    public void FR8_The_last_owner_cannot_be_removed() =>
        Assert.Equal(
            ErrorCodes.LastOwner,
            MembershipRules.Remove([new(Owner, ProjectRole.Owner), new(Other, ProjectRole.Contributor)], Owner).Error!.Code);

    [Fact]
    public void FR8_An_owner_can_be_demoted_when_another_owner_exists()
    {
        var result = MembershipRules.ChangeRole([new(Owner, ProjectRole.Owner), new(Other, ProjectRole.Owner)], Owner, ProjectRole.Viewer);

        Assert.Contains(new ProjectMember(Owner, ProjectRole.Viewer), result.Value);
    }

    [Fact]
    public void FR8_Members_can_be_added_once()
    {
        var added = MembershipRules.Add([new(Owner, ProjectRole.Owner)], Other, ProjectRole.Viewer);

        Assert.Equal(2, added.Value.Count);
        Assert.Equal(ErrorCodes.Conflict, MembershipRules.Add(added.Value, Other, ProjectRole.Owner).Error!.Code);
    }

    [Fact]
    public void FR8_Removing_or_changing_a_non_member_is_not_found()
    {
        IReadOnlyList<ProjectMember> members = [new(Owner, ProjectRole.Owner)];

        Assert.Equal(ErrorCodes.NotFound, MembershipRules.Remove(members, Other).Error!.Code);
        Assert.Equal(ErrorCodes.NotFound, MembershipRules.ChangeRole(members, Other, ProjectRole.Owner).Error!.Code);
    }

    [Fact]
    public void FR8_Non_owner_can_be_removed() =>
        Assert.Single(MembershipRules.Remove([new(Owner, ProjectRole.Owner), new(Other, ProjectRole.Contributor)], Other).Value);
}
