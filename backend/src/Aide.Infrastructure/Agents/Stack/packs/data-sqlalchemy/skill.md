<!-- Adapted from stack-packs data-sqlalchemy@1.0.0 skills/data-sqlalchemy/SKILL.md, snapshot 2026-10-25 -->

# SQLAlchemy 2.0 + Alembic (PostgreSQL)

## Detect
Mirror the existing SQLAlchemy style (`Mapped`/`mapped_column`), sync vs async engine, `alembic/env.py`, model locations and session handling.

## Compatible with
`be-fastapi`. Also Supabase host mode, with Alembic owning the migrations.

## Setup
- `sqlalchemy[asyncio]`, `asyncpg`, `alembic` in `pyproject.toml`; current stable versions you are confident exist, Python pinned in `.python-version`.
- `app/models/base.py`: `DeclarativeBase` with a `MetaData(naming_convention=…)` for `ix/uq/ck/fk/pk`; models in `app/models/`, all imported by its `__init__.py`.
- `app/db/session.py`: `create_async_engine(settings.database_url, pool_pre_ping=True)`, `async_sessionmaker(expire_on_commit=False)`, a `get_session()` dependency per request.
- `alembic/env.py` (async): `Base.metadata`, URL from settings, `compare_type=True`, `compare_server_default=True`.

## Modelling
- Typed columns: `Mapped[uuid.UUID]` keys, `String(n)`, `Numeric(p, s)`, `nullable`, `index=True`, `UniqueConstraint` from rules.
- `DateTime(timezone=True)` with UTC from an injected clock. `Enum(..., native_enum=False)` or check-constrained strings.
- Case-insensitive uniqueness: `CITEXT` or `Index(..., func.lower(col), unique=True)`.
- `version_id_col` for optimistic locking; `StaleDataError` → 409.
- `lazy="raise"` with explicit `selectinload`/`joinedload`; `select()` queries, paginated; `text()` only with bound parameters.

## Migrations
- **One revision per step**, hand-written: `alembic/versions/<rev>_<slug>.py`, 12-hex `revision`, `down_revision` = the single current head, `upgrade()`/`downgrade()` with `op.*`.
- Add what autogenerate misses: renames via `op.alter_column(new_column_name=…)`, server defaults, enum changes, `CREATE EXTENSION`, backfills.
- **Expand/contract** for breaking changes. **Never edit a base-commit revision**. No `create_all()` in app code.
- **Migrate command:** `uv run --no-sync alembic upgrade head`, the platform's pre-deploy step; on Vercel the build runs it only when `AIDE_RUN_MIGRATIONS=true`.
- **CI** (Postgres service): `alembic upgrade head`, `alembic check`, exactly one `alembic heads`.

## Local database
`postgres:<major>` pinned to match production, `POSTGRES_*`, `PGDATA` in a volume subdirectory, `pg_isready`, port 5432; dev URL `postgresql+asyncpg://…@localhost:5432/<db>`.

## Testing
Session-scoped `testcontainers[postgres]` fixture, `alembic upgrade head` once, truncate or roll back per test. Never SQLite.

## Production notes
- Settings rewrites `postgres://` (Railway `${{Postgres.DATABASE_URL}}`) to `postgresql+asyncpg://` with `ssl=require`; pool within limits.
- Vercel: `NullPool`, Supabase transaction pooler (`:6543`), `statement_cache_size=0`. Long-lived servers and migrations: session pooler (`:5432`). Never `db.<ref>.supabase.co`.

## Checklist
- [ ] Typed models, naming convention, UTC, version column where edits race.
- [ ] One expand/contract-safe revision per step from a single head; base-commit revisions untouched.
- [ ] No `create_all`; `alembic upgrade head` is the pre-start step; tests use migrated Postgres.
