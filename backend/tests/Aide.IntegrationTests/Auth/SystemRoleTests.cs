using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Auth;
using Aide.Domain.Access;
using Aide.Infrastructure;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aide.IntegrationTests.Auth;

/// <summary>FR-3: first user is Admin; Admins list users and change system roles (§6 /api/admin/users).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SystemRoleTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR3_First_registered_user_is_Admin_and_the_next_is_User()
    {
        await using var fresh = await FreshDatabaseAsync();

        using var first = await ApiSession.RegisterAsync(fresh);
        using var second = await ApiSession.RegisterAsync(fresh);

        Assert.Equal(SystemRole.Admin, first.User!.SystemRole);
        Assert.Equal(SystemRole.User, second.User!.SystemRole);
    }

    [Fact]
    public async Task FR3_Concurrent_first_registrations_create_exactly_one_Admin()
    {
        await using var fresh = await FreshDatabaseAsync();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ApiSession.RegisterAsync(fresh)));

        Assert.Single(sessions, s => s.User!.SystemRole == SystemRole.Admin);
        foreach (var s in sessions)
        {
            s.Dispose();
        }
    }

    [Fact]
    public async Task FR3_The_last_Admin_cannot_be_demoted()
    {
        await using var fresh = await FreshDatabaseAsync();
        using var admin = await ApiSession.RegisterAsync(fresh);

        var response = await admin.Client.PatchAsJsonAsync(
            $"/api/admin/users/{admin.User!.Id}/role", new { systemRole = "user" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, "CONFLICT");
    }

    [Fact]
    public async Task FR3_Admin_changes_a_users_role_and_it_applies_immediately()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        using var user = await ApiSession.RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode);

        var promote = await admin.Client.PatchAsJsonAsync($"/api/admin/users/{user.User!.Id}/role", new { systemRole = "admin" }, Ct);

        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        Assert.Equal(SystemRole.Admin, (await promote.Content.ReadFromJsonAsync<UserDto>(ApiSession.Json, Ct))!.SystemRole);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode);

        var demote = await admin.Client.PatchAsJsonAsync($"/api/admin/users/{user.User.Id}/role", new { systemRole = "user" }, Ct);

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR3_Admin_api_token_cannot_change_roles()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        using var user = await ApiSession.RegisterAsync(factory);
        var created = await admin.Client.PostAsJsonAsync("/api/me/tokens", new { name = "admin-cli" }, Ct);
        var token = (await created.Content.ReadFromJsonAsync<Api.Features.Tokens.CreatedApiTokenDto>(ApiSession.Json, Ct))!;
        using var cli = ApiSession.BearerClient(factory, token.Token);

        var response = await cli.PatchAsJsonAsync($"/api/admin/users/{user.User!.Id}/role", new { systemRole = "admin" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR3_Non_admin_changing_a_role_gets_403_FORBIDDEN()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        var response = await user.Client.PatchAsJsonAsync($"/api/admin/users/{user.User!.Id}/role", new { systemRole = "admin" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task FR3_Anonymous_admin_request_gets_401_UNAUTHENTICATED()
    {
        using var anonymous = new ApiSession(factory);

        var response = await anonymous.Client.GetAsync(new Uri("/api/admin/users", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task FR3_Changing_the_role_of_an_unknown_user_returns_404_NOT_FOUND()
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await admin.Client.PatchAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}/role", new { systemRole = "admin" }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"systemRole\":\"superuser\"}")]
    public async Task FR3_Changing_a_role_without_a_valid_role_returns_400_VALIDATION(string json)
    {
        using var admin = await ApiSession.AdminAsync(factory);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await admin.Client.PatchAsync(new Uri($"/api/admin/users/{admin.User!.Id}/role", UriKind.Relative), content, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }

    [Fact]
    public async Task FR3_Admin_lists_users_with_search_and_paging()
    {
        using var admin = await ApiSession.AdminAsync(factory);
        var tag = Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < 3; i++)
        {
            using var _ = await ApiSession.RegisterAsync(factory, $"{tag}-{i}@example.com");
        }

        var page1 = await admin.Client.GetFromJsonAsync<PagedResult<AdminUserDto>>($"/api/admin/users?search={tag}&limit=2", ApiSession.Json, Ct);
        var page2 = await admin.Client.GetFromJsonAsync<PagedResult<AdminUserDto>>($"/api/admin/users?search={tag.ToUpperInvariant()}&limit=2&page=2", ApiSession.Json, Ct);

        Assert.Equal(3, page1!.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.Limit);
        Assert.Single(page2!.Items);
        Assert.Equal(3, page1.Items.Concat(page2.Items).Select(u => u.Email).Distinct().Count());
    }

    [Fact]
    public async Task FR3_Search_treats_like_wildcards_literally()
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var result = await admin.Client.GetFromJsonAsync<PagedResult<AdminUserDto>>("/api/admin/users?search=%25%25%25_", ApiSession.Json, Ct);

        Assert.Equal(0, result!.Total);
    }

    [Theory]
    [InlineData("limit=101")]
    [InlineData("limit=0")]
    [InlineData("page=0")]
    public async Task FR3_List_users_with_out_of_range_paging_returns_400_VALIDATION(string query)
    {
        using var admin = await ApiSession.AdminAsync(factory);

        var response = await admin.Client.GetAsync(new Uri($"/api/admin/users?{query}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }

    private async Task<WebApplicationFactory<Program>> FreshDatabaseAsync()
    {
        var connectionString = await factory.CreateMigratedDatabaseAsync(Ct);
        return factory.WithWebHostBuilder(b =>
            b.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", connectionString));
    }
}
