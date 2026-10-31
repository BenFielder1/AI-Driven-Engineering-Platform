using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Review;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-147, FR-148, FR-149, FR-150, FR-153, FR-154: rollout planning and sequencing, findings, divergence, reset, migration warning.</summary>
public class RolloutRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly RolloutTarget NetlifyFrontend = new(DeploymentComponent.Frontend, DeploymentProvider.Netlify, true);

    private static readonly RolloutTarget RenderBackend = new(DeploymentComponent.Backend, DeploymentProvider.Render, true);

    private static Dictionary<RolloutStepKind, RolloutStepState> States(params (RolloutStepKind Kind, RolloutStepState State)[] states) =>
        states.ToDictionary(s => s.Kind, s => s.State);

    [Fact]
    public void FR147_FR149_Rollouts_run_migrations_then_backend_then_frontend()
    {
        var plan = RolloutRules.Plan(DeploymentEnvironment.Production, true, [NetlifyFrontend, RenderBackend]);

        Assert.Equal([RolloutStepKind.Migrations, RolloutStepKind.Backend, RolloutStepKind.Frontend], plan.Select(s => s.Kind));
        Assert.All(plan, s => Assert.False(s.Skipped));
    }

    [Fact]
    public void FR147_Host_mode_has_no_migration_step_and_unconfigured_targets_are_left_out()
    {
        var plan = RolloutRules.Plan(DeploymentEnvironment.Preview, false, [NetlifyFrontend, RenderBackend with { Configured = false }]);

        Assert.Equal([RolloutStepKind.Frontend], plan.Select(s => s.Kind));
    }

    [Fact]
    public void FR147_A_host_without_previews_is_skipped_with_a_note_in_preview_only()
    {
        var pages = new RolloutTarget(DeploymentComponent.Frontend, DeploymentProvider.GithubPages, true);

        var preview = Assert.Single(RolloutRules.Plan(DeploymentEnvironment.Preview, false, [pages]));
        var production = Assert.Single(RolloutRules.Plan(DeploymentEnvironment.Production, false, [pages]));

        Assert.True(preview.Skipped);
        Assert.Contains("GitHub Pages", preview.Note, StringComparison.Ordinal);
        Assert.False(production.Skipped);
        Assert.Equal(RolloutDecisionKind.Completed, RolloutRules.Next([preview], States()).Kind);
    }

    [Fact]
    public void FR147_Each_step_waits_for_the_previous_one_to_be_ready()
    {
        var plan = RolloutRules.Plan(DeploymentEnvironment.Preview, true, [NetlifyFrontend, RenderBackend]);

        Assert.Equal(new RolloutDecision(RolloutDecisionKind.Start, RolloutStepKind.Migrations), RolloutRules.Next(plan, States()));
        Assert.Equal(new RolloutDecision(RolloutDecisionKind.Start, RolloutStepKind.Backend), RolloutRules.Next(plan, States((RolloutStepKind.Migrations, RolloutStepState.Succeeded))));
        Assert.Equal(
            new RolloutDecision(RolloutDecisionKind.Wait, RolloutStepKind.Backend),
            RolloutRules.Next(plan, States((RolloutStepKind.Migrations, RolloutStepState.Succeeded), (RolloutStepKind.Backend, RolloutStepState.Running))));
        Assert.Equal(
            new RolloutDecision(RolloutDecisionKind.Start, RolloutStepKind.Frontend),
            RolloutRules.Next(plan, States((RolloutStepKind.Migrations, RolloutStepState.Succeeded), (RolloutStepKind.Backend, RolloutStepState.Succeeded))));
        Assert.Equal(
            RolloutDecisionKind.Completed,
            RolloutRules.Next(plan, States((RolloutStepKind.Migrations, RolloutStepState.Succeeded), (RolloutStepKind.Backend, RolloutStepState.Succeeded), (RolloutStepKind.Frontend, RolloutStepState.Succeeded))).Kind);
    }

    [Fact]
    public void FR147_A_failure_stops_the_later_steps()
    {
        var plan = RolloutRules.Plan(DeploymentEnvironment.Preview, true, [NetlifyFrontend, RenderBackend]);

        Assert.Equal(new RolloutDecision(RolloutDecisionKind.Failed, RolloutStepKind.Migrations), RolloutRules.Next(plan, States((RolloutStepKind.Migrations, RolloutStepState.Failed))));
        Assert.Equal(
            new RolloutDecision(RolloutDecisionKind.Failed, RolloutStepKind.Backend),
            RolloutRules.Next(plan, States((RolloutStepKind.Migrations, RolloutStepState.Succeeded), (RolloutStepKind.Backend, RolloutStepState.Failed))));
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued, RolloutStepState.Running)]
    [InlineData(DeploymentStatus.Building, RolloutStepState.Running)]
    [InlineData(DeploymentStatus.Ready, RolloutStepState.Succeeded)]
    [InlineData(DeploymentStatus.Error, RolloutStepState.Failed)]
    [InlineData(DeploymentStatus.Canceled, RolloutStepState.Failed)]
    public void FR147_A_deployment_state_is_a_step_state(DeploymentStatus status, RolloutStepState expected) =>
        Assert.Equal(expected, RolloutRules.StateOf(status));

    [Theory]
    [InlineData("Render: the pre-deploy command failed (pre_deploy_failed).", true)]
    [InlineData("The Fake pre-deploy migration failed: the commit contains .aide-fake/fail-migration.", true)]
    [InlineData("The Fake build failed: the commit contains .aide-fake/fail-build.", false)]
    [InlineData(null, false)]
    public void FR154_A_pre_deploy_failure_is_recognised(string? message, bool expected) =>
        Assert.Equal(expected, RolloutRules.IsPreDeployFailure(message));

    [Fact]
    public void FR147_A_failed_build_is_a_critical_architecture_finding_with_the_log_tail()
    {
        var finding = Assert.Single(RolloutFindings.For(
            [new RolloutComponentOutcome(DeploymentComponent.Frontend, DeploymentProvider.Vercel, DeploymentStatus.Error, ErrorCodes.PlatformError, "failed", "Type error: x", null)],
            null));

        Assert.Equal((ReviewCategory.Architecture, Severity.Critical, "Vercel preview build failed", "Type error: x"), (finding.Category, finding.Severity, finding.Message, finding.Evidence));
        Assert.Equal(FindingSource.Deterministic, finding.Source);
    }

    [Fact]
    public void FR147_FR154_A_failed_backend_pre_deploy_is_a_critical_finding_with_the_pre_deploy_log()
    {
        var finding = Assert.Single(RolloutFindings.For(
            [new RolloutComponentOutcome(DeploymentComponent.Backend, DeploymentProvider.Render, DeploymentStatus.Error, ErrorCodes.MigrationFailed, "pre_deploy_failed", "==> Pre-deploy command failed", null)],
            null));

        Assert.Equal((ReviewCategory.Architecture, Severity.Critical, RolloutFindings.PreDeployFailedMessage), (finding.Category, finding.Severity, finding.Message));
        Assert.Contains("Pre-deploy", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void FR147_FR153_A_failed_preview_migration_is_a_critical_data_model_finding_and_advisors_are_security_findings()
    {
        var findings = RolloutFindings.For(
            [],
            new RolloutMigrationOutcome(
                MigrationRunStatus.Failed,
                ErrorCodes.MigrationFailed,
                "0003_orders.sql failed (SQLSTATE 42P07): relation exists",
                [
                    new AdvisorFinding("rls_disabled_in_public", "ERROR", "RLS Disabled in Public", "orders has RLS disabled", null),
                    new AdvisorFinding("function_search_path_mutable", "WARN", "Function Search Path Mutable", null, null),
                    new AdvisorFinding("info", "INFO", "Just so you know", null, null),
                ]));

        Assert.Equal(
            [(ReviewCategory.DataModel, Severity.Critical), (ReviewCategory.Security, Severity.Major), (ReviewCategory.Security, Severity.Minor)],
            findings.Select(f => (f.Category, f.Severity)));
        Assert.Equal(RolloutFindings.MigrationFailedMessage, findings[0].Message);
        Assert.Equal("Supabase advisor: RLS Disabled in Public", findings[1].Message);
    }

    [Fact]
    public void FR148_A_diverged_preview_migration_history_is_its_own_finding()
    {
        var finding = Assert.Single(RolloutFindings.For([], new RolloutMigrationOutcome(MigrationRunStatus.Failed, ErrorCodes.PreviewDatabaseDiverged, "0003 missing", [])));

        Assert.Equal((ReviewCategory.DataModel, RolloutFindings.DivergedMessage), (finding.Category, finding.Message));
    }

    [Fact]
    public void FR147_A_ready_rollout_has_no_findings() =>
        Assert.Empty(RolloutFindings.For(
            [new RolloutComponentOutcome(DeploymentComponent.Backend, DeploymentProvider.Render, DeploymentStatus.Ready, null, null, null, "https://api-preview.onrender.com")],
            new RolloutMigrationOutcome(MigrationRunStatus.Succeeded, null, null, [])));

    [Theory]
    [InlineData("supabase/migrations/20261031_0003_orders.sql", true)]
    [InlineData("backend/src/App.Infrastructure/Migrations/20261031_AddOrders.cs", true)]
    [InlineData("backend/src/App.Infrastructure/Migrations/20261031_AddOrders.Designer.cs", false)]
    [InlineData("backend/src/App.Infrastructure/Migrations/AppDbContextModelSnapshot.cs", false)]
    [InlineData("prisma/migrations/20261031_orders/migration.sql", true)]
    [InlineData("prisma/migrations/migration_lock.toml", false)]
    [InlineData("alembic/versions/0003_orders.py", true)]
    [InlineData("drizzle/migrations/meta/_journal.json", false)]
    [InlineData("src/orders.ts", false)]
    [InlineData("migrations", false)]
    public void FR148_Migration_files_are_recognised_across_data_packs(string path, bool expected) =>
        Assert.Equal(expected, PreviewDatabaseRules.IsMigrationFile(path));

    [Fact]
    public void FR148_A_rollback_before_migration_0003_makes_the_preview_database_diverge()
    {
        string[] applied = ["supabase/migrations/0001_init.sql", "supabase/migrations/0002_items.sql", "supabase/migrations/0003_orders.sql", "src/app.ts"];
        string[] head = ["supabase/migrations/0001_init.sql", "supabase/migrations/0002_items.sql", "src/app.ts"];

        var missing = PreviewDatabaseRules.Missing(applied, head);

        Assert.Equal(["supabase/migrations/0003_orders.sql"], missing);
        Assert.Contains("0003_orders.sql", PreviewDatabaseRules.Describe(missing), StringComparison.Ordinal);
        Assert.Empty(PreviewDatabaseRules.Missing(head, applied));
        Assert.Empty(PreviewDatabaseRules.Missing(null, head));
    }

    [Theory]
    [InlineData(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Available, PreviewResetMethod.ResetSchema)]
    [InlineData(ManagedDatabaseProvider.External, ManagedDatabaseStatus.Available, PreviewResetMethod.ResetSchema)]
    [InlineData(ManagedDatabaseProvider.Render, ManagedDatabaseStatus.Expiring, PreviewResetMethod.ResetSchemaExternal)]
    [InlineData(ManagedDatabaseProvider.Railway, ManagedDatabaseStatus.Available, PreviewResetMethod.Manual)]
    [InlineData(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Provisioning, PreviewResetMethod.None)]
    [InlineData(ManagedDatabaseProvider.Supabase, ManagedDatabaseStatus.Removed, PreviewResetMethod.None)]
    public void FR148_The_reset_method_follows_the_provider(ManagedDatabaseProvider provider, ManagedDatabaseStatus status, PreviewResetMethod expected) =>
        Assert.Equal(expected, PreviewDatabaseRules.ResetMethod(provider, status));

    [Fact]
    public void FR148_No_preview_database_has_no_reset() =>
        Assert.Equal(PreviewResetMethod.None, PreviewDatabaseRules.ResetMethod(null, null));

    [Fact]
    public void FR150_A_deployment_older_than_the_latest_migration_is_warned_about()
    {
        Assert.True(MigrationWarningRules.OlderThanLatestMigration(T0, T0.AddMinutes(5)));
        Assert.False(MigrationWarningRules.OlderThanLatestMigration(T0.AddMinutes(10), T0.AddMinutes(5)));
        Assert.Null(MigrationWarningRules.OlderThanLatestMigration(T0, null));
        Assert.Equal(T0.AddMinutes(7), MigrationWarningRules.Latest(T0.AddMinutes(7), T0.AddMinutes(3)));
        Assert.Equal(T0, MigrationWarningRules.Latest(null, T0));
        Assert.Null(MigrationWarningRules.Latest(null, null));
    }
}
