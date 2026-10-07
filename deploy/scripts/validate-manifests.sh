#!/usr/bin/env bash
# Offline validation of the local overlay (no cluster access, no real secrets needed). Used by the gate and CI.
#   1. copies deploy/k8s (minus the real secrets.env and the generated overlay) to a temp dir
#   2. puts secrets.env.example in place as secrets.env in the COPY (the real file is never created or modified)
#   3. renders overlays/local with `kubectl kustomize` and fails if the output is empty
#   4. runs kubeconform if it is installed
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$REPO_ROOT/deploy/k8s"

command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl not found." >&2; exit 1; }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/aide-k8s-validate.XXXXXX")"
cleanup() { [[ -n "${WORK:-}" && -d "$WORK" && "$WORK" == *aide-k8s-validate.* ]] && rm -rf -- "$WORK"; }
trap cleanup EXIT

# Copy without the real secrets file or generated output (tar keeps relative layout; works in Git Bash and Linux).
tar -C "$SRC" --exclude='./overlays/local/secrets.env' --exclude='./.generated' -cf - . | tar -C "$WORK" -xf -
cp "$WORK/overlays/local/secrets.env.example" "$WORK/overlays/local/secrets.env"

# Relative paths from inside the copy: no host-path translation issues with kubectl.exe under Git Bash.
RENDERED="$(cd "$WORK" && kubectl kustomize overlays/local)"
if [[ -z "${RENDERED//[[:space:]]/}" ]]; then
  echo "ERROR: kubectl kustomize rendered nothing." >&2
  exit 1
fi
COUNT="$(grep -c '^kind:' <<<"$RENDERED" || true)"
echo "kustomize: overlays/local rendered $COUNT resources"

if command -v kubeconform >/dev/null 2>&1; then
  kubeconform -strict -summary -ignore-missing-schemas <<<"$RENDERED"
else
  echo "kubeconform not installed; schema validation skipped."
fi
echo "Manifests OK."
