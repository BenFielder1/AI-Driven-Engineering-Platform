#!/usr/bin/env bash
# FR-261 (0.7.0): the SDLC characterisation snapshots are the behaviour contract of the engine extraction. They were
# committed in 8dce91a (milestone M54, from CI run 44108300811) and no later commit may change, add or remove one.
# Usage: scripts/ci/check-snapshots-frozen.sh   (run from the repo root; fetches the base commit if it's missing)
set -euo pipefail

base=8dce91a747692c1d25493933703f505fb983ead6
dir=backend/tests/Aide.IntegrationTests/Characterisation/Snapshots

if ! git cat-file -e "${base}^{commit}" 2>/dev/null; then
  git fetch --quiet --depth=1 origin "$base"
fi

if ! git diff --exit-code --stat "$base" -- "$dir"; then
  echo "::error::The characterisation snapshots in $dir differ from M54 ($base). They are frozen for 0.7.0 (FR-261): fix the behaviour change instead." >&2
  exit 1
fi
echo "Characterisation snapshots unchanged since M54 ($base)"
