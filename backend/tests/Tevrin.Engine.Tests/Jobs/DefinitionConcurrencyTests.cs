using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Jobs;

/// <summary>FR-286, §4.59: one-per-project exclusivity comes from the registered definitions' <c>concurrency</c> block.</summary>
public class DefinitionConcurrencyTests
{
    private const string PackJob = "pack_job";

    private static readonly JobHandlerRegistration[] Handlers =
    [
        new(EngineJobTypes.AgentRun, _ => throw new NotSupportedException()),
        new(PackJob, _ => throw new NotSupportedException()),
    ];

    private static PipelineRegistryHost Host(Func<string, string>? edit = null) =>
        new([edit is null ? new FixturePack() : new FixturePack(edit)], [], Handlers);

    [Fact]
    public void FR286_a_job_type_a_definition_lists_with_scope_project_is_one_per_project()
    {
        var host = Host();
        Assert.True(host.Registry!.TryGet("fixture-review", out _), string.Join("; ", host.Registry.Errors.Select(e => e.Error.ToString())));

        var exclusive = JobTypeRegistry.ExclusiveOf(host);
        var registry = new JobTypeRegistry(Handlers, exclusive);

        Assert.Equal([new ExclusiveJob(EngineJobTypes.AgentRun, ConcurrencyScope.Project)], exclusive);
        Assert.True(registry.IsOnePerProject(EngineJobTypes.AgentRun));
        Assert.False(registry.IsOnePerProject(PackJob));
    }

    [Fact]
    public void FR286_without_the_definition_entry_the_type_runs_in_parallel()
    {
        var host = Host(yaml => yaml.Replace("    - { job_type: agent.run, scope: project }\n", string.Empty, StringComparison.Ordinal)
            .Replace("  exclusive:\n", "  exclusive: []\n", StringComparison.Ordinal));
        Assert.True(host.Registry!.TryGet("fixture-review", out _), string.Join("; ", host.Registry.Errors.Select(e => e.Error.ToString())));

        var registry = new JobTypeRegistry(Handlers, JobTypeRegistry.ExclusiveOf(host));

        Assert.False(registry.IsOnePerProject(EngineJobTypes.AgentRun));
    }

    [Fact]
    public void FR286_the_registration_flag_stays_a_fallback_for_types_no_definition_mentions()
    {
        var registry = new JobTypeRegistry(
            [new JobHandlerRegistration(PackJob, _ => throw new NotSupportedException(), OnePerProject: true)],
            JobTypeRegistry.ExclusiveOf(Host()));

        Assert.True(registry.IsOnePerProject(PackJob));
        Assert.False(registry.IsRegistered(EngineJobTypes.AgentRun));
        Assert.Empty(JobTypeRegistry.ExclusiveOf(null));
    }

    [Fact]
    public void FR286_a_run_scoped_entry_is_never_enforced_per_project() =>
        Assert.False(new JobTypeRegistry(Handlers, [new ExclusiveJob(PackJob, ConcurrencyScope.Run)]).IsOnePerProject(PackJob));
}
