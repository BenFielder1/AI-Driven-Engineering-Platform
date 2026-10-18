#!/usr/bin/env bash
# Seed the "existing" Git repository for the Local import E2E (SPEC §8 Testing, FR-73) on the aide-data volume:
#   /data/import/existing-app — README.md, package.json, 3 commits, annotated tags v0.1.0 and v0.2.0.
# Runs inside the api pod (git is in the image) via kubectl exec. Idempotent: an existing, complete repo is left
# untouched; never deletes anything outside an incomplete seed directory. Context docker-desktop only.
# Called by deploy-local.sh --e2e; can be run on its own after an E2E deploy.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

IMPORT_ROOT="${AIDE_IMPORT_ROOT:-/data/import}"
REPO="$IMPORT_ROOT/existing-app"

require_context

step "Seeding E2E import repo $REPO (api pod)"
# Fixed author/committer identity and dates: same commit ids on every fresh seed.
$K -n "$NS" exec -i deploy/api -c api -- sh -s -- "$IMPORT_ROOT" "$REPO" <<'SH'
set -eu
root="$1"; repo="$2"
if [ -d "$repo/.git" ] && git -C "$repo" rev-parse -q --verify refs/tags/v0.1.0 >/dev/null \
   && git -C "$repo" rev-parse -q --verify refs/tags/v0.2.0 >/dev/null; then
  echo "    already seeded: $(git -C "$repo" tag -l | tr '\n' ' ')"
  exit 0
fi
case "$repo" in "$root"/*) ;; *) echo "refusing: $repo is not under $root" >&2; exit 1 ;; esac
rm -rf -- "$repo"          # incomplete earlier attempt only (the complete case exited above)
mkdir -p "$repo"
cd "$repo"
export GIT_AUTHOR_NAME="AIDE E2E Seed" GIT_AUTHOR_EMAIL="seed@aide.local" \
       GIT_COMMITTER_NAME="AIDE E2E Seed" GIT_COMMITTER_EMAIL="seed@aide.local"
commit() { # $1 = date, $2 = message
  GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1" git commit -q -m "$2"
}
tag() { # $1 = date, $2 = tag
  GIT_COMMITTER_DATE="$1" git tag -a "$2" -m "Release $2"
}
git init -q -b main .

cat > README.md <<'MD'
# Existing App

A small "existing" project used by the AIDE import E2E (seeded by deploy/scripts/seed-e2e-repo.sh).
MD
cat > package.json <<'JSON'
{
  "name": "existing-app",
  "version": "0.1.0",
  "private": true,
  "scripts": {
    "start": "node index.js"
  }
}
JSON
printf 'console.log("existing-app 0.1.0");\n' > index.js
git add -A
commit "2026-01-05T10:00:00Z" "Initial commit"
tag    "2026-01-05T10:00:00Z" v0.1.0

sed 's/"version": "0.1.0"/"version": "0.2.0"/' package.json > package.json.new && mv package.json.new package.json
printf 'console.log("existing-app 0.2.0");\n' > index.js
printf '\n## Changelog\n\n- 0.2.0: greeting shows the version\n- 0.1.0: first release\n' >> README.md
git add -A
commit "2026-02-10T10:00:00Z" "Release 0.2.0"
tag    "2026-02-10T10:00:00Z" v0.2.0

printf '\nUnreleased: work after 0.2.0.\n' >> README.md
git add -A
commit "2026-03-01T10:00:00Z" "Docs: note unreleased work"

echo "    seeded $repo: $(git rev-list --count HEAD) commits, tags $(git tag -l | tr '\n' ' ')"
SH
