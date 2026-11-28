<!-- Adapted from stack-packs data-efcore-postgres@1.0.0 skills/data-efcore-postgres/SKILL.md, snapshot 2026-10-25 -->

# EF Core + PostgreSQL

## Detect
Mirror the existing provider packages, DbContext location, `Migrations/`, naming convention and migrate step.

## Compatible with
`be-aspnet`. Also Supabase host mode, where EF owns the migrations.

## Setup
- `dotnet-ef` in `backend/dotnet-tools.json` at the EF Core version; current stable versions you are confident exist, SDK pinned in `global.json`.
- Packages: `Npgsql.EntityFrameworkCore.PostgreSQL` + `EFCore.NamingConventions` (Infrastructure), `Microsoft.EntityFrameworkCore.Design` (Api).
- `src/<App>.Infrastructure/Persistence/` holds the DbContext, `Configurations/`, the seeder and an `IDesignTimeDbContextFactory`; `Migrations/` sits beside it.
- `UseNpgsql(cs).UseSnakeCaseNamingConvention()`, with `cs` from `ConnectionStrings:Default` or `DATABASE_URL` converted in one place (be-aspnet). The design-time factory resolves it the same way.

## Modelling
- One `IEntityTypeConfiguration<T>` per entity: `HasMaxLength` (or `text`), `HasPrecision`, required/optional, indexes on foreign keys and predicates, unique indexes from rules, snake_case `HasFilter`, explicit `OnDelete`.
- Case-insensitive uniqueness: `HasPostgresExtension("citext")` + `HasColumnType("citext")`.
- Concurrency: `uint Version` + `.IsRowVersion()` (`xmin`); `DbUpdateConcurrencyException` → 409 `CONFLICT`.
- `timestamptz` takes UTC only (`TimeProvider.GetUtcNow()`). Enums as strings. `jsonb` only for schemaless data. `Guid.CreateVersion7()` keys.
- `AsNoTracking` + `Select` projections, pagination, no N+1, raw SQL only via `FromSql($"…")`.

## Migrations
- **One migration per step**, hand-written in the `dotnet ef migrations add` shape: `<yyyyMMddHHmmss>_<Name>.cs` (`Up`/`Down`), its `.Designer.cs` (`BuildTargetModel` = the new model), and the updated `<Context>ModelSnapshot.cs`.
- Avoid rewrites and long locks; big indexes via `migrationBuilder.Sql("CREATE INDEX CONCURRENTLY …", suppressTransaction: true)`.
- **Expand/contract** for renames, drops and type changes: rollouts aren't atomic. **Never edit a base-commit migration**; only the snapshot changes.
- **Migrate command:** the image build runs `dotnet ef migrations bundle --self-contained -r <rid> -o /app/efbundle`; the platform runs `/app/efbundle` before the new version starts. No `EnsureCreated()`, `Database.Migrate()` or `MigrateAsync()` in app code.
- Seed: `HasData` for reference data; dev data through an idempotent seeder gated by `Seed:Enabled`.
- **CI:** `dotnet ef migrations has-pending-model-changes`, the bundle build, and integration tests applying every migration.

## Local database
`postgres:<major>` pinned to match production, `POSTGRES_DB/USER/PASSWORD`, `PGDATA` in a volume subdirectory, `pg_isready` health check, port 5432, named volume.

## Testing
`Testcontainers.PostgreSql` (same major) per collection, `MigrateAsync()` once, truncate between tests. Never InMemory or SQLite.

## Production notes
- Convert platform `postgres://` URIs (Render, Railway `${{Postgres.DATABASE_URL}}`) with `SSL Mode=Require`; keep the pool within the connection limit.
- Supabase host mode: the session pooler (`:5432`) for app and migrations, never `db.<ref>.supabase.co`.

## Checklist
- [ ] Snake_case naming, explicit configurations, `citext`/`xmin`/UTC where needed.
- [ ] One expand/contract-safe migration per step with designer and snapshot; base-commit migrations untouched.
- [ ] Bundle is the pre-start migrate step; no app-start migrations.
- [ ] Testcontainers tests apply migrations; seed idempotent and gated.
