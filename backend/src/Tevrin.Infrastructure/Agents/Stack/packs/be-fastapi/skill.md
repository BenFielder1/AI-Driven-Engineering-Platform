<!-- Adapted from stack-packs be-fastapi@1.0.0 skills/be-fastapi/SKILL.md, snapshot 2026-10-25 -->

# Python + FastAPI

## Detect

Read `pyproject.toml` (dependencies, tool configs), the package manager (`uv.lock`, `poetry.lock`, `requirements*.txt`), the app structure, sync vs async DB access, auth and tests. Mirror what exists.

## Scaffold

Write a uv-managed app under the backend path:
- **`pyproject.toml`:** `[project]` with `fastapi[standard]`, `pydantic-settings` and the data pack's dependencies; `[dependency-groups] dev` with `pytest`, `pytest-asyncio`, `httpx`, `ruff`, `mypy`, `testcontainers[postgres]`; `[tool.ruff]`, `[tool.mypy]` (`strict = true`) and `[tool.pytest.ini_options]`. Use current stable versions you are confident exist, and pin Python in `.python-version`.
- **No `uv.lock`:** it's created by the first CI run (CI uses `uv sync` without `--frozen` until it exists).
- **Layout:**
  ```
  backend/app/
    main.py           # create_app(), routers, exception handlers; app = create_app()
    api/<feature>.py  # APIRouter(prefix="/api/<feature>")
    domain/           # pure rules
    schemas/          # Pydantic request/response models
    services/         # orchestration
    core/             # settings, errors, security
  backend/scripts/export_openapi.py
  backend/tests/
  backend/openapi/api.json
  ```
- **Serving:** `fastapi dev app/main.py --port <dev port>` locally, and `uvicorn app.main:app --host 0.0.0.0 --port 8080` in the container.

## Conventions

- **Routers** per feature, with `Depends` for the DB session, current user and permissions. Handlers stay thin.
- **Pydantic v2** models for every request and response (`response_model`). Never return ORM objects directly (`model_config = ConfigDict(from_attributes=True)` on read models).
- **Domain rules** are pure Python in `app/domain`, unit-tested. Inject time as a parameter or a clock dependency.
- **Async** end to end if the data layer is async (`data-sqlalchemy` defaults to async). Never block the event loop.
- **Typing:** full type hints, `mypy --strict`, ruff for lint and format.
- **Logging:** `logging` with structured extras, no PII.

## Errors

- An `AppError(status, error_code, title, detail)` exception, with handlers returning `JSONResponse(media_type="application/problem+json")` and `{type, title, status, detail, errorCode}`.
- Override `RequestValidationError` to return 400 (or keep 422 if the spec says so) with `errors: {field: [msgs]}`.
- Codes are constants in `core/errors.py`, mirroring the spec's error table.

## Auth

Follow the spec:
- **Same-origin:** a session cookie (signed, HttpOnly, SameSite=Lax), with password hashing by `argon2-cffi` or `pwdlib`.
- **Cross-origin:** OAuth2 bearer (JWT), validated with `pyjwt`.

Authorisation goes in dependencies (`require_role`) plus ownership checks in services. `CORSMiddleware` only for the cross-origin model, with the origins read from `CORS_ALLOWED_ORIGINS` in Settings.

## Contract export

- **Stable operation IDs:** `FastAPI(generate_unique_id_function=lambda r: r.name)`, so route function names are the operation IDs.
- **Document errors** with `responses={409: {"model": Problem}}` on routes.
- **Export:** `scripts/export_openapi.py` writes `json.dumps(app.openapi(), indent=2, sort_keys=True)` plus a trailing newline to `openapi/api.json`.

You can't run it, so **hand-write the change to `openapi/api.json` in that shape**: keys sorted, two-space indent, the `openapi` version FastAPI emits (keep the existing file's), schemas named after the Pydantic models, and FastAPI's `HTTPValidationError`/`ValidationError` entries where it adds them. CI re-exports and fails on any difference.

## Config and database URL

`pydantic-settings` `Settings` reads env (and `.env` in development only). The data pack expects `DATABASE_URL` (`postgresql+asyncpg://…` for async SQLAlchemy). A platform gives `postgres://` or `postgresql://`, so a Settings validator rewrites the scheme to `postgresql+asyncpg://` and adds SSL for managed hosts.

## Health

`/api/health/live` (no dependencies) and `/api/health/ready` (`SELECT 1` through the session).

## Testing

pytest + `httpx.AsyncClient(transport=ASGITransport(app))`, with a session-scoped testcontainers Postgres fixture. Run Alembic migrations once per session, then truncate between tests. Cover each FR: success, 400/422, 401, 403, 404, each errorCode, 409. Domain rules get plain unit tests named by FR.

## Gate

CI runs, from the backend path (`uv sync` without `--frozen` while there's no `uv.lock`):
```bash
uv sync --frozen && uv run ruff check . && uv run ruff format --check . && uv run mypy app && uv run pytest
uv run python scripts/export_openapi.py && git diff --exit-code openapi/api.json
```

## Containerise

`Dockerfile` in the backend path: `python:<ver>-slim`, uv copied from its image (`COPY --from=ghcr.io/astral-sh/uv:<ver> /uv /bin/uv`), `uv sync --frozen --no-dev` (no `--frozen` without a lockfile), a non-root user (`RUN useradd -r -u 10001 app` then `USER app`), `EXPOSE 8080`, `CMD ["uv","run","--no-sync","uvicorn","app.main:app","--host","0.0.0.0","--port","8080"]`. `.dockerignore` excludes `.venv/`, caches and `.env`.

## Deploy notes

- **Render:** native Python runtime or Docker, health check path `/api/health/ready`.
- **Railway:** Railpack Python or the Dockerfile, health check path `/api/health/ready`.
- **Vercel:** Python functions with zero-config FastAPI (Vercel finds `app` in entrypoints such as `app/main.py`, or `[tool.vercel] entrypoint` in `pyproject.toml`). No long-running background tasks, pooled connections only (Supabase/Neon poolers), limited lifespan shutdown.
- **Not supported** on Netlify, Cloudflare or GitHub Pages.

## Checklist

- [ ] Routers are thin, Pydantic models are on every edge, and domain rules are pure and tested by FR.
- [ ] Problem details carry the spec's errorCodes, and auth dependencies plus ownership checks are in place.
- [ ] Settings are validated and the `DATABASE_URL` scheme is normalised.
- [ ] Operation IDs are stable, the contract is fresh, and ruff, mypy and pytest are green.
