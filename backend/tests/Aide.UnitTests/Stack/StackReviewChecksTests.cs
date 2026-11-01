using Aide.Domain.Review;
using Aide.Domain.Stack;
using static Aide.UnitTests.Stack.StackResolverTests;
using static Aide.UnitTests.Stack.StackTestCatalog;

namespace Aide.UnitTests.Stack;

/// <summary>FR-126, §4.18 "Review checks": each deterministic check's hit and near miss, and the FR-126 worked examples.</summary>
public class StackReviewChecksTests
{
    private static SourceFile F(string path, string content) => new(path, content);

    private static IReadOnlyList<ReviewFinding> Run(StackResolution resolution, params SourceFile[] files) =>
        StackReviewChecks.Run(resolution, new StackReviewInput(files));

    private static Dictionary<ReviewCategory, decimal> Perfect() => ComplianceScoring.Weights.Keys.ToDictionary(c => c, _ => 1m);

    // ── FR-126 worked examples ───────────────────────────────────────────

    [Fact]
    public void FR126_A_VITE_secret_is_a_critical_security_finding_costing_020()
    {
        var resolution = Resolve(AspnetSpa());

        var findings = Run(resolution, F("frontend/src/lib/stripe.ts", "export const key = import.meta.env.VITE_STRIPE_SECRET_KEY;\n"));

        var finding = Assert.Single(findings);
        Assert.Equal((ReviewCategory.Security, Severity.Critical, FindingSource.Deterministic), (finding.Category, finding.Severity, finding.Source));
        Assert.Contains("secret-like value exposed to the browser", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.80m, ComplianceScoring.Score(Perfect(), findings).CategoryScores[ReviewCategory.Security]);

        Assert.Empty(Run(resolution, F("frontend/src/lib/api.ts", "const base = import.meta.env.VITE_API_BASE_URL;")));
    }

    [Fact]
    public void FR126_EnsureCreated_in_app_code_is_a_major_architecture_finding_costing_010()
    {
        var resolution = Resolve(AspnetSpa());

        var findings = Run(resolution, F("backend/src/Shop.Api/Program.cs", "app.Services.GetRequiredService<ShopDb>().Database.EnsureCreated();\napp.MapGet(\"/api/health/live\", () => 1);\napp.MapGet(\"/api/health/ready\", () => 1);"));

        var finding = Assert.Single(findings);
        Assert.Equal((ReviewCategory.Architecture, Severity.Major), (finding.Category, finding.Severity));
        Assert.Equal(0.90m, ComplianceScoring.Score(Perfect(), findings).CategoryScores[ReviewCategory.Architecture]);

        Assert.Empty(StackReviewChecks.EnsureCreated([F("backend/tests/Shop.IntegrationTests/Fixture.cs", "db.Database.EnsureCreated();")]));
        Assert.Single(StackReviewChecks.EnsureCreated([F("backend/src/Shop.Api/Startup.cs", "await db.Database.MigrateAsync(ct);")]));
        Assert.Empty(StackReviewChecks.EnsureCreated([F("backend/src/Shop.Api/Startup.cs", "// never call db.Database.Migrate() here")]));
    }

    // ── Supabase ─────────────────────────────────────────────────────────

    [Fact]
    public void FR126_rls_on_every_table_runs_only_for_Supabase_BaaS()
    {
        var create = F("supabase/migrations/20261028_todos.sql", "create table public.todos (id uuid primary key);\ngrant select on public.todos to authenticated;");
        var baas = Resolve(Sel("fe-vue", "none", "data-supabase", "vcs-github"));

        var finding = Assert.Single(Run(baas, create));
        Assert.Equal((ReviewCategory.Security, Severity.Critical), (finding.Category, finding.Severity));
        Assert.Empty(Run(baas, create with { Content = create.Content + "\nalter table public.todos enable row level security;" }));

        Assert.Empty(Run(Resolve(AspnetSpa(databaseHost: "supabase", backendHost: "deploy-render")), create));
    }

