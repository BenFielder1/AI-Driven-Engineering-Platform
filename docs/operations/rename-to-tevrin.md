---
title: Rename to Tevrin (0.8.0)
parent: Operations
nav_order: 7
---

# Rename the deployments from aide to Tevrin (0.8.0)

0.8.0 renames the remaining `aide` names to `tevrin` (SPEC FR-327, D-0.8.0-16, D-0.8.0-17). The code and IaC change in the
0.8.0 build PR. The Railway project, its variables, the database and the local Kubernetes namespace are renamed by hand,
with this runbook. **Ben runs it. Agents never do.**

What changes:

| Item | Before | After |
| --- | --- | --- |
| Railway project | `aide` | `tevrin` |
| Connection string variable (`api`) | `ConnectionStrings__Aide` | `ConnectionStrings__Tevrin` (both set during 0.8.x) |
| Postgres database name (only if it is `aide`) | `aide` | `tevrin` |
| Local Kubernetes namespace, images, data PVC | `aide`, `aide-api`/`aide-web`, `aide-data` | `tevrin`, `tevrin-api`/`tevrin-web`, `tevrin-data` |

What doesn't change:

- `DataProtection__ApplicationName` stays `aide`. The key ring is bound to it, and changing it makes stored secrets unreadable.
- `AIDE_RUN_MIGRATIONS` keeps its name.
- The services stay `api`, `web` and `postgres` on Railway, and `api`, `web`, `db` and `db-migrate` locally (D-0.8.0-17).
- The domains stay `www.tevrin.dev` (production) and `aide.benfielder.com` (preview). No DNS changes.

## Order and timing

Do steps 1–3 for an environment **while it still runs 0.7.0**: preview before the 0.8.0 build PR merges, production before
0.8.0 is published. Each step works with both versions:

- 0.7.0 reads only `ConnectionStrings__Aide`, and 0.8.0 reads `ConnectionStrings__Tevrin` (falling back to the old name).
  Both are set from step 2 on.
- `.railway/railway.ts` lists `ConnectionStrings__Aide` in its ignored variables for 0.8.x, so `railway config apply`
  never deletes it.

If the build PR merges first, `deploy.yml`'s `railway config plan --detailed-exit-code` drift check fails the preview
deploy until steps 1–3 are done. That is a blocked deploy (nothing is applied or created), and you can finish the steps
and re-run the workflow.

Step 4 (removing the old variable) waits until 0.8.0 runs in that environment. Step 5 (the database rename) is usually
skipped; see its first paragraph.

## Maintenance window

Allow about 30 minutes per environment. The API restarts when variables change. Tell `preview` users first, then pick a
quiet time for `production`. Take a Postgres backup in Railway (postgres → Backups) before starting each environment.

## 1. Rename the Railway project

1. Railway dashboard → project `aide` → Settings → General → **Project name** → `tevrin` → Save. This renames in place:
   the project ID, environments, services, volumes, domains and tokens don't change.
2. If this machine is linked, you can re-run `railway link` and pick `tevrin`. This is optional, since the link is by ID.

Rollback: rename it back to `aide` in the same place.

## 2. Add the new connection variable (per environment)

`api` → Variables → add `ConnectionStrings__Tevrin` with **exactly** the value of `ConnectionStrings__Aide`:

```
Host=${{postgres.PGHOST}};Port=${{postgres.PGPORT}};Database=${{postgres.PGDATABASE}};Username=${{postgres.PGUSER}};Password=${{postgres.PGPASSWORD}}
```

Leave `ConnectionStrings__Aide` in place. The running 0.7.0 API keeps using it.

Rollback: delete `ConnectionStrings__Tevrin`.

## 3. Plan and apply the IaC (preview, then production)

