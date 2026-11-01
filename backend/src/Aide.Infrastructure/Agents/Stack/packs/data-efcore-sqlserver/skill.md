<!-- Adapted from stack-packs data-efcore-sqlserver@1.0.0 skills/data-efcore-sqlserver/SKILL.md, snapshot 2026-10-25 -->

# EF Core + SQL Server

## Detect
Mirror the existing provider packages, DbContext, `Migrations/`, collation, schema (`dbo` or custom) and migrate step.

## Compatible with
`be-aspnet`. No managed SQL Server on the catalog's platforms: production uses an **External** connection string (e.g. Azure SQL), or the project stays local.

## Setup
- `dotnet-ef` in `backend/dotnet-tools.json` at the EF Core version; current stable versions you are confident exist, SDK pinned in `global.json`.
- Packages: `Microsoft.EntityFrameworkCore.SqlServer` (Infrastructure), `Microsoft.EntityFrameworkCore.Design` (Api).
- Layout as data-efcore-postgres: `Persistence/` (DbContext, `Configurations/`, seeder, design-time factory) and `Migrations/` in Infrastructure.
- `UseSqlServer(cs, sql => sql.EnableRetryOnFailure())`, `cs` from `ConnectionStrings:Default` or the ADO.NET string in `DATABASE_URL`.
- Dev: `Server=localhost,1433;…;Encrypt=True;TrustServerCertificate=True` in user-secrets (`TrustServerCertificate` is local only).

## Modelling
- `IEntityTypeConfiguration<T>`: `nvarchar(n)` (`max` only when needed), `HasPrecision`, unique and filtered indexes (`HasFilter("[Status] = 'Active'")`), explicit `OnDelete` (avoid multiple cascade paths with `Restrict`).
- Default `*_CI_AS` collations are case-insensitive; confirm it. Case-sensitive columns: `UseCollation("Latin1_General_100_CS_AS")`.
- Concurrency: `byte[] RowVersion` + `.IsRowVersion()`; → 409 `CONFLICT`.
- `datetimeoffset` or UTC `datetime2`. Enums as strings. Check JSON column support for the server version.
- `AsNoTracking`, projections, pagination, `FromSql($"…")` only.

## Migrations
- **One migration per step**, hand-written in the `dotnet ef migrations add` shape: migration, `.Designer.cs`, updated `<Context>ModelSnapshot.cs`.
- Watch table rebuilds, type changes on large tables, `ONLINE = ON` (edition-dependent) and named default constraints.
- **Expand/contract** for breaking changes. **Never edit a base-commit migration**; only the snapshot changes.
- **Migrate command:** `/app/efbundle`, built in the image (`dotnet ef migrations bundle --self-contained -r <rid>`), run by the platform before start. No `EnsureCreated()`/`Database.Migrate()` in app code.
- Seed: `HasData` for reference data, plus a gated idempotent seeder.
- **CI:** `dotnet ef migrations has-pending-model-changes`, the bundle build, integration tests applying migrations.

## Local database
- `mcr.microsoft.com/mssql/server:<current GA>-latest`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD` (complexity rules), `MSSQL_PID=Developer`, port 1433, volume `/var/opt/mssql`.
- Health: `/opt/mssql-tools18/bin/sqlcmd -C -Q "SELECT 1"` (path varies by image). The first migration creates the database.
- Apple Silicon runs it emulated: slower start, give Docker 2 GB+. Note it in the README.

## Testing
`Testcontainers.MsSql` per collection, `MigrateAsync()` once, clean between tests. Never SQLite or InMemory.

## Production notes
- External string as `DATABASE_URL`, `Encrypt=True` with a trusted certificate; Azure SQL managed identity where supported.
- `EnableRetryOnFailure()`, pool within limits, platform outbound IPs allowed in the firewall. Migrations run on the platform.

## Checklist
- [ ] Retry policy, explicit configurations, `rowversion` where edits race, collation confirmed.
- [ ] One expand/contract-safe migration per step; base-commit migrations untouched.
- [ ] Bundle is the pre-start step; Testcontainers tests pass; Apple Silicon note in the README.
