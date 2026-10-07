using System.Security.Claims;
using Aide.Api.Auth;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Auth;

/// <summary>§2 / D9: <see cref="IProjectAccess"/> applies the domain access matrix to stored memberships.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectAccessTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ProjectRole.Viewer, ProjectAction.View, null)]
    [InlineData(ProjectRole.Viewer, ProjectAction.RunAgents, ErrorCodes.Forbidden)]
    [InlineData(ProjectRole.Contributor, ProjectAction.AdvancePhase, null)]
    [InlineData(ProjectRole.Contributor, ProjectAction.DecideGate, ErrorCodes.Forbidden)]
    [InlineData(ProjectRole.Owner, ProjectAction.DeleteProject, null)]
    public async Task S2_Members_are_checked_against_the_role_matrix(ProjectRole role, ProjectAction action, string? expectedError)
    {
        var (project, user) = await SeedAsync(SystemRole.User, role);

        var result = await CheckAsync(user, project, action);

        Assert.Equal(expectedError, result.Error?.Code);
        if (result.IsSuccess)
        {
            Assert.Equal(role, result.Value.ProjectRole);
        }
    }

    [Fact]
    public async Task S2_Non_members_get_NOT_FOUND()
    {
        var (project, _) = await SeedAsync(SystemRole.User, ProjectRole.Owner);
        var (_, outsider) = await SeedAsync(SystemRole.User, null);

        var result = await CheckAsync(outsider, project, ProjectAction.View);

        Assert.Equal(ErrorCodes.NotFound, result.Error?.Code);
    }

    [Fact]
    public async Task S2_Admins_may_act_on_any_existing_project()
    {
        var (project, _) = await SeedAsync(SystemRole.User, ProjectRole.Owner);
        var (_, admin) = await SeedAsync(SystemRole.Admin, null);

        var result = await CheckAsync(admin, project, ProjectAction.DeleteProject);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ProjectRole);
    }

    [Fact]
    public async Task S2_Admins_get_NOT_FOUND_for_a_missing_project()
    {
        var (_, admin) = await SeedAsync(SystemRole.Admin, null);

        var result = await CheckAsync(admin, Guid.NewGuid(), ProjectAction.View);

        Assert.Equal(ErrorCodes.NotFound, result.Error?.Code);
    }

    [Fact]
    public async Task S2_Anonymous_callers_get_UNAUTHENTICATED()
    {
        var (project, _) = await SeedAsync(SystemRole.User, ProjectRole.Owner);

        var result = await CheckAsync(null, project, ProjectAction.View);

        Assert.Equal(ErrorCodes.Unauthenticated, result.Error?.Code);
    }

    private async Task<Result<ProjectAccessGrant>> CheckAsync(Guid? userId, Guid projectId, ProjectAction action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var identity = userId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())], "Test");
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = scope.ServiceProvider };
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IProjectAccess>().CheckAsync(projectId, action, Ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    /// <summary>A project plus a user with the given system role and (optional) membership.</summary>
    private async Task<(Guid ProjectId, Guid UserId)> SeedAsync(SystemRole systemRole, ProjectRole? membership)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var now = DateTimeOffset.UtcNow;
        var email = $"access-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Name = "Access",
            SystemRole = systemRole,
            CreatedAt = now,
        };
        var project = new Project { Name = "P", Slug = $"p-{Guid.NewGuid():N}", Idea = "Idea", CreatedAt = now, UpdatedAt = now };
        db.Users.Add(user);
        db.Projects.Add(project);
        if (membership is { } role)
        {
            db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = user.Id, Role = role, CreatedAt = now });
        }

        await db.SaveChangesAsync(Ct);
        return (project.Id, user.Id);
    }
}