From the repo root, on the 0.8.0 build branch (or `main` once it's merged):

```bash
railway environment preview          # or: railway link, choose tevrin / preview
railway config plan                  # read-only diff
```

Expected plan: the pre-deploy command switches to `$ConnectionStrings__Tevrin`, plus any project-name change. It must
**not** delete `ConnectionStrings__Aide`, since it is ignored. **Stop if the plan wants to replace or delete `postgres`,
a volume or a service, or to delete `ConnectionStrings__Aide`.** When it shows only the expected changes:

```bash
railway config apply
railway config plan                  # again: no drift
```

The new pre-deploy command also works for a 0.7.0 redeploy, because `ConnectionStrings__Tevrin` exists since step 2.
Then repeat for `production` (`railway environment production`, plan, review, apply).

Rollback: check out the previous commit of `.railway/railway.ts` and `railway config apply` it.

## 4. After 0.8.0 runs in the environment: remove the old variable

When 0.8.0 is deployed there (preview after the build PR's deploy, production after the release deploy):

1. Smoke test (step 6). In the API logs, there is no "old configuration name" warning for the connection string.
2. `api` → Variables → delete `ConnectionStrings__Aide`. The plan stays clean because the name is ignored. 0.9.0 drops it
   from the ignore list.

Rollback: re-add `ConnectionStrings__Aide` with the same value.

## 5. Rename the Postgres database (only if it is named `aide`)

Check the database name first: postgres → Variables → `PGDATABASE`. Railway's template creates `railway`. If it isn't
`aide`, skip this step: the connection string reads `PGDATABASE`, so nothing references the name.

If it is `aide`:

1. Scale `api` to 0 replicas.
2. Enable the Postgres TCP proxy temporarily (postgres → Settings → Networking → TCP Proxy). `railway connect` and
   `DATABASE_PUBLIC_URL` need it.
3. Connect to the **`postgres`** database, not `aide`, because a database can't be renamed while you're connected to it.
   For example, use `railway connect postgres` then `\c postgres`, or `psql` on `DATABASE_PUBLIC_URL` with the database
   part set to `postgres`.
4. Run:

   ```sql
   SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'aide' AND pid <> pg_backend_pid();
   ALTER DATABASE aide RENAME TO tevrin;
   ```

5. postgres → Variables: set `PGDATABASE` to `tevrin`, and the database part of `DATABASE_URL` / `DATABASE_PUBLIC_URL`
   if they spell it out.
6. Disable the TCP proxy again, and check that `DATABASE_PUBLIC_URL` is gone (variables.md keeps it disabled).
7. Scale `api` back up and redeploy it.

Rollback: re-enable the proxy, connect to `postgres`, terminate connections to `tevrin`, run
`ALTER DATABASE tevrin RENAME TO aide;`, restore `PGDATABASE` and the URLs, disable the proxy, and redeploy.

## 6. Verify

```bash
bash deploy/scripts/smoke-remote.sh https://aide.benfielder.com     # preview
bash deploy/scripts/smoke-remote.sh https://www.tevrin.dev          # production
```

Run `railway config plan` once more: it reports no drift, so the next `deploy.yml` run passes its check. Sign in and
open a project to confirm stored credentials still decrypt (Data Protection unchanged).

## 7. Local Docker Desktop

The local manifests now use the `tevrin` namespace, so the next local deploy is a fresh one with an empty database.

1. **Free localhost:8080.** The old `aide` namespace's `web` service holds the port. `deploy-local.sh` stops until it
   is released. Scaling down and deleting the service keeps the old database volume and data:

   ```bash
   kubectl --context docker-desktop -n aide scale deploy --all --replicas=0
   kubectl --context docker-desktop -n aide scale statefulset --all --replicas=0
   kubectl --context docker-desktop -n aide delete service web
   ```

2. **Update `deploy/k8s/overlays/local/secrets.env`.** Rename `ConnectionStrings__Aide` to `ConnectionStrings__Tevrin`
   and set `Database=tevrin;Username=tevrin` in it. The script stops until you do.
3. Deploy: `./deploy/scripts/deploy-local.sh` → <http://localhost:8080>.

To carry the old data across (optional):

1. Scale the old database back up for the dump, and dump it:

   ```bash
   kubectl --context docker-desktop -n aide scale statefulset db --replicas=1
   kubectl --context docker-desktop -n aide exec db-0 -- pg_dump -U aide aide > aide-backup.sql
   kubectl --context docker-desktop -n aide scale statefulset db --replicas=0
   ```

2. Restore it into the new, empty database before using the app. The dump's owner names are `aide`, so restore without
   owners:

   ```bash
   kubectl --context docker-desktop -n tevrin scale deploy api --replicas=0
   kubectl --context docker-desktop -n tevrin exec -i db-0 -- psql -U tevrin -d tevrin -c 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'
   sed -E '/^ALTER .* OWNER TO /d' aide-backup.sql | kubectl --context docker-desktop -n tevrin exec -i db-0 -- psql -U tevrin -d tevrin
   ```

3. **Copy the Data Protection key ring**, or the restored secrets (LLM keys, Git tokens) won't decrypt. It lives in the
   old `aide-data` volume at `/data/keys`. Copy it into `tevrin-data` with a temporary pod that mounts both PVCs, or
   with `kubectl cp` through an api pod in each namespace.
4. Run `./deploy/scripts/deploy-local.sh` again. The dump has the 0.7.0 schema, and dropping the schema undid the first
   migration run, so the `db-migrate` Job must apply the 0.8.0 migrations to the restored data. The api isn't ready until
   it has.

When you no longer need the old data, remove the old namespace. This deletes its database volume:

```bash
kubectl --context docker-desktop delete namespace aide
```

Rollback: the old namespace is untouched until you delete it. Tear down the new one with `teardown-local.sh`, then scale
the old one back up and recreate its `web` service (`kubectl apply -k` on the 0.7.0 manifests).