    [Fact]
    public void FR126_grants_for_exposed_tables_needs_a_grant_to_authenticated_or_anon()
    {
        var finding = Assert.Single(StackReviewChecks.GrantsForExposed([F("supabase/migrations/1_todos.sql", "create table todos (id uuid);\nalter table todos enable row level security;")]));
        Assert.Equal((ReviewCategory.ApiContracts, Severity.Major), (finding.Category, finding.Severity));
        Assert.Contains("not exposed to the Data API", finding.Message, StringComparison.Ordinal);

        Assert.Empty(StackReviewChecks.GrantsForExposed([F("supabase/migrations/1_todos.sql", "create table todos (id uuid);\ngrant select, insert on table public.todos to authenticated;")]));
        Assert.Empty(StackReviewChecks.GrantsForExposed([F("supabase/migrations/1_todos.sql", "create table todos (id uuid);\ngrant select on all tables in schema public to anon;")]));
        Assert.Empty(StackReviewChecks.GrantsForExposed([F("supabase/migrations/1_audit.sql", "create table private.audit (id uuid);")]));
        Assert.Single(StackReviewChecks.GrantsForExposed([F("supabase/migrations/1_todos.sql", "create table todos (id uuid);\ngrant all on public.todos to service_role;")]));
    }

    // ── secrets ──────────────────────────────────────────────────────────

    [Fact]
    public void FR126_no_secret_in_client_flags_non_public_env_in_use_client_files_and_secret_Angular_keys()
    {
        var hit = Assert.Single(StackReviewChecks.SecretInClient([F("frontend/src/app/pay/form.tsx", "'use client'\nconst k = process.env.STRIPE_KEY;")], "frontend", "NEXT_PUBLIC_"));
        Assert.Contains("STRIPE_KEY", hit.Message, StringComparison.Ordinal);
        Assert.Empty(StackReviewChecks.SecretInClient([F("frontend/src/app/pay/form.tsx", "'use client'\nconst a = process.env.NEXT_PUBLIC_API_BASE_URL; const m = process.env.NODE_ENV;")], "frontend", "NEXT_PUBLIC_"));
        Assert.Empty(StackReviewChecks.SecretInClient([F("frontend/src/server/pay.ts", "const k = process.env.STRIPE_KEY;")], "frontend", "NEXT_PUBLIC_"));
        Assert.Single(StackReviewChecks.SecretInClient([F("frontend/src/lib/supabase.ts", "export const key = process.env.NEXT_PUBLIC_SUPABASE_SERVICE_ROLE_KEY;")], "frontend", "NEXT_PUBLIC_"));
        Assert.Single(StackReviewChecks.SecretInClient([F("frontend/.env", "VITE_SUPABASE_KEY=sb_secret_abc123")], "frontend", "VITE_"));

        Assert.Single(StackReviewChecks.SecretInClient([F("frontend/src/environments/environment.prod.ts", "export const environment = { apiBaseUrl: '/api', stripeSecret: 'x' };")], "frontend", null));
        Assert.Empty(StackReviewChecks.SecretInClient([F("frontend/src/environments/environment.ts", "export const environment = { apiBaseUrl: '/api', supabaseUrl: 'https://x.supabase.co' };")], "frontend", null));
        Assert.Empty(StackReviewChecks.SecretInClient([F("backend/src/config.ts", "process.env.VITE_SECRET")], "frontend", "VITE_"));
    }

    [Fact]
    public void FR126_no_committed_secrets_matches_the_vcs_secret_scan_patterns_outside_example_files()
    {
        var hit = Assert.Single(StackReviewChecks.CommittedSecrets([F("deploy/app.env", "DB_PASSWORD=hunter2\n")]));
        Assert.Equal((ReviewCategory.Security, Severity.Critical), (hit.Category, hit.Severity));
        Assert.Contains("line 1", hit.Message, StringComparison.Ordinal);
        Assert.Single(StackReviewChecks.CommittedSecrets([F("keys/id_rsa", "-----BEGIN RSA PRIVATE KEY-----")]));
        Assert.Single(StackReviewChecks.CommittedSecrets([F("src/pay.ts", "const k = 'sk_live_abcdefgh1234';")]));
        Assert.Single(StackReviewChecks.CommittedSecrets([F("src/admin.ts", "const role = 'service_role';")]));

        Assert.Empty(StackReviewChecks.CommittedSecrets([F(".env.example", "DB_PASSWORD=change-me")]));
        Assert.Empty(StackReviewChecks.CommittedSecrets([F(".github/workflows/ci.yml", "        run: echo PASSWORD=${{ secrets.DB_PASSWORD }}\nAPI_KEY=")]));
        Assert.Empty(StackReviewChecks.CommittedSecrets([F("supabase/migrations/1.sql", "grant all on public.todos to service_role;")]));
    }

