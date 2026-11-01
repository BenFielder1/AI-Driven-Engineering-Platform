<!-- Adapted from stack-packs local-docker-desktop@1.0.0 skills/local-docker-desktop/SKILL.md, snapshot 2026-10-25 -->

# Docker Desktop (Compose)

One `compose.yaml` at the repo root, with two uses:
- **default (`db`):** only the database (and other dependencies) for the dev loop; backend and frontend run on the host.
- **`app` profile:** the whole stack in containers, production-like, at `http://localhost:8080`.

## Prerequisites
The user runs Docker Desktop with Compose v2. Write files only; don't assume they were run. On Apple Silicon, follow the data pack's image notes (e.g. SQL Server needs emulation: `platform: linux/amd64`).

## Files
```yaml
# compose.yaml
name: <app>
services:
  db:
    image: <from the data pack, e.g. postgres:17>
    env_file: .env
    environment:
      PGDATA: /var/lib/postgresql/data/pgdata              # postgres; see the data pack for others
    ports: ["127.0.0.1:5432:5432"]
    volumes: [db-data:/var/lib/postgresql/data]
    healthcheck: { test: ["CMD-SHELL", "pg_isready -U $${POSTGRES_USER} -d $${POSTGRES_DB}"], interval: 5s, retries: 20 }

  migrate:
    profiles: [app]
    build: { context: ./backend }
    command: <the data pack's one-off migrate command>
    env_file: .env
    environment:
      DATABASE_URL: postgres://${POSTGRES_USER}:${POSTGRES_PASSWORD}@db:5432/${POSTGRES_DB}   # or the backend's own key and format
    depends_on: { db: { condition: service_healthy } }
    restart: "no"

  api:
    profiles: [app]
    build: { context: ./backend }
    env_file: .env
    environment:
      DATABASE_URL: <as in migrate>
      PORT: "8080"                                         # ASPNETCORE_HTTP_PORTS / SERVER_PORT per the backend pack
    depends_on: { migrate: { condition: service_completed_successfully } }
    healthcheck: { test: ["CMD-SHELL", "wget -qO- http://localhost:8080<ready path> || exit 1"], interval: 10s, retries: 12 }

  web:
    profiles: [app]
    build: { context: ./frontend }                         # nginx serving the SPA + proxy /api → api:8080
    environment:
      API_ORIGIN: http://api:8080
    ports: ["127.0.0.1:8080:8080"]
    depends_on: { api: { condition: service_healthy } }

volumes:
  db-data:
```
- **Compose interpolation:** `${VAR}` is filled by Compose from the shell and the root `.env` (so URLs can be built from `POSTGRES_*` without literal passwords); `$${VAR}` is passed through literally, which only a shell inside the container expands (the `pg_isready` health check). Never write a literal password.
- **Adapt to the composition:**
  - no backend: drop `migrate` and `api`. Supabase BaaS uses the Supabase local stack instead of `db` (note it in `.env.example`).
  - Next.js full-stack: `web` is the Node server (`PORT: "8080"`), and `migrate` uses the frontend image.
  - no database: drop `db` and `migrate`.
  - SQL Server: the data pack's image, `MSSQL_SA_PASSWORD` from `.env`, its own health check.
- **Images** come from the layer packs' Containerise recipes; the web container proxies `/api` (same-origin locally).
- **Health checks:** if the runtime image has neither `wget` nor `curl` (aspnet chiseled, distroless), drop the API health check and use `condition: service_started` for `web`.
- **`.env`** (git-ignored) holds DB credentials and app secrets. **`.env.example`** is committed with every key and placeholder values (e.g. `POSTGRES_PASSWORD=change-me`). The dev-loop URL points at `localhost:5432`; inside Compose it points at `db:5432`.
- Make sure `.gitignore` covers `.env` (the scaffolder's file; ask through your step summary if missing).

## Commands
For the README and your step summary only (you don't run them):
```bash
docker compose up -d db                       # dev loop DB
docker compose --profile app up -d --build    # full stack → http://localhost:8080
docker compose --profile app logs -f api
docker compose --profile app down             # stop (keeps the volume)
```

## Safety
- Never suggest `docker compose down -v`, `docker volume rm` or `docker system prune` without a warning that they wipe local data.
- Publish ports on `127.0.0.1` only.
- No secrets in `compose.yaml`: values come from `.env`.

## Troubleshooting
| Symptom | Check |
| --- | --- |
| `migrate` exits non-zero | DB URL host must be `db`; credentials; the migrate command exists in the image |
| API unhealthy | Ready path, port 8080 binding, missing env, no `wget` in the image |
| Port already allocated | Another process on 5432 or 8080: change the host port |
| DB auth fails after changing `.env` | Postgres reads the password only on first init: change it in the DB, or wipe the volume (with consent) |

## Checklist
- [ ] `db` has a health check and a named volume; the `app` profile runs migrate → api → web in order.
- [ ] Images follow the layer packs' recipes, and `web` proxies `/api` to `api:8080`.
- [ ] Ports bound to `127.0.0.1`; no secrets in `compose.yaml`; `.env.example` complete with placeholders.
