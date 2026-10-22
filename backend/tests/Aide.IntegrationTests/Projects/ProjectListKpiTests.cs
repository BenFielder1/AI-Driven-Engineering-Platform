using System.Net;
using Aide.Api.Features.Projects;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Projects;

/// <summary>§7.2: the projects-list KPI row (<c>GET /api/projects/kpis</c>) over the caller's projects and the list's filters.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectListKpiTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR53_ProjectList_kpis_count_active_in_review_awaiting_gates_and_released_over_the_callers_filtered_projects()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        using var other = await ApiSession.RegisterAsync(factory);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var reviewing = await ProjectApi.CreateAsync(owner.Client, $"kpi-{tag}-reviewing");
        var released = await ProjectApi.CreateAsync(owner.Client, $"kpi-{tag}-released");
        var gated = await ProjectApi.CreateAsync(owner.Client, $"kpi-{tag}-gated");
        await ProjectApi.CreateAsync(other.Client, $"kpi-{tag}-not-mine");

        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Projects.Where(p => p.Id == reviewing.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProjectStatus.Reviewing).SetProperty(p => p.CurrentPhase, Phase.Review), Ct);
            await db.Projects.Where(p => p.Id == released.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProjectStatus.Released).SetProperty(p => p.CurrentPhase, Phase.Release), Ct);
        });
        await LifecycleSeed.AddGateAsync(factory, gated.Id, GateType.SpecApproval);
        await LifecycleSeed.AddGateAsync(factory, gated.Id, GateType.PlanApproval);
        var decided = await LifecycleSeed.AddGateAsync(factory, reviewing.Id, GateType.PreRelease);
        await ProjectApi.WithDbAsync(factory, db => db.ApprovalGates.Where(g => g.Id == decided.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GateStatus.Approved), Ct));

        var all = await KpisAsync(owner, $"?search=kpi-{tag}");
        Assert.Equal(new ProjectKpisDto(3, 2, 1, 2, 1), all);

        // The same filters as the list: the KPI row counts the projects the list shows.
        var list = await ProjectApi.ReadAsync<Aide.Api.Features.Admin.PagedResult<ProjectSummaryDto>>(
            await owner.Client.GetAsync(new Uri($"/api/projects?search=kpi-{tag}&status=reviewing", UriKind.Relative), Ct));
        var filtered = await KpisAsync(owner, $"?search=kpi-{tag}&status=reviewing");
        Assert.Equal(list.Total, filtered.Total);
        Assert.Equal(new ProjectKpisDto(1, 1, 1, 0, 0), filtered);

        Assert.Equal(new ProjectKpisDto(0, 0, 0, 0, 0), await KpisAsync(owner, $"?search=nothing-{tag}"));
        Assert.Equal(new ProjectKpisDto(1, 1, 0, 0, 0), await KpisAsync(other, $"?search=kpi-{tag}"));
        await Problems.AssertAsync(
            await owner.Client.GetAsync(new Uri("/api/projects/kpis?status=nope", UriKind.Relative), Ct), HttpStatusCode.BadRequest, Aide.Domain.Common.ErrorCodes.Validation);
    }

    private static async Task<ProjectKpisDto> KpisAsync(ApiSession session, string query) =>
        await ProjectApi.ReadAsync<ProjectKpisDto>(await session.Client.GetAsync(new Uri($"/api/projects/kpis{query}", UriKind.Relative), Ct));
}