    // ── ORM rules ────────────────────────────────────────────────────────

    [Fact]
    public void FR126_no_ddl_auto_update_outside_test_config()
    {
        var hit = Assert.Single(StackReviewChecks.DdlAutoUpdate([F("backend/src/main/resources/application.properties", "spring.jpa.hibernate.ddl-auto=update")]));
        Assert.Equal((ReviewCategory.Architecture, Severity.Major), (hit.Category, hit.Severity));
        Assert.Single(StackReviewChecks.DdlAutoUpdate([F("backend/src/main/resources/application.yml", "spring:\n  jpa:\n    hibernate:\n      ddl-auto: create-drop")]));
        Assert.Empty(StackReviewChecks.DdlAutoUpdate([F("backend/src/main/resources/application.properties", "spring.jpa.hibernate.ddl-auto=validate")]));
        Assert.Empty(StackReviewChecks.DdlAutoUpdate([F("backend/src/test/resources/application.yml", "ddl-auto: update")]));
        Assert.Empty(StackReviewChecks.DdlAutoUpdate([F("backend/src/main/resources/application-test.yml", "ddl-auto: update")]));
    }

    [Fact]
    public void FR126_no_create_all_outside_tests()
    {
        Assert.Single(StackReviewChecks.CreateAll([F("backend/app/main.py", "Base.metadata.create_all(engine)")]));
        Assert.Empty(StackReviewChecks.CreateAll([F("backend/tests/conftest.py", "Base.metadata.create_all(engine)")]));
        Assert.Empty(StackReviewChecks.CreateAll([F("backend/app/main.py", "# never Base.metadata.create_all(engine)")]));
    }

    [Fact]
    public void FR126_no_schema_push_in_package_scripts()
    {
        var hit = Assert.Single(StackReviewChecks.SchemaPush([F("frontend/package.json", "{\"scripts\":{\"db:push\":\"prisma db push\",\"build\":\"next build\"}}")]));
        Assert.Contains("db:push", hit.Message, StringComparison.Ordinal);
        Assert.Single(StackReviewChecks.SchemaPush([F("backend/package.json", "{\"scripts\":{\"db\":\"drizzle-kit push\"}}")]));
        Assert.Empty(StackReviewChecks.SchemaPush([F("frontend/package.json", "{\"scripts\":{\"db:migrate\":\"prisma migrate deploy\"},\"description\":\"never prisma db push\"}")]));
    }

