#!/usr/bin/env bash
# Offline validation of the local overlay (no cluster access, no real secrets needed). Used by the gate and CI.
#   1. copies deploy/k8s (minus the real secrets.env and the generated overlay) to a temp dir
#   2. puts secrets.env.example in place as secrets.env in the COPY (the real file is never created or modified)
#   3. renders overlays/local and overlays/local-e2e with `kubectl kustomize`; fails if empty, mode config is wrong
#      or the web wiring (FR-104: BACKEND_URL, /healthz probes, uid 1000) is missing
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

command -v kubeconform >/dev/null 2>&1 || echo "kubeconform not installed; schema validation skipped."

# doc_of KIND NAME < rendered: prints the one YAML document (split on `---`) with top-level `kind: KIND` and
# `metadata.name: NAME` (kustomize output: two-space indent, no comments).
doc_of() {
  awk -v kind="$1" -v name="$2" '
    function flush() { if (k && n) printf "%s", buf; buf = ""; k = 0; n = 0; inmeta = 0 }
    /^---$/ { flush(); next }
    { buf = buf $0 "\n" }
    $0 == "kind: " kind { k = 1 }
    /^metadata:/ { inmeta = 1; next }
    /^[^ ]/ { inmeta = 0 }
    inmeta && $0 == "  name: " name { n = 1 }
    END { flush() }'
}

# Both variants: overlays/local (normal deploy) and overlays/local-e2e (deploy-local.sh --e2e).
for overlay in overlays/local overlays/local-e2e; do
  # Relative paths from inside the copy: no host-path translation issues with kubectl.exe under Git Bash.
  RENDERED="$(cd "$WORK" && kubectl kustomize "$overlay")"
  if [[ -z "${RENDERED//[[:space:]]/}" ]]; then
    echo "ERROR: kubectl kustomize $overlay rendered nothing." >&2
    exit 1
  fi
  COUNT="$(grep -c '^kind:' <<<"$RENDERED" || true)"
  echo "kustomize: $overlay rendered $COUNT resources"

  # Mode-specific config must be where expected: Fake deployments only in the E2E variant.
  if [[ "$overlay" == overlays/local-e2e ]]; then
    for kv in 'Deployments__Provider: Fake' 'Databases__Provider: Fake' 'Deployments__AllowFake: "true"' 'Git__Local__ImportRoot: /data/import' \
              'App__PublicBaseUrl: http://localhost:8080'; do
      grep -qF "$kv" <<<"$RENDERED" || { echo "ERROR: $overlay is missing '$kv'." >&2; exit 1; }
    done
  elif grep -qE 'Deployments__(Provider|AllowFake)|Databases__Provider|Git__Local__ImportRoot' <<<"$RENDERED"; then
    echo "ERROR: $overlay (normal deploy) must not set Fake deployments or databases, or a Local import root." >&2
    exit 1
  fi

  # FR-104: web is the Next.js server, wired to the API through app-config, probed on /healthz, non-root (uid 1000).
  # Asserted inside the web Deployment and the app-config ConfigMap documents only, not across the whole render.
  WEB_DEPLOY="$(doc_of Deployment web <<<"$RENDERED")"
  APP_CONFIG="$(doc_of ConfigMap app-config <<<"$RENDERED")"
  [[ -n "$WEB_DEPLOY" ]] || { echo "ERROR: $overlay has no Deployment 'web'." >&2; exit 1; }
  [[ -n "$APP_CONFIG" ]] || { echo "ERROR: $overlay has no ConfigMap 'app-config'." >&2; exit 1; }
  for kv in 'runAsUser: 1000' 'runAsGroup: 1000' 'name: BACKEND_URL' 'key: BACKEND_URL' 'name: app-config' \
            'readOnlyRootFilesystem: true' 'mountPath: /tmp'; do
    grep -qF "$kv" <<<"$WEB_DEPLOY" || { echo "ERROR: $overlay web Deployment is missing '$kv' (FR-104)." >&2; exit 1; }
  done
  for probe in startupProbe readinessProbe livenessProbe; do
    grep -A3 -E "^ +${probe}:" <<<"$WEB_DEPLOY" | grep -qF 'path: /healthz' \
      || { echo "ERROR: $overlay web Deployment: $probe must probe /healthz (FR-104)." >&2; exit 1; }
  done
  grep -qE '^  BACKEND_URL: "?http://api:8080"?$' <<<"$APP_CONFIG" \
    || { echo "ERROR: $overlay app-config must set BACKEND_URL: http://api:8080 (FR-104)." >&2; exit 1; }
  # No nginx leftovers in the web Deployment or in app-config values (keys stripped), and no edge-proxy trust here.
  if grep -qiE 'nginx|TRUST_EDGE_FORWARDED' <<<"$WEB_DEPLOY" \
     || sed -nE '/^data:/,/^[^ ]/{s/^  [^:]+: *//p}' <<<"$APP_CONFIG" | grep -qi 'nginx' \
     || grep -qE '^  TRUST_EDGE_FORWARDED:' <<<"$APP_CONFIG"; then
    echo "ERROR: $overlay: web/app-config mention nginx or set TRUST_EDGE_FORWARDED (no edge proxy on Docker Desktop)." >&2
    exit 1
  fi

  if command -v kubeconform >/dev/null 2>&1; then
    kubeconform -strict -summary -ignore-missing-schemas <<<"$RENDERED"
  fi
done
echo "Manifests OK."
