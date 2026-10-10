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

cd "$REPO_ROOT"
