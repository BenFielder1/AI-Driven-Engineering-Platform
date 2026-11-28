#!/usr/bin/env bash
# Build the images, apply the local overlay, run migrations, roll out and smoke-test.
# Target: Docker Desktop Kubernetes (context docker-desktop) only. App: http://localhost:8080
# Idempotent: safe to re-run. Never deletes data (PVCs, namespace).
# Stops before changing anything if the database volume still holds PostgreSQL 17 data (FR-164; see README.md).
#
# Usage: deploy-local.sh [--e2e]
#   (default)  overlays/local: Vercel deployments, no Local import (normal use).
#   --e2e      overlays/local-e2e (the only switch): Deployments__Provider=Fake + Databases__Provider=Fake + Deployments__AllowFake=true,
#              Trackers__Provider=Fake (SPEC 0.5.0 §9), Git__Local__ImportRoot=/data/import, Llm__DefaultProvider=fake, and seeds the Local import repo
#              /data/import/existing-app (tags v0.1.0, v0.2.0). For the Playwright suites (SPEC §8). Re-run without
#              --e2e to switch back; the seeded repo stays on the data volume (harmless, import is off).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

TIMEOUT="${DEPLOY_TIMEOUT:-180}"
E2E=0
# --e2e is the only switch: a stray TEVRIN_E2E (or the pre-0.8.0 name AIDE_E2E) in the environment must never turn a
# plain deploy into an E2E one.
[[ -n "${TEVRIN_E2E:-}${AIDE_E2E:-}" ]] && TEVRIN_E2E_SET=1 || TEVRIN_E2E_SET=0
for arg in "$@"; do
  case "$arg" in
    --e2e) E2E=1 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg (see --help)" ;;
  esac
done
if [[ $TEVRIN_E2E_SET -eq 1 && $E2E -eq 0 ]]; then
  die "TEVRIN_E2E (or AIDE_E2E) is set but is not a switch. Pass --e2e for an E2E deploy, or unset it for a normal deploy."
fi
if [[ $E2E -eq 1 ]]; then MODE=e2e; OVERLAY_REF="../overlays/local-e2e"; else MODE=normal; OVERLAY_REF="../overlays/local"; fi

# ---------------------------------------------------------------------------
step "Checking Docker and the '$CTX' context"
docker info >/dev/null 2>&1 || die "Docker is not running. Start Docker Desktop."
require_context
RUNTIME="$($K get nodes -o jsonpath='{.items[0].status.nodeInfo.containerRuntimeVersion}')"
info "node runtime: $RUNTIME"
# FR-327: before 0.8.0 the app ran in namespace 'aide'. It is never touched or deleted here; its data stays put.
if $K -n aide get service web >/dev/null 2>&1; then
  # Its web LoadBalancer holds localhost:8080, so the new one would stay pending and the smoke test would reach 0.7.0.
  die "The pre-0.8.0 namespace 'aide' still serves web on localhost:8080, so '$NS' can't take the port.
       Stop it and free the port (its database volume and data are kept):
         kubectl --context $CTX -n aide scale deploy --all --replicas=0
         kubectl --context $CTX -n aide scale statefulset --all --replicas=0
         kubectl --context $CTX -n aide delete service web
       Then run this script again. See docs/operations/rename-to-tevrin.md (Local Docker Desktop)."
fi
if $K get namespace aide >/dev/null 2>&1; then
  warn "The pre-0.8.0 namespace 'aide' still exists. This deploy uses '$NS' (a new, empty database) and leaves 'aide' alone.
       Once you've checked you no longer need its data, remove it with: kubectl --context $CTX delete namespace aide"
fi

# ---------------------------------------------------------------------------
step "Checking the database volume's PostgreSQL version"
check_db_data_version

