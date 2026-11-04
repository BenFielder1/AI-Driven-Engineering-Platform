---
title: PostgreSQL 17 to 18 upgrade
parent: Operations
nav_order: 3
---

# Upgrade local data from PostgreSQL 17 to 18

PostgreSQL 18 can't open a 17 data directory, and the `postgres:18` image keeps its data at a new path (`/var/lib/postgresql/18/docker`). So the upgrade is a dump from 17 and a restore into 18. Neither procedure deletes your 17 data, and you only need one if you have local data worth keeping.

Run these in Git Bash or WSL, not PowerShell, whose `>` and `<` can change the dump's encoding. The commands assume the default user `aide`. The dump holds all your data and the role's password hash, so keep it outside the repo and never commit it.

The restore also brings back the role's old password. That matches `.env` and `secrets.env` as long as you haven't changed them since the 17 database was created.

## Dev loop (compose)

The old data stays in the volume `aide_aide-db-data`. Compose now uses `aide_aide-db-data-18`. Stop `dotnet run` first.

```bash
export MSYS_NO_PATHCONV=1                  # Git Bash only: keeps /var/lib/... paths intact
docker compose stop db

# Start 17 on the old volume, with no network, and dump it.
docker run -d --name aide-db-17 --network none -v aide_aide-db-data:/var/lib/postgresql/data postgres:17
until docker exec aide-db-17 pg_isready -U aide; do sleep 2; done
docker exec aide-db-17 pg_dumpall -U aide --clean --if-exists > ~/aide-17.sql
grep -c 'PostgreSQL database cluster dump complete' ~/aide-17.sql   # must print 1
docker rm -f aide-db-17

# Start 18 on the new volume, ready for the restore.
docker compose up -d --wait db
PSQL="docker compose exec -T db psql -U aide"
```

Now follow [Restore and check](#restore-and-check). Then run `dotnet run` as usual. It keeps working with the same connection string.

## Local Kubernetes

`deploy-local.sh` stops if the database volume (PVC `data-db-0`) has a `pgdata/` directory, which is where the 17 layout keeps its data. This procedure dumps that directory, then renames it to `pgdata-17` on the same volume, so 18 starts cleanly beside it. Skip any `scale` command that reports `NotFound`, which happens after `teardown-local.sh`.

```bash
export MSYS_NO_PATHCONV=1                  # Git Bash only: keeps /var/lib/... paths intact
K="kubectl --context docker-desktop -n aide"

# 1. Stop the app and the database. The volume is kept.
$K scale deploy/api deploy/web --replicas=0
$K scale statefulset/db --replicas=0
$K wait --for=delete pod/db-0 --timeout=120s

# 2. Start 17 on the old data directory and dump it.
$K create -f - <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: db-17-dump
spec:
  restartPolicy: Never
  securityContext: { runAsNonRoot: true, runAsUser: 999, runAsGroup: 999 }
  containers:
    - name: postgres
      image: postgres:17
      env:
        - { name: PGDATA, value: /var/lib/postgresql/data/pgdata }
      volumeMounts:
        - { name: data, mountPath: /var/lib/postgresql/data }
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: data-db-0 }
EOF
$K wait --for=condition=Ready pod/db-17-dump --timeout=180s
until $K exec db-17-dump -- pg_isready -U aide; do sleep 2; done
$K exec db-17-dump -- pg_dumpall -U aide --clean --if-exists > ~/aide-17.sql
grep -c 'PostgreSQL database cluster dump complete' ~/aide-17.sql   # must print 1; stop here if it doesn't
$K delete pod db-17-dump

# 3. Keep the 17 directory, under a new name, so the deploy check passes.
$K create -f - <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: db-17-rename
spec:
  restartPolicy: Never
  securityContext: { runAsNonRoot: true, runAsUser: 999, runAsGroup: 999 }
  containers:
    - name: rename
      image: postgres:18
      command: ["mv", "/vol/pgdata", "/vol/pgdata-17"]
      volumeMounts:
        - { name: data, mountPath: /vol }
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: data-db-0 }
EOF
$K wait --for=jsonpath='{.status.phase}'=Succeeded pod/db-17-rename --timeout=180s
$K delete pod db-17-rename

# 4. Deploy 18, which starts with a new database, then stop the app and wait until nothing is connected.
./deploy/scripts/deploy-local.sh
$K scale deploy/api deploy/web --replicas=0
$K wait --for=delete pod -l 'app.kubernetes.io/name in (api,web)' --timeout=120s   # "no matching resources" means they've already gone
PSQL="$K exec -i db-0 -c postgres -- psql -U aide"
```

Now follow [Restore and check](#restore-and-check). When the checks pass, run `./deploy/scripts/deploy-local.sh` again to bring the app back on the restored data.

To go back, stop the database, rename `pgdata-17` to `pgdata` with the pod from step 3 (swap the two paths), and deploy from a commit before the move to 18.

## Restore and check

Run this in the same shell, with `PSQL` set by the procedure above and the app stopped. The first command removes the database that 18 started with, and stops the restore if anything is still connected, so the dump can't merge into live data.

```bash
# 1. Restore. Skipped if the DROP fails.
$PSQL -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS aide' \
  && $PSQL -d postgres < ~/aide-17.sql > ~/aide-restore.log 2>&1

# 2. Show unexpected errors. Must print nothing.
grep -E 'ERROR:|FATAL:' ~/aide-restore.log | grep -vE 'current user cannot be dropped|role "aide" already exists'

# 3. Compare every table's row count with the dump. Must print nothing.
awk '/^COPY public\./ {t=$2; n=0; f=1; next} f && /^\\\.$/ {print t, n; f=0; next} f {n++}' ~/aide-17.sql |
  while read -r t n; do
    c="$($PSQL -d aide -Atc "SELECT count(*) FROM $t" </dev/null | tr -d '\r')"
    [ "$c" = "$n" ] || echo "MISMATCH $t: dump $n, database $c"
  done

# 4. Must print 18.x.
$PSQL -d aide -Atc 'SHOW server_version' </dev/null
```

| What you see | What it means |
| --- | --- |
| Step 2 hides `current user cannot be dropped` and `role "aide" already exists` | Expected: `aide` is the user running the restore, so its role can't be dropped and already exists. Both are harmless |
| `DROP DATABASE` fails with `is being accessed by other users` | Something is still connected, and nothing was restored. Stop it, then redo from step 1 |
| Step 2 prints any line, or step 3 prints `MISMATCH` | The restore is incomplete. Don't start the app. Fix the cause and redo from step 1 |

Redoing is safe: the dump is unchanged, and step 1 starts again from an empty database. Your 17 data is still there too. `~/aide-restore.log` can contain table and role names, so delete it along with the dump when you've finished.

## After you've checked your data

Your 17 data takes up disk space until you remove it, and nothing removes it for you. Remove it only when you're sure you don't need it: `docker volume rm aide_aide-db-data` for compose, and `rm -rf /vol/pgdata-17` from a pod like the one in step 3 for Kubernetes.
