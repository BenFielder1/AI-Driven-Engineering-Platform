using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Projects;

/// <summary>FR-8: members and roles, and the last-Owner rule.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectMemberTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR8_Owner_adds_an_existing_user_by_email_with_a_role()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        using var newcomer = await ApiSession.RegisterAsync(factory, name: "New Comer");
        var project = await ProjectApi.CreateAsync(owner.Client);

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/members", new { email = newcomer.User!.Email.ToUpperInvariant(), role = "contributor" }, Ct);
        var member = await ProjectApi.ReadAsync<MemberDto>(response, HttpStatusCode.Created);

        Assert.Equal((newcomer.User.Id, newcomer.User.Email, "New Comer", ProjectRole.Contributor), (member.UserId, member.Email, member.Name, member.Role));
        Assert.Equal($"/api/projects/{project.Id}/members/{newcomer.User.Id}", response.Headers.Location?.OriginalString);
        var members = await ListAsync(owner.Client, project.Id);
        Assert.Equal([ProjectRole.Owner, ProjectRole.Contributor], members.Select(m => m.Role));
        var shared = await ProjectApi.ReadAsync<ProjectDto>(await newcomer.Client.GetAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct));
        Assert.Equal(ProjectRole.Contributor, shared.MyRole);
        await AssertEventAsync(project.Id, EventTypes.MemberAdded);
    }

    [Fact]
    public async Task FR8_Unknown_email_is_USER_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PostAsJsonAsync(
            $"{fixture.Path}/members", new { email = ApiSession.NewEmail("nobody"), role = "viewer" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task FR8_Adding_an_existing_member_is_CONFLICT()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PostAsJsonAsync(
            $"{fixture.Path}/members", new { email = fixture.Viewer.User!.Email, role = "contributor" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Theory]
    [InlineData("""{"email":"not-an-email","role":"viewer"}""")]
    [InlineData("""{"email":"a@example.com","role":"admin"}""")]
    [InlineData("""{"role":"viewer"}""")]
    public async Task FR8_Invalid_add_requests_are_VALIDATION(string json)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PostAsync(
            new Uri($"{fixture.Path}/members", UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR8_Only_Owners_manage_members_but_every_member_can_list_them()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var other = await ApiSession.RegisterAsync(factory);

        var asViewer = await ListAsync(fixture.Viewer.Client, fixture.Project.Id);
        var add = await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/members", new { email = other.User!.Email, role = "viewer" }, Ct);
        var change = await ProjectApi.PatchAsync(fixture.Contributor.Client, $"{fixture.Path}/members/{fixture.Viewer.User!.Id}", new { role = "owner" });
        var remove = await fixture.Viewer.Client.DeleteAsync(new Uri($"{fixture.Path}/members/{fixture.Contributor.User!.Id}", UriKind.Relative), Ct);
        var outsiderList = await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/members", UriKind.Relative), Ct);

        Assert.Equal(3, asViewer.Count);
        Assert.Equal(fixture.Owner.User!.Id, asViewer[0].UserId);
        await Problems.AssertAsync(add, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(change, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(remove, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(outsiderList, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR8_DemoteLastOwner_409()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{fixture.Owner.User!.Id}", new { role = "contributor" });

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.LastOwner);
    }

    [Fact]
    public async Task FR8_RemoveLastOwner_409_including_leaving()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.DeleteAsync(new Uri($"{fixture.Path}/members/{fixture.Owner.User!.Id}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.LastOwner);
    }

    [Fact]
    public async Task FR8_Owner_changes_roles_and_a_second_Owner_can_then_step_down()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var promoted = await ProjectApi.ReadAsync<MemberDto>(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{fixture.Contributor.User!.Id}", new { role = "owner" }));
        var steppedDown = await ProjectApi.ReadAsync<MemberDto>(
            await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{fixture.Owner.User!.Id}", new { role = "viewer" }));
        var nowForbidden = await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { name = "x" });

        Assert.Equal(ProjectRole.Owner, promoted.Role);
        Assert.Equal(ProjectRole.Viewer, steppedDown.Role);
        await Problems.AssertAsync(nowForbidden, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await AssertEventAsync(fixture.Project.Id, EventTypes.MemberRoleChanged, expected: 2);
    }

    [Fact]
    public async Task FR8_Changing_the_role_of_a_non_member_is_404()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/members/{fixture.Outsider.User!.Id}", new { role = "viewer" });

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR8_Owner_removes_a_member()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.DeleteAsync(new Uri($"{fixture.Path}/members/{fixture.Contributor.User!.Id}", UriKind.Relative), Ct);
        var removedView = await fixture.Contributor.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Problems.AssertAsync(removedView, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(2, (await ListAsync(fixture.Owner.Client, fixture.Project.Id)).Count);
        await AssertEventAsync(fixture.Project.Id, EventTypes.MemberRemoved);
    }

    [Fact]
    public async Task FR8_Any_member_can_leave_the_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Viewer.Client.DeleteAsync(new Uri($"{fixture.Path}/members/{fixture.Viewer.User!.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Problems.AssertAsync(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR8_Admins_manage_members_of_any_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await ProjectApi.PatchAsync(admin.Client, $"{fixture.Path}/members/{fixture.Viewer.User!.Id}", new { role = "contributor" });

        Assert.Equal(ProjectRole.Contributor, (await ProjectApi.ReadAsync<MemberDto>(response)).Role);
    }

    private static async Task<List<MemberDto>> ListAsync(HttpClient client, Guid projectId) =>
        await ProjectApi.ReadAsync<List<MemberDto>>(await client.GetAsync(new Uri($"/api/projects/{projectId}/members", UriKind.Relative), Ct));

    private Task AssertEventAsync(Guid projectId, string type, int expected = 1) =>
        ProjectApi.WithDbAsync(factory, async db =>
            Assert.Equal(expected, await db.Events.CountAsync(e => e.ProjectId == projectId && e.Type == type, Ct)));
}
