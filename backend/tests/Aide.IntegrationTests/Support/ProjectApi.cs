using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Projects;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>Helpers for the projects feature tests.</summary>
public static class ProjectApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A unique name prefix, so list assertions only see this test's projects.</summary>
    public static string UniqueName(string label = "proj") => $"{label}-{Guid.NewGuid():N}"[..Math.Min(label.Length + 13, 60)];

    public static async Task<ProjectDto> CreateAsync(HttpClient client, string? name = null, object? git = null, string idea = "Build a todo API.", string? initialVersion = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.PostAsJsonAsync(
            "/api/projects",
            new { name = name ?? UniqueName(), idea, git = git ?? new { provider = "local" }, initialVersion },
            Ct);
        return await ReadAsync<ProjectDto>(response, HttpStatusCode.Created);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, ApiSession.Json)!;
    }

    public static Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PatchAsJsonAsync(path, body, Ct);
    }

    /// <summary>Runs <paramref name="action"/> against the factory's database.</summary>
    public static async Task<T> WithDbAsync<T>(WebApplicationFactory<Program> factory, Func<AideDbContext, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AideDbContext>());
    }

    public static async Task WithDbAsync(WebApplicationFactory<Program> factory, Func<AideDbContext, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await WithDbAsync(factory, async db =>
        {
            await action(db);
            return true;
        });
    }

    /// <summary>Adds <paramref name="userId"/> to the project with a role, directly in the database.</summary>
    public static Task AddMemberInDbAsync(WebApplicationFactory<Program> factory, Guid projectId, Guid userId, Aide.Domain.Access.ProjectRole role) =>
        WithDbAsync(factory, async db =>
        {
            db.ProjectMembers.Add(new Aide.Domain.Entities.ProjectMembership { ProjectId = projectId, UserId = userId, Role = role, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

    /// <summary>A project created by a fresh Owner, plus sessions for a Contributor, a Viewer and an outsider.</summary>
    public static async Task<ProjectFixture> FixtureAsync(WebApplicationFactory<Program> factory, object? git = null, string? initialVersion = null)
    {
        var owner = await ApiSession.RegisterAsync(factory);
        var project = await CreateAsync(owner.Client, git: git, initialVersion: initialVersion);
        var contributor = await ApiSession.RegisterAsync(factory);
        var viewer = await ApiSession.RegisterAsync(factory);
        var outsider = await ApiSession.RegisterAsync(factory);
        await AddMemberInDbAsync(factory, project.Id, contributor.User!.Id, Aide.Domain.Access.ProjectRole.Contributor);
        await AddMemberInDbAsync(factory, project.Id, viewer.User!.Id, Aide.Domain.Access.ProjectRole.Viewer);
        return new ProjectFixture(project, owner, contributor, viewer, outsider);
    }
}

public sealed record ProjectFixture(ProjectDto Project, ApiSession Owner, ApiSession Contributor, ApiSession Viewer, ApiSession Outsider) : IDisposable
{
    public string Path => $"/api/projects/{Project.Id}";

    public void Dispose()
    {
        Owner.Dispose();
        Contributor.Dispose();
        Viewer.Dispose();
        Outsider.Dispose();
    }
}
