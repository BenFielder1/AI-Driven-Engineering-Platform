---
title: Local Kubernetes
parent: Operations
nav_order: 1
---

# Local Kubernetes (Docker Desktop)

Run the whole app, built from your checkout, on the Docker Desktop Kubernetes cluster. Enable Kubernetes first (see [Prerequisites](../getting-started/prerequisites.md)). Scripts only ever use the `docker-desktop` context.

```bash
./deploy/scripts/deploy-local.sh                          # build images, apply, migrate, roll out, smoke test → http://localhost:8080
./deploy/scripts/teardown-local.sh                        # remove app (keeps DB data unless --wipe-data)
```

The script builds the `aide-api` and `aide-web` images, tags them with the git SHA, applies the manifests in two passes, runs the `db-migrate` Job, rolls out `api` and `web`, then runs `smoke.sh`. It is safe to re-run and never deletes data. Run it from the repo root in Git Bash or WSL.

- **App:** http://localhost:8080 (namespace `aide`).
- **web:** the Next.js standalone server (`frontend/server.ts` in the `aide-web` image) on the `web` LoadBalancer Service, port 8080. It serves the pages, sets the security headers and CSP nonce, and proxies `/api` (including the SignalR WebSockets) to `BACKEND_URL=http://api:8080` from the `app-config` ConfigMap. It runs as uid 1000 (`node`) with a read-only root filesystem; only `/tmp` (an emptyDir holding Next's cache) is writable. Probes use `/healthz`, which never calls the API. `TRUST_EDGE_FORWARDED` is not set, because there is no edge proxy in front of it. The API is ClusterIP only.
- **secrets.env:** if `deploy/k8s/overlays/local/secrets.env` is missing, the script copies `secrets.env.example` and generates the database password, the matching connection string and `Approvals__SigningKey`. It is git-ignored. Edit it to add LLM keys, GitHub App or OAuth values, SMTP; then re-run the script. The script refuses to generate a new password while the database volume exists.
- **Seed admin:** `admin@aide.local`. The password is logged once by the pod that creates it (a Warning line, `Seeded admin admin@aide.local with password ...`):
  `kubectl --context docker-desktop -n aide logs deploy/api | grep -i 'Seeded admin'`
  After a redeploy the line is gone. Note it on the first deploy.
- **Logs:** `kubectl --context docker-desktop -n aide logs deploy/api` (also `deploy/web`, `statefulset/db`, `job/db-migrate`). Logs are JSON.
- **Smoke test only:** `./deploy/scripts/smoke.sh` (`deploy-local.sh` runs it at the end; set `BASE_URL` to test another host). It checks:
  - `/healthz` returns `ok`
  - `/` returns 307 (to `/projects`) or 200
  - `/login` returns 200 with a `Content-Security-Policy` that has a `'nonce-…'` source, `X-Frame-Options: DENY` and `X-Content-Type-Options: nosniff`
  - `/api/health/ready` returns 200 through the proxy. Readiness also fails while migrations are pending or if an embedded preset does not load.
- **E2E mode:** `./deploy/scripts/deploy-local.sh --e2e` is the only switch. It applies `deploy/k8s/overlays/local-e2e`: `Deployments__Provider=Fake`, `Databases__Provider=Fake`, `Deployments__AllowFake=true`, `Git__Local__ImportRoot=/data/import` and `Llm__DefaultProvider=fake`. Fake databases mean no real provisioning or migration runner. It also seeds the Local import repo `/data/import/existing-app` (tags `v0.1.0`, `v0.2.0`) with `seed-e2e-repo.sh`. The API logs a warning on every boot while Fake deployments are on. Use it only for Playwright runs.
- **Switch back:** run `./deploy/scripts/deploy-local.sh` again without `--e2e`. The API pod restarts with Vercel deployments and no Local import. The seeded repo stays on the volume, unused.
- **Teardown:** `./deploy/scripts/teardown-local.sh` (no flags) removes workloads, services, config and secrets. It keeps the namespace and the volumes, so data survives.
- **`--wipe-data`:** `./deploy/scripts/teardown-local.sh --wipe-data` also deletes the whole `aide` namespace: the database and the `aide-data` volume (working copies, encryption keys). You must type `aide` to confirm. Saved project secrets cannot be decrypted afterwards.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `No 'docker-desktop' kubectl context` | Enable Kubernetes in Docker Desktop settings and wait until it is running |
| `holds a pre-18 PostgreSQL data directory` | The database volume still has PostgreSQL 17 data. Nothing was changed. Follow [Upgrade local data from PostgreSQL 17 to 18](postgres-18-upgrade.md) |
| `secrets.env is missing but the database volume already exists` | Restore your old `secrets.env`, or run `teardown-local.sh --wipe-data` (deletes all data) |
| `secrets.env has Windows (CRLF) line endings` | Convert the file to LF (`sed -i 's/\r$//' deploy/k8s/overlays/local/secrets.env`) and re-run |
| `still contains the 'change-me' POSTGRES_PASSWORD` | Set a real password that matches the one in `ConnectionStrings__Aide` |
| Migration Job logs `Cannot load library libgssapi_krb5.so.2` | Harmless Npgsql Kerberos probe. Ignore it |
| Seed admin password not in the logs | It is logged only once, by the pod that created the account. Register a new user and promote it (Admin page), or wipe data |
| `429 RATE_LIMITED` on register or login | Limit is 5 per minute per IP. Wait for the `Retry-After` time. Dev loop: set `Auth__RateLimits__RegisterPerMinute` |
| API ignores `Llm__Anthropic__ApiKey` | Old key name. Use `Llm__Providers__Anthropic__ApiKey` (same for OpenAI) |
| Blank `Smtp__Port=` breaks startup | Blank non-string values fail binding. The script comments them out. Leave them unset |
| API refuses to start with the Fake deployment provider | Outside Development it also needs `Deployments__AllowFake=true`. Use `deploy-local.sh --e2e`, which sets both |
| Import E2E cannot find the Local repo | `Git__Local__ImportRoot` is unset. Deploy with `--e2e`, which also seeds `/data/import/existing-app` |
| API pod not ready | Check `kubectl ... logs deploy/api`, `get events`. A failed migration leaves the old pods running |
| `/api/...` returns 502 `UPSTREAM_ERROR` | The web server can't reach the API. Check the `api` pod, and that `BACKEND_URL` in `app-config` is `http://api:8080` |
| web pod crash-loops with `Invalid web server environment` | `BACKEND_URL` (or `PORT`) is not a valid URL or number. Fix it in `deploy/k8s/base/configmap.yaml` and redeploy |
