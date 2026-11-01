#!/usr/bin/env bash
# Shared helpers for deploy scripts. Sourced, not executed.
# Every kubectl call goes through $K, pinned to the docker-desktop context; the current context is never used.

CTX=docker-desktop
NS=aide
APP_PORT="${APP_PORT:-8080}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OVERLAY_DIR="deploy/k8s/overlays/local"
GENERATED_DIR="deploy/k8s/.generated"
SECRETS_FILE="$OVERLAY_DIR/secrets.env"
SECRETS_EXAMPLE="$OVERLAY_DIR/secrets.env.example"

# Git Bash on Windows: stop MSYS from rewriting arguments that look like POSIX paths (e.g. /app/efbundle).
export MSYS_NO_PATHCONV=1

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

require_context() {
  command -v kubectl >/dev/null 2>&1 || die "kubectl not found. Enable Kubernetes in Docker Desktop (it installs kubectl)."
  kubectl config get-contexts "$CTX" >/dev/null 2>&1 \
    || die "No '$CTX' kubectl context. Enable Kubernetes in Docker Desktop (Settings > Kubernetes)."
  K="kubectl --context $CTX"
  $K get nodes >/dev/null 2>&1 || die "Context '$CTX' exists but the cluster is not reachable. Is Docker Desktop Kubernetes running?"
}

# FR-164: stop before anything is applied if the database volume still holds a postgres:17 data directory.
# postgres:18 would not read it: with the new mount and PGDATA it would start an empty database next to it.
# Always reads the volume (not a running pod), so a stopped or crash-looping database is covered too. The check is a
# short-lived pod that mounts PVC data-db-0 read-only and is deleted afterwards. If the answer isn't clear, it stops.
#   <volume>/pgdata/       the 17 layout (mount /var/lib/postgresql/data, PGDATA .../data/pgdata)
#   <volume>/18/docker/    the 18 layout (mount /var/lib/postgresql, PGDATA .../18/docker)
DB_UPGRADE_DOC="README.md, section 'Upgrade local data from PostgreSQL 17 to 18'"
DB_CHECK_POD=db-version-check

delete_db_check_pod() {
  $K -n "$NS" delete pod "$DB_CHECK_POD" --ignore-not-found --timeout=30s >/dev/null 2>&1 || true
}

check_db_data_version() {
  local pvc_phase pod_phase deadline result legacy current
  pvc_phase="$($K -n "$NS" get pvc data-db-0 --ignore-not-found -o 'jsonpath={.status.phase}')" \
    || die "Could not read PVC $NS/data-db-0. Nothing was changed."
  case "$pvc_phase" in
    ""|Pending) info "no database volume with data yet (fresh cluster)"; return 0 ;;
    Bound) ;;
    *) die "PVC $NS/data-db-0 is in phase '$pvc_phase', so its database version can't be read. Nothing was changed." ;;
  esac

  delete_db_check_pod
  trap delete_db_check_pod EXIT
  $K -n "$NS" create -f - >/dev/null <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: $DB_CHECK_POD
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext:
    runAsNonRoot: true
    runAsUser: 999
    runAsGroup: 999
    seccompProfile:
      type: RuntimeDefault
  containers:
    - name: check
      image: postgres:18
      imagePullPolicy: IfNotPresent
      command:
        - sh
        - -c
        - |
          legacy=none
          if [ -e /vol/pgdata ]; then
            if [ -r /vol/pgdata/PG_VERSION ]; then legacy="\$(tr -d '[:space:]' < /vol/pgdata/PG_VERSION)"
            elif [ -r /vol/pgdata ] && [ -x /vol/pgdata ] && [ -z "\$(ls -A /vol/pgdata)" ]; then legacy=none
            else legacy=unknown; fi
          fi
          current=none
          if [ -e /vol/18/docker ]; then
            if [ -r /vol/18/docker/PG_VERSION ]; then current="\$(tr -d '[:space:]' < /vol/18/docker/PG_VERSION)"; else current=unknown; fi
          fi
          echo "aide-db-check legacy=\${legacy:-unknown} current=\${current:-unknown}"
      securityContext:
        allowPrivilegeEscalation: false
        readOnlyRootFilesystem: true
        capabilities: { drop: ["ALL"] }
      resources:
        requests: { cpu: 10m, memory: 16Mi }
        limits: { memory: 64Mi }
      volumeMounts:
        - name: data
          mountPath: /vol
          readOnly: true
  volumes:
    - name: data
      persistentVolumeClaim:
        claimName: data-db-0
        readOnly: true
EOF

  deadline=$(( $(date +%s) + ${DEPLOY_TIMEOUT:-180} ))
  while :; do
    pod_phase="$($K -n "$NS" get pod "$DB_CHECK_POD" -o 'jsonpath={.status.phase}' 2>/dev/null || true)"
    [[ "$pod_phase" == Succeeded ]] && break
    if [[ "$pod_phase" == Failed || $(date +%s) -ge $deadline ]]; then
      $K -n "$NS" describe pod "$DB_CHECK_POD" >&2 || true
      die "Could not read the database version from PVC $NS/data-db-0 (check pod: '$pod_phase'). Nothing was applied."
    fi
    sleep 2
  done

  result="$($K -n "$NS" logs "$DB_CHECK_POD" | tr -d '\r')" \
    || die "Could not read the check pod's result. Nothing was applied."
  delete_db_check_pod
  trap - EXIT
  [[ "$result" =~ ^aide-db-check\ legacy=([^\ ]+)\ current=([^\ ]+)$ ]] \
    || die "Unexpected result from the database version check: '$result'. Nothing was applied."
  legacy="${BASH_REMATCH[1]}"
  current="${BASH_REMATCH[2]}"

  if [[ "$legacy" != none ]]; then
    die "PVC $NS/data-db-0 holds a pre-18 PostgreSQL data directory in pgdata/ (PG_VERSION: $legacy). PostgreSQL 18 can't open it.
       Nothing was applied and the data is unchanged. Follow $DB_UPGRADE_DOC, then re-run this script."
  fi
  case "$current" in
    none) info "database volume has no data yet" ;;
    18) info "database volume holds PostgreSQL 18 data" ;;
    *) die "PVC $NS/data-db-0 has a data directory in 18/docker/ whose version isn't 18 (PG_VERSION: $current), or can't be read.
       Nothing was applied and the data is unchanged. Inspect the volume before re-running this script. For moving data between versions, see $DB_UPGRADE_DOC." ;;
  esac
}

cd "$REPO_ROOT"
