#!/usr/bin/env bash
# CI: seed the Local import E2E repository on THIS machine (no kubectl, no cluster).
# Runs the exact in-pod seed script embedded in deploy/scripts/seed-e2e-repo.sh (the heredoc between <<'SH' and
# SH), so the CI seed and the Kubernetes seed can never drift: same files, commits, dates and tags v0.1.0/v0.2.0.
#
# Usage: scripts/ci/seed-e2e-repo-local.sh [import-root]   (default: $AIDE_IMPORT_ROOT or /data/import)
# Seeds <import-root>/existing-app. Idempotent (a complete repo is left untouched). Needs git and a POSIX sh.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE="$REPO_ROOT/deploy/scripts/seed-e2e-repo.sh"
IMPORT_ROOT="${1:-${AIDE_IMPORT_ROOT:-/data/import}}"
REPO="$IMPORT_ROOT/existing-app"

# Lines strictly between the heredoc opener (ends in <<'SH') and its terminator (a line that is exactly SH).
INNER="$(awk '/<<'"'"'SH'"'"'$/ { inside = 1; next } inside && /^SH$/ { exit } inside { print }' "$SOURCE")"
if [[ -z "$INNER" ]] || ! grep -q 'git init' <<<"$INNER" || ! grep -q 'v0.2.0' <<<"$INNER"; then
  echo "::error::Could not extract the seed script from deploy/scripts/seed-e2e-repo.sh (heredoc <<'SH' ... SH)." >&2
  exit 1
fi

mkdir -p "$IMPORT_ROOT"
echo "Seeding E2E import repo $REPO (local)"
sh -s -- "$IMPORT_ROOT" "$REPO" <<<"$INNER"
git -C "$REPO" rev-parse -q --verify refs/tags/v0.1.0 >/dev/null
git -C "$REPO" rev-parse -q --verify refs/tags/v0.2.0 >/dev/null
