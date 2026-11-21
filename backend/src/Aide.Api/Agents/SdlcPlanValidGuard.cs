using Aide.Api.Features.Stack;
using Aide.Domain.Execution;
using Aide.Domain.Stack;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

/// <summary>FR-29, FR-124, FR-243: what a generated plan is checked against: the project's limit and its version's stack.</summary>
public sealed record SdlcPlanCheck(int MaxFilesPerStep, PlanAgents? Agents, DeployFileCheck? DeployFiles)
{
    /// <summary>0.6.1's plan check and content: read and validate the reply (deploy-file steps added), then render and prove it.</summary>
    public Result<string> ToContent(string reply)
    {
        var plan = PlanDocument.ParseAgentOutput(reply, MaxFilesPerStep, Agents, DeployFiles);
        return plan.IsSuccess
            ? PlanDocument.RenderChecked(SdlcArtifacts.ImplementationPlan, plan.Value, MaxFilesPerStep, Agents)
            : Result.Failure<string>(plan.Error!);
    }

    /// <summary>The check for a project and its current version's stack, as <see cref="PlanGenerationJobHandler"/> builds it.</summary>
    public static SdlcPlanCheck Of(int maxFilesPerStep, VersionStackContext version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var deployFiles = version.Stack is { } composed
            ? new DeployFileCheck(StackRequiredFiles.For(composed), version.BaseFiles, Optimisation: false)
            : null;
        return new SdlcPlanCheck(maxFilesPerStep, version.PlanAgents(SdlcArtifacts.ImplementationPlan), deployFiles);
    }
}

/// <summary>
/// FR-321: the plan check of each project for this DI scope: the one the plan job built (no second stack read), else read
/// from the project and its version's stack.
/// </summary>
public sealed class SdlcPlanChecks(IServiceProvider services)
{
    private readonly Dictionary<Guid, SdlcPlanCheck> _byProject = [];

    public void Set(Guid projectId, SdlcPlanCheck check) => _byProject[projectId] = check;

    public async Task<SdlcPlanCheck> ForAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (_byProject.TryGetValue(projectId, out var known))
        {
            return known;
        }

        // Resolved only when needed: the plan job sets its check, so a job never reads the stack twice.
        var project = await services.GetRequiredService<AgentJobInputs>().ProjectAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} no longer exists.");
        var check = SdlcPlanCheck.Of(project.MaxFilesPerStep, (await services.GetRequiredService<VersionStackLoader>().CurrentAsync(projectId, cancellationToken)).ThrowIfUnresolvable());
        _byProject[projectId] = check;
        return check;
    }
}

/// <summary>
/// FR-314, FR-321: <c>sdlc.plan-valid</c>, the <c>planning</c> stage's <c>validate:</c> guard: the stage output read and
/// validated by the plan rules and proven to render (<see cref="SdlcPlanCheck.ToContent"/>), with the error's message as
/// its one reason, exactly the host check it replaces. Without a stage output (a lifecycle evaluation) it passes: it is
/// never an entry rule. Its services are resolved only when it checks an output.
/// </summary>
public sealed class SdlcPlanValidGuard(IServiceProvider services) : IGuard
{
    public async Task<GuardResult> EvaluateAsync(GuardContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Facts.Get<StageOutputFact>() is not { } output)
        {
            return GuardResult.Pass;
        }

        var check = await services.GetRequiredService<SdlcPlanChecks>().ForAsync(context.ProjectId, cancellationToken);
        var content = check.ToContent(output.Content);
        return content.IsSuccess ? GuardResult.Pass : GuardResult.Fail(content.Error!.Message);
    }
}