# ---------------------------------------------------------------------------
step "Checking $SECRETS_FILE"
rand_hex() { # $1 = bytes
  if command -v openssl >/dev/null 2>&1; then openssl rand -hex "$1"
  else head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'; fi
}
if [[ ! -f "$SECRETS_FILE" ]]; then
  [[ -f "$SECRETS_EXAMPLE" ]] || die "Missing $SECRETS_EXAMPLE"
  # PostgreSQL reads POSTGRES_PASSWORD only when it first initialises its volume. A new random password
  # would not match an existing database, so never generate one while the DB volume exists.
  if $K -n "$NS" get pvc data-db-0 >/dev/null 2>&1; then
    die "$SECRETS_FILE is missing but the database volume (PVC $NS/data-db-0) already exists and uses the old password.
       Restore your previous secrets.env, or run ./deploy/scripts/teardown-local.sh --wipe-data (deletes ALL data) first."
  fi
  db_password="$(rand_hex 24)"
  signing_key="$(rand_hex 32)"
  # Hex values are safe in sed replacements and connection strings.
  sed -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${db_password}|" \
      -e "s|^ConnectionStrings__Tevrin=.*|ConnectionStrings__Tevrin=Host=db;Port=5432;Database=tevrin;Username=tevrin;Password=${db_password}|" \
      -e "s|^Approvals__SigningKey=.*|Approvals__SigningKey=${signing_key}|" \
      "$SECRETS_EXAMPLE" > "$SECRETS_FILE"
  unset db_password signing_key
  info "Created $SECRETS_FILE (git-ignored) from the example."
  info "Generated: POSTGRES_PASSWORD, ConnectionStrings__Tevrin (matching password), Approvals__SigningKey."
  info "Optional keys (LLM, GitHub OAuth, SMTP) are blank or commented out; edit the file and re-run to set them."
else
  info "Using existing $SECRETS_FILE"
fi
if grep -q $'\r' "$SECRETS_FILE"; then
  die "$SECRETS_FILE has Windows (CRLF) line endings; values would contain a stray CR. Convert it to LF and re-run."
fi
# FR-327: a pre-0.8.0 secrets.env names the key ConnectionStrings__Aide and points at database/user 'aide'. The
# '$NS' namespace starts a new database 'tevrin' (user 'tevrin'), so stop with the fix rather than rewrite values.
if grep -qE '^ConnectionStrings__Aide=' "$SECRETS_FILE" && ! grep -qE '^ConnectionStrings__Tevrin=.+' "$SECRETS_FILE"; then
  die "$SECRETS_FILE uses the pre-0.8.0 key ConnectionStrings__Aide. Rename it to ConnectionStrings__Tevrin and set
       Database=tevrin;Username=tevrin in it (keep Password equal to POSTGRES_PASSWORD), then re-run."
fi
for key in POSTGRES_PASSWORD ConnectionStrings__Tevrin; do
  grep -qE "^${key}=.+" "$SECRETS_FILE" || die "$SECRETS_FILE: $key is missing or empty."
done
grep -qE '^POSTGRES_PASSWORD=change-me$' "$SECRETS_FILE" \
  && die "$SECRETS_FILE still contains the 'change-me' POSTGRES_PASSWORD placeholder. Set a real value (it must match the password in ConnectionStrings__Tevrin and, if the DB volume exists, the database's current password)."