    [Fact]
    public void FR126_migration_backward_compatible_flags_destructive_changes_in_new_migrations()
    {
        var flyway = Resolve(Sel("fe-angular", "be-spring-boot", "data-jpa-flyway", "vcs-github"));
        var drop = F("backend/src/main/resources/db/migration/V3__drop_note.sql", "-- drop column later\nALTER TABLE orders DROP COLUMN note;");

        var hit = Assert.Single(StackReviewChecks.Run(flyway, new StackReviewInput([drop])), f => f.Category == ReviewCategory.DataModel);
        Assert.Equal(Severity.Major, hit.Severity);
        Assert.Contains("expand/contract", hit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StackReviewChecks.Run(flyway, new StackReviewInput([drop], BaseFiles: new HashSet<string> { drop.Path })), f => f.Category == ReviewCategory.DataModel);
        Assert.DoesNotContain(StackReviewChecks.Run(flyway, new StackReviewInput([drop with { Content = "ALTER TABLE orders ADD COLUMN note text;" }])), f => f.Category == ReviewCategory.DataModel);

        static bool Always(string _) => true;
        Assert.Single(StackReviewChecks.BackwardCompatible([F("m/1.sql", "alter table todos rename column title to name;")], Always, Always));
        Assert.Single(StackReviewChecks.BackwardCompatible([F("m/1.sql", "ALTER TABLE todos ALTER COLUMN qty TYPE bigint;")], Always, Always));
        Assert.Empty(StackReviewChecks.BackwardCompatible([F("m/1.sql", "ALTER TABLE todos ALTER COLUMN qty SET NOT NULL;")], Always, Always));

        const string efUp = "protected override void Up(MigrationBuilder migrationBuilder) { migrationBuilder.CreateTable(name: \"Orders\"); }\nprotected override void Down(MigrationBuilder migrationBuilder) { migrationBuilder.DropTable(name: \"Orders\"); }";
        Assert.Empty(StackReviewChecks.BackwardCompatible([F("m/20261028_Add.cs", efUp)], Always, Always));
        Assert.Single(StackReviewChecks.BackwardCompatible([F("m/20261028_Drop.cs", "void Up(MigrationBuilder b) { b.DropColumn(name: \"Note\", table: \"Orders\"); }\nvoid Down(MigrationBuilder b) { }")], Always, Always));
        Assert.Single(StackReviewChecks.BackwardCompatible([F("m/20261028_Type.cs", "void Up(MigrationBuilder b) { b.AlterColumn<string>(name: \"Name\", table: \"T\", type: \"text\", nullable: false, oldClrType: typeof(string), oldType: \"character varying(100)\"); }")], Always, Always));
        Assert.Empty(StackReviewChecks.BackwardCompatible([F("m/20261028_Null.cs", "void Up(MigrationBuilder b) { b.AlterColumn<string>(name: \"Name\", table: \"T\", type: \"text\", nullable: true, oldClrType: typeof(string), oldType: \"text\"); }")], Always, Always));

        Assert.Single(StackReviewChecks.BackwardCompatible([F("alembic/versions/2.py", "def upgrade():\n    op.drop_column('orders', 'note')\n\ndef downgrade():\n    op.add_column('orders', sa.Column('note'))")], Always, Always));
        Assert.Empty(StackReviewChecks.BackwardCompatible([F("alembic/versions/1.py", "def upgrade():\n    op.create_table('orders')\n\ndef downgrade():\n    op.drop_table('orders')")], Always, Always));
    }

    // ── containers, deploys, health ──────────────────────────────────────

    [Fact]
    public void FR126_container_non_root_needs_a_non_root_USER_in_the_final_stage()
    {
        var hit = Assert.Single(StackReviewChecks.NonRootContainer([F("backend/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nCOPY . .")], "backend"));
        Assert.Equal((ReviewCategory.Security, Severity.Minor), (hit.Category, hit.Severity));
        Assert.Empty(StackReviewChecks.NonRootContainer([F("backend/Dockerfile", "FROM sdk AS build\nRUN x\nFROM aspnet\nUSER $APP_UID")], "backend"));
        Assert.Single(StackReviewChecks.NonRootContainer([F("backend/Dockerfile", "FROM sdk AS build\nUSER app\nFROM aspnet\nCOPY . .")], "backend"));
        Assert.Single(StackReviewChecks.NonRootContainer([F("backend/Dockerfile", "FROM node\nUSER root")], "backend"));
        Assert.Empty(StackReviewChecks.NonRootContainer([F("frontend/Dockerfile", "FROM nginx")], "backend"));
    }

    [Fact]
    public void FR126_container_non_root_is_registered_only_when_the_backend_runs_in_a_container()
    {
        Assert.Contains(StackReviewChecks.ContainerNonRoot, Resolve(AspnetSpa(backendHost: "deploy-render", local: "none")).ReviewChecks);
        Assert.Contains(StackReviewChecks.ContainerNonRoot, Resolve(AspnetSpa(local: "compose")).ReviewChecks);
        Assert.DoesNotContain(StackReviewChecks.ContainerNonRoot, Resolve(Sel("fe-react-vite", "be-fastapi", "data-sqlalchemy", "vcs-github", frontendHost: "deploy-vercel", backendHost: "deploy-vercel", databaseHost: "external", local: "none")).ReviewChecks);
    }

