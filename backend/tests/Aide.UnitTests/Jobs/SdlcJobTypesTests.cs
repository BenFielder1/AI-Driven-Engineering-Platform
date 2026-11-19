using Aide.Api.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Jobs;

/// <summary>FR-272: the SDLC registers every 0.6.1 job type string unchanged, and a handler for each type it enqueues.</summary>
public class SdlcJobTypesTests
{
    [Fact]
    public void FR272_the_SDLC_job_types_are_the_0_6_1_strings_unchanged() =>
        Assert.Equal(
            [
                "idea_refinement", "spec_generation", "plan_generation", "execution", "pull_request", "review", "release",
                "merge_poll", "merge_readiness", "repository_analysis", "stack_analysis", "tracker_import", "deployment",
                "deployment_setup", "database_provisioning", "preview_rollout", "production_rollout", "preview_database_reset",
                "database_expiry_sweep", "notification", "build_check", "build_fix",
            ],
            SdlcJobTypes.All);

    [Fact]
    public void FR272_the_SDLC_registers_a_handler_for_every_job_type_it_enqueues()
    {
        var registrations = Registrations();

        Assert.Equal(
            SdlcJobTypes.All.Order(StringComparer.Ordinal),
            registrations.Select(r => r.JobType).Order(StringComparer.Ordinal));
        var registry = new JobTypeRegistry(registrations);
        Assert.All(SdlcJobTypes.All, type => Assert.True(registry.IsRegistered(type), type));
    }

    [Fact]
    public void FR272_FR286_only_execution_is_one_per_project_and_the_SDLC_definition_says_so()
    {
        var host = Aide.UnitTests.Lifecycle.SdlcPipelineFixture.Services.GetRequiredService<PipelineRegistryHost>();
        var registry = new JobTypeRegistry(Registrations(), JobTypeRegistry.ExclusiveOf(host));

        Assert.Equal([SdlcJobTypes.Execution], SdlcJobTypes.All.Where(registry.IsOnePerProject));
        Assert.Equal([new ExclusiveJob(SdlcJobTypes.Execution, ConcurrencyScope.Project)], JobTypeRegistry.ExclusiveOf(host));

        // FR-286: the definition, not a code flag, makes it exclusive.
        Assert.DoesNotContain(Registrations(), r => r.OnePerProject);
        Assert.DoesNotContain(SdlcJobTypes.All, new JobTypeRegistry(Registrations()).IsOnePerProject);
    }

    [Fact]
    public void FR286_the_one_per_project_conflict_message_is_unchanged() =>
        Assert.Equal(
            "An execution job is already queued or running for this project; retry this dead letter after it finishes.",
            JobPolicy.OnePerProjectActiveMessage(SdlcJobTypes.Execution));

    [Fact]
    public void FR272_SDLC_job_types_never_collide_with_the_engine_job_types() =>
        Assert.Empty(SdlcJobTypes.All.Intersect(EngineJobTypes.All, StringComparer.Ordinal));

    [Fact]
    public void FR272_every_agent_runs_as_a_registered_SDLC_job_type() =>
        Assert.All(SdlcAgents.All, agent => Assert.Contains(SdlcJobTypes.ForAgent(agent), SdlcJobTypes.All));

    private static List<JobHandlerRegistration> Registrations() =>
    [
        .. new ServiceCollection().AddSdlcJobHandlers()
            .Where(d => d.ServiceType == typeof(JobHandlerRegistration))
            .Select(d => Assert.IsType<JobHandlerRegistration>(d.ImplementationInstance)),
    ];
}