# FR-48: Approvals__SigningKey must be >= 32 characters (after trimming). Replace a missing, 'change-me' or too-short
# key with a random one; every other line of the file is kept byte-for-byte. Values are never printed.
signing_key_value="$(sed -n 's/^Approvals__SigningKey=//p' "$SECRETS_FILE" | tail -n 1 | tr -d '[:space:]')"
if [[ "$signing_key_value" == "change-me" || ${#signing_key_value} -lt 32 ]]; then
  new_key="$(rand_hex 32)"
  tmp="$(mktemp "$SECRETS_FILE.XXXXXX")"
  if grep -q '^Approvals__SigningKey=' "$SECRETS_FILE"; then
    sed -e "s|^Approvals__SigningKey=.*|Approvals__SigningKey=${new_key}|" "$SECRETS_FILE" > "$tmp"
  else
    { cat "$SECRETS_FILE"; [[ -z "$(tail -c 1 "$SECRETS_FILE")" ]] || echo; echo "Approvals__SigningKey=${new_key}"; } > "$tmp"
  fi
  mv -f "$tmp" "$SECRETS_FILE"
  unset new_key tmp
  info "Generated a new Approvals__SigningKey (was missing, a placeholder or shorter than 32 characters)."
fi
unset signing_key_value

# Non-string options with a blank value (KEY=) bind an empty string and fail config binding/validation at startup.
# Comment such lines out so the backend default applies (only blank lines are changed; set values are kept).
BLANK_UNSAFE_KEYS='Smtp__Port|Smtp__EnableSsl|Smtp__Timeout|Smtp__From|App__PublicBaseUrl'
if grep -qE "^(${BLANK_UNSAFE_KEYS})=[[:space:]]*$" "$SECRETS_FILE"; then
  tmp="$(mktemp "$SECRETS_FILE.XXXXXX")"
  sed -E "s/^((${BLANK_UNSAFE_KEYS})=[[:space:]]*)$/# \1  (blank: commented out by deploy-local.sh, default applies)/" "$SECRETS_FILE" > "$tmp"
  mv -f "$tmp" "$SECRETS_FILE"
  unset tmp
  warn "Commented out blank non-string keys in $SECRETS_FILE (backend defaults apply). Set a value to override."
fi
# Pre-phase-3 key names bind to nothing (the backend reads Llm:Providers:<Name>:ApiKey). Warn, never rewrite values.
if grep -qE '^Llm__(Anthropic|OpenAI)__ApiKey=' "$SECRETS_FILE"; then
  warn "$SECRETS_FILE uses old LLM key names (Llm__Anthropic__ApiKey / Llm__OpenAI__ApiKey), which the API ignores.
       Rename them to Llm__Providers__Anthropic__ApiKey / Llm__Providers__OpenAI__ApiKey (see secrets.env.example)."
fi
git check-ignore -q "$SECRETS_FILE" || die "$SECRETS_FILE is not git-ignored. Refusing to continue."

# ---------------------------------------------------------------------------
step "Building images"
SHA="$(git rev-parse --short HEAD 2>/dev/null || echo nogit)"
if [[ -n "$(git status --porcelain 2>/dev/null)" ]]; then TAG="${SHA}-dirty"; else TAG="$SHA"; fi
API_IMAGE="tevrin-api:$TAG"
WEB_IMAGE="tevrin-web:$TAG"
info "tag: $TAG"
docker build -f backend/Dockerfile -t "$API_IMAGE" backend
docker build -f frontend/Dockerfile -t "$WEB_IMAGE" frontend

# Docker Desktop's default (kubeadm) cluster uses Docker's image store (runtime docker://): nothing to do.
# The kind provisioner runs containerd nodes as containers: load the images into each node.
if [[ "$RUNTIME" != docker://* ]]; then
  step "Loading images into kind nodes (runtime $RUNTIME)"
  mapfile -t NODES < <(docker ps --format '{{.Names}}' | grep -E '^desktop-(control-plane|worker)' || true)
  [[ ${#NODES[@]} -gt 0 ]] || die "Cluster does not share Docker's image store and no kind node containers were found. See README (Local Kubernetes)."
  for node in "${NODES[@]}"; do
    for img in "$API_IMAGE" "$WEB_IMAGE"; do
      info "$img -> $node"
      docker save "$img" | docker exec -i "$node" ctr -n k8s.io images import --all-platforms - >/dev/null
    done
  done
fi

# ---------------------------------------------------------------------------
step "Writing image tags to $GENERATED_DIR (mode: $MODE)"
mkdir -p "$GENERATED_DIR"
cat > "$GENERATED_DIR/kustomization.yaml" <<EOF
# GENERATED by deploy/scripts/deploy-local.sh — do not edit, git-ignored. Mode: $MODE
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources:
  - $OVERLAY_REF
images:
  - name: tevrin-api
    newTag: "$TAG"
  - name: tevrin-web
    newTag: "$TAG"
EOF

# Images currently deployed, to decide whether a restart is needed (same tag = rebuilt image, no spec change).
PREV_API_IMAGE="$($K -n "$NS" get deploy api -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || true)"
PREV_WEB_IMAGE="$($K -n "$NS" get deploy web -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || true)"

# ---------------------------------------------------------------------------
# Two passes, split by the label tevrin.deploy/stage=app (api + web Deployments/Services):
#   1. everything else (namespace, config, secret, PVCs, db, db-migrate Job) -> wait for migrations
#   2. api + web, only after the Job succeeded. A failed migration leaves the running api/web untouched.
RENDERED="$GENERATED_DIR/rendered.yaml"
kubectl kustomize "$GENERATED_DIR" > "$RENDERED"
[[ -s "$RENDERED" ]] || die "Rendered manifests are empty."

step "Applying infrastructure + migration Job (pass 1)"
# Jobs are immutable; remove the previous migration Job so this deploy's runs.
$K -n "$NS" delete job db-migrate --ignore-not-found --wait=true >/dev/null 2>&1 || true
$K apply -f "$RENDERED" -l 'tevrin.deploy/stage!=app'

step "Waiting for the database"
$K -n "$NS" rollout status statefulset/db --timeout="${TIMEOUT}s"

# ---------------------------------------------------------------------------
step "Running migrations (job/db-migrate)"
deadline=$(( $(date +%s) + TIMEOUT ))
while :; do
  succeeded="$($K -n "$NS" get job db-migrate -o jsonpath='{.status.succeeded}' 2>/dev/null || true)"
  failed_cond="$($K -n "$NS" get job db-migrate -o jsonpath='{.status.conditions[?(@.type=="Failed")].status}' 2>/dev/null || true)"
  if [[ "${succeeded:-0}" -ge 1 ]]; then info "migrations applied"; break; fi
  if [[ "$failed_cond" == "True" || $(date +%s) -ge $deadline ]]; then
    echo "--- job/db-migrate logs ---" >&2
    $K -n "$NS" logs job/db-migrate --all-containers --tail=200 >&2 || true
    echo "--- describe job/db-migrate ---" >&2
    $K -n "$NS" describe job db-migrate >&2 || true
    die "Migration Job failed or timed out. api/web were NOT updated (still running the previous version)."
  fi
  sleep 3
done
$K -n "$NS" logs job/db-migrate -c migrate --tail=20 || true

# ---------------------------------------------------------------------------
step "Applying api + web (pass 2) and rolling out"
$K apply -f "$RENDERED" -l 'tevrin.deploy/stage=app'
[[ "$PREV_API_IMAGE" == "$API_IMAGE" ]] && { info "api tag unchanged; restarting to pick up the rebuilt image"; $K -n "$NS" rollout restart deploy/api; }
[[ "$PREV_WEB_IMAGE" == "$WEB_IMAGE" ]] && { info "web tag unchanged; restarting to pick up the rebuilt image"; $K -n "$NS" rollout restart deploy/web; }
for d in api web; do
  if ! $K -n "$NS" rollout status "deploy/$d" --timeout="${TIMEOUT}s"; then
    echo "--- deploy/$d logs ---" >&2
    $K -n "$NS" logs "deploy/$d" --tail=100 >&2 || true
    echo "--- pods ---" >&2
    $K -n "$NS" get pods -o wide >&2 || true
    echo "--- recent events ---" >&2
    $K -n "$NS" get events --sort-by=.lastTimestamp | tail -n 20 >&2 || true
    die "Rollout of $d failed."
  fi
done

$K -n "$NS" get pods,svc

# ---------------------------------------------------------------------------
step "Smoke test"
"$REPO_ROOT/deploy/scripts/smoke.sh"

if [[ $E2E -eq 1 ]]; then
  "$REPO_ROOT/deploy/scripts/seed-e2e-repo.sh"
  step "Checking E2E config on the api"
  # grep reads all input (no -q early exit), so pipefail never sees a SIGPIPE'd kubectl.
  fake_warnings="$($K -n "$NS" logs deploy/api --tail=500 2>/dev/null | grep -c 'Fake deployment provider enabled' || true)"
  if [[ "${fake_warnings:-0}" -ge 1 ]]; then
    info "api logged the Fake deployment provider warning (Deployments:AllowFake)"
  else
    warn "api did not log the Fake deployment provider warning; check Deployments__Provider/AllowFake in app-config."
  fi
fi

step "Deployed $TAG ($MODE mode) -> http://localhost:$APP_PORT"
if [[ $E2E -eq 1 ]]; then
  info "E2E mode: Fake deployments + Fake trackers + Fake LLM default, Local import root /data/import (existing-app). Run without --e2e to switch back."
fi
# DEP-2: the overlay sets Seed__Enabled=true. The api logs the generated admin password ONCE (Warning) when it
# first creates admin@aide.local; later restarts don't log it again (the account is never reset).
info "Seed admin: admin@aide.local (demo project: 'Demo: Todo API'). Its generated password is logged once, on first start:"
info "  kubectl --context $CTX -n $NS logs deploy/api | grep -i 'Seeded admin'"
info "  (only the pod that created the account has it; after a container restart add --previous; after a redeploy it is gone)"