    [Fact]
    public void FR126_deploy_branch_control_checks_vercel_json_and_netlify_toml()
    {
        var vercel = Resolve(AspnetSpa(frontendHost: "deploy-vercel", backendHost: "deploy-render"));
        var hit = Assert.Single(StackReviewChecks.Run(vercel, new StackReviewInput([F("frontend/vercel.json", "{\"git\":{}}")])));
        Assert.Equal((ReviewCategory.Architecture, Severity.Major, "frontend/vercel.json"), (hit.Category, hit.Severity, hit.Path));
        Assert.Empty(StackReviewChecks.Run(vercel, new StackReviewInput([F("frontend/vercel.json", "{\"git\":{\"deploymentEnabled\":{\"aide/**\":false}}}")])));
        Assert.Empty(StackReviewChecks.Run(vercel, new StackReviewInput([])));
        Assert.Contains("missing", Assert.Single(StackReviewChecks.Run(vercel, new StackReviewInput([], Tree: ["frontend/package.json"]))).Message, StringComparison.Ordinal);

        var netlify = Resolve(AspnetSpa(frontendHost: "deploy-netlify", backendHost: "deploy-render"));
        Assert.Single(StackReviewChecks.Run(netlify, new StackReviewInput([F("frontend/netlify.toml", "[build]\n  command = \"npm run build\"")])));
        Assert.Empty(StackReviewChecks.Run(netlify, new StackReviewInput([F("frontend/netlify.toml", "[build]\n  ignore = 'case \"$BRANCH\" in aide/*) exit 0 ;; *) exit 1 ;; esac'")])));
    }

    [Fact]
    public void FR126_health_endpoints_must_appear_in_the_backend_source()
    {
        var hit = Assert.Single(StackReviewChecks.Health([F("backend/src/Shop.Api/Program.cs", "app.MapGet(\"/api/orders\", () => 1);")], "be-aspnet", "backend"));
        Assert.Equal((ReviewCategory.FeatureCoverage, Severity.Minor), (hit.Category, hit.Severity));
        Assert.Empty(StackReviewChecks.Health([F("backend/src/Shop.Api/Program.cs", "var g = app.MapGroup(\"/api/health\"); g.MapHealthChecks(\"/live\"); g.MapHealthChecks(\"/ready\");")], "be-aspnet", "backend"));
        Assert.Empty(StackReviewChecks.Health([F("frontend/src/app.tsx", "nothing")], "be-aspnet", "backend"));
        Assert.Empty(StackReviewChecks.Health([F("backend/src/main/resources/application.properties", "management.endpoint.health.probes.enabled=true")], "be-spring-boot", "backend"));
        Assert.Single(StackReviewChecks.Health([F("backend/src/main/resources/application.properties", "server.port=8080")], "be-spring-boot", "backend"));
    }

    [Fact]
    public void FR126_IsTestPath_recognises_test_folders_and_files()
    {
        Assert.True(StackReviewChecks.IsTestPath("backend/tests/Shop.IntegrationTests/Fixture.cs"));
        Assert.True(StackReviewChecks.IsTestPath("backend/Shop.UnitTests/OrderTests.cs"));
        Assert.True(StackReviewChecks.IsTestPath("backend/src/test/java/OrderIT.java"));
        Assert.True(StackReviewChecks.IsTestPath("frontend/src/orders.test.tsx"));
        Assert.True(StackReviewChecks.IsTestPath("backend/app/test_orders.py"));
        Assert.False(StackReviewChecks.IsTestPath("backend/src/Shop.Api/Program.cs"));
        Assert.False(StackReviewChecks.IsTestPath("backend/app/contest.py"));
    }

    // ── checklist ────────────────────────────────────────────────────────

    [Fact]
    public void FR126_Checklist_violations_take_their_items_category_and_severity_without_becoming_deterministic()
    {
        var resolution = Resolve(AspnetSpa());
        var index = resolution.Checklist.ToList().FindIndex(i => i.Pack == "be-aspnet" && i.Severity == Severity.Critical);
        Assert.True(index >= 0);
        var marker = $"[C{index + 1}]";

        var mapped = StackReviewChecklist.Apply([new ReviewFinding(ReviewCategory.Architecture, Severity.Minor, $"{marker} no ownership check", Source: FindingSource.Agent)], resolution);

        Assert.Equal((ReviewCategory.Security, Severity.Critical, FindingSource.Agent), (mapped[0].Category, mapped[0].Severity, mapped[0].Source));
        Assert.Null(StackReviewChecklist.ItemOf("[C999] x", resolution));
        Assert.Null(StackReviewChecklist.ItemOf("no marker", resolution));
        Assert.Contains(resolution.Checklist, i => i.Pack == "data-efcore-postgres" && i.Category == ReviewCategory.DataModel);
    }
}
