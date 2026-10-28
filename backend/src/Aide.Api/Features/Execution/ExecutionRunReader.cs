using System.Text.Json;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Execution;

/// <summary>
/// FR-57, D-V2-14: execution runs and step attempt history. Reads are a fixed number of set-based queries whatever the
/// number of runs and steps (NFR: p95 &lt; 300 ms for 10 cycles × 30 steps); <see cref="ExecutionRunRules"/> derives
/// run order, cycle and status.
/// </summary>
public sealed class ExecutionRunReader(AideDbContext db)
{
    /// <summary>Every run of release version <paramref name="releaseVersionId"/>: the development run first, then the review-fix runs by cycle.</summary>
    public async Task<IReadOnlyList<ExecutionRunDto>> ListAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        if (releaseVersionId is not { } versionId)
        {
            return [];
        }

        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.ReleaseVersionId == versionId)
            .ToListAsync(cancellationToken);
        if (steps.Count == 0)
        {
            return [];
        }

        var planIds = steps.Select(s => s.PlanArtifactId).Distinct().ToList();
        var plans = await db.Artifacts.AsNoTracking()
            .Where(a => planIds.Contains(a.Id))
            .Select(a => new RunPlan(a.Id, a.Type, a.Version, a.Status, a.CreatedAt))
            .ToListAsync(cancellationToken);
        var reports = plans.Any(p => p.Type == ArtifactType.OptimizationPlan)
            ? await ReportsAsync(projectId, versionId, cancellationToken)
            : [];
        var stepIds = steps.Select(s => s.Id).ToList();
        var tokens = await db.AgentRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.StepId != null && stepIds.Contains(r.StepId.Value))
            .GroupBy(r => r.StepId!.Value)
            .Select(g => new { StepId = g.Key, Tokens = g.Sum(r => (long)r.InputTokens + r.OutputTokens) })
            .ToDictionaryAsync(x => x.StepId, x => x.Tokens, cancellationToken);
        var attempts = await db.ExecutionStepAttempts.AsNoTracking()
            .Where(a => stepIds.Contains(a.StepId))
            .GroupBy(a => a.StepId)
            .Select(g => new { StepId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StepId, x => x.Count, cancellationToken);
        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.ExecutionPaused, p.CurrentReleaseVersionId })
            .FirstAsync(cancellationToken);
        var git = await db.ProjectGitSettings.AsNoTracking()
            .Where(g => g.ProjectId == projectId)
            .Select(g => new { g.Provider, RepoUrl = g.Provider == Aide.Domain.Entities.GitProvider.GithubApp ? "https://github.com/" + g.RepoFullName : g.RepoUrl })
            .FirstOrDefaultAsync(cancellationToken);
        var jobActive = await ExecutionQueries.JobActiveAsync(db, projectId, cancellationToken);
        var invalidations = plans.Any(p => p.Status == ArtifactStatus.Invalidated)
            ? await InvalidationsAsync(projectId, plans.Min(p => p.CreatedAt), cancellationToken)
            : [];

        // The current plan (the one the runner executes) is the most recently created locked plan of the current version.
        var currentPlanId = versionId == project.CurrentReleaseVersionId
            ? plans.Where(p => p.Status == ArtifactStatus.Locked).OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Select(p => (Guid?)p.Id).FirstOrDefault()
            : null;
        var stepsByPlan = steps.GroupBy(s => s.PlanArtifactId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.StepNumber).ToList());
        var owners = plans.ToDictionary(p => p.Id, p => ExecutionRunRules.ReportOf(p, reports));

        return
        [
            .. ExecutionRunRules.Order(plans, p => owners[p.Id]).Select(plan =>
            {
                var planSteps = stepsByPlan[plan.Id];
                var status = ExecutionRunRules.Status(
                    plan.Status, [.. planSteps.Select(s => s.Status)], plan.Id == currentPlanId, project.ExecutionPaused, jobActive);
                var superseded = status == PlanRunStatus.Superseded;
                return new ExecutionRunDto(
                    plan.Id,
                    plan.Type,
                    plan.Version,
                    versionId,
                    owners[plan.Id]?.Cycle,
                    status,
                    superseded,
                    superseded ? ExecutionRunRules.SupersededReason(plan.Type, plan.Version, invalidations.GetValueOrDefault(plan.Id)) : null,
                    planSteps.Count,
                    planSteps.Count(s => s.Status == StepStatus.Completed),
                    planSteps.Min(s => s.StartedAt),
                    status == PlanRunStatus.Completed ? planSteps.Max(s => s.CompletedAt) : null,
                    planSteps.Count(s => s.CommitSha is not null),
                    planSteps.Sum(s => tokens.GetValueOrDefault(s.Id)),
                    [
                        .. planSteps.Select(s => new ExecutionStepSummaryDto(
                            s.Id,
                            s.StepNumber,
                            s.Title,
                            s.AgentKey,
                            s.Status,
                            Math.Max(s.Attempt, attempts.GetValueOrDefault(s.Id)),
                            [.. s.DeclaredFiles.Select(f => new DeclaredFileDto(f.Path, f.Action, PathRules.IsProtected(f.Path)))],
                            [.. (s.FileManifest ?? []).Select(f => new FileManifestEntryDto(f.Path, f.Action, f.ContentHash))],
                            s.CommitSha,
                            git is null ? null : ExecutionQueries.CommitUrl(git.Provider, git.RepoUrl, s.CommitSha),
                            s.LegacyAgentKey)),
                    ],
                    owners[plan.Id]?.Id);
            }),
        ];
    }

    /// <summary>
    /// FR-56: the review-fix run (its plan artifact) that report <paramref name="reportId"/>'s optimisation plan produced
    /// in release version <paramref name="releaseVersionId"/>: the latest version of that report's plan with steps, or
    /// <c>null</c>. Linked by report, never by cycle number alone (cycles restart after a rollback).
    /// </summary>
    public async Task<RunPlan?> RunOfReportAsync(Guid projectId, Guid releaseVersionId, Guid reportId, CancellationToken cancellationToken)
    {
        var plans = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == ArtifactType.OptimizationPlan
                && db.ExecutionSteps.Any(s => s.PlanArtifactId == a.Id))
            .Select(a => new RunPlan(a.Id, a.Type, a.Version, a.Status, a.CreatedAt))
            .ToListAsync(cancellationToken);
        if (plans.Count == 0)
        {
            return null;
        }

        var reports = await ReportsAsync(projectId, releaseVersionId, cancellationToken);
        return ExecutionRunRules.RunOfReport(reportId, plans, reports);
    }

    /// <summary>D-V2-14: a step's attempts, oldest first, with each attempt's agent run summary and its repo tool calls.</summary>
    public async Task<IReadOnlyList<StepAttemptDto>> AttemptsAsync(Guid stepId, CancellationToken cancellationToken)
    {
        var attempts = await db.ExecutionStepAttempts.AsNoTracking()
            .Where(a => a.StepId == stepId)
            .OrderBy(a => a.Attempt)
            .ToListAsync(cancellationToken);
        var runIds = attempts.Select(a => a.AgentRunId).OfType<Guid>().Distinct().ToList();
        var runs = runIds.Count == 0
            ? []
            : await db.AgentRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Agent, r.AgentKey, r.Model, r.InputTokens, r.OutputTokens, r.DurationMs, r.Status, r.ToolCalls })
                .ToDictionaryAsync(r => r.Id, cancellationToken);

        return
        [
            .. attempts.Select(a =>
            {
                var run = a.AgentRunId is { } id ? runs.GetValueOrDefault(id) : null;
                return new StepAttemptDto(
                    a.Attempt,
                    a.Outcome,
                    a.CreatedAt,
                    [.. a.Violations.Select(ToDto)],
                    [.. a.Warnings.Select(ToDto)],
                    [.. a.ProducedFiles.Select(f => new FileManifestEntryDto(f.Path, f.Action, f.ContentHash))],
                    run is null ? null : new StepAttemptRunDto(run.Id, run.Agent, run.AgentKey, run.Model, run.InputTokens, run.OutputTokens, run.DurationMs, run.Status),
                    run is null ? [] : ToolCalls(run.ToolCalls));
            }),
        ];
    }

    /// <summary>
    /// FR-71: the run's tool calls from its <c>tool_calls</c> JSON array (<c>{tool, arguments, bytes, truncated}</c> each);
    /// entries that aren't objects with a tool name are skipped, and unreadable JSON reads as none.
    /// </summary>
    public static IReadOnlyList<ToolCallDto> ToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var calls = new List<ToolCallDto>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var arguments = entry.TryGetProperty("arguments", out var args) ? args.Clone() : EmptyObject();
                var bytes = entry.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var n) ? n : 0;
                var truncated = entry.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
                var error = entry.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                calls.Add(new ToolCallDto(tool.GetString()!, arguments, bytes, truncated, error));
            }

            return calls;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task<List<RunReport>> ReportsAsync(Guid projectId, Guid releaseVersionId, CancellationToken cancellationToken) =>
        db.ComplianceReports.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ReleaseVersionId == releaseVersionId)
            .Select(r => new RunReport(r.Id, r.ReviewCycle, r.CreatedAt, r.OptimizationPlanArtifactId))
            .ToListAsync(cancellationToken);

    /// <summary>The latest <c>ARTIFACT_INVALIDATED</c> event of each artifact since <paramref name="since"/>, by artifact id.</summary>
    private async Task<Dictionary<Guid, PlanInvalidation>> InvalidationsAsync(Guid projectId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var payloads = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.ArtifactInvalidated && e.CreatedAt >= since)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Select(e => e.Payload)
            .ToListAsync(cancellationToken);
        var result = new Dictionary<Guid, PlanInvalidation>();
        foreach (var payload in payloads)
        {
            if (ReadInvalidation(payload) is { } read)
            {
                result[read.ArtifactId] = read.Invalidation;
            }
        }

        return result;
    }

    private static (Guid ArtifactId, PlanInvalidation Invalidation)? ReadInvalidation(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("artifactId", out var idElement) || !idElement.TryGetGuid(out var artifactId))
            {
                return null;
            }

            var approvedType = Text(root, "approvedType") is { } wire && EnumStrings.TryParse<ArtifactType>(wire, out var type) ? type : (ArtifactType?)null;
            return (artifactId, new PlanInvalidation(Text(root, "cause"), approvedType, Int(root, "approvedVersion"), Int(root, "targetCheckpoint")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    private static StepViolationDto ToDto(GuardianViolation v) => new(v.Check, v.Code, v.Severity, v.Message, v.Path);

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
