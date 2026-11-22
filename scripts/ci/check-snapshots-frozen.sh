#!/usr/bin/env bash
# FR-261 (0.7.0): the SDLC characterisation snapshots are the behaviour contract of the engine extraction. They were
# committed in 8dce91a (milestone M54, from CI run 44108300811) and re-sorted once in d4d84f8 (D-0.7.0-22: id-label arrays sorted as sets) and no later commit may change, add or remove one.
# Usage: scripts/ci/check-snapshots-frozen.sh   (run from the repo root; fetches the base commit if it's missing)
set -euo pipefail

base=d4d84f86b1504b983f287d904551d8b372b6e3df
dir=backend/tests/Aide.IntegrationTests/Characterisation/Snapshots

if ! git cat-file -e "${base}^{commit}" 2>/dev/null; then
  git fetch --quiet --depth=1 origin "$base"
fi

if ! git diff --exit-code --stat "$base" -- "$dir"; then
  echo "::error::The characterisation snapshots in $dir differ from M54 ($base). They are frozen for 0.7.0 (FR-261): fix the behaviour change instead." >&2
  exit 1
fi
echo "Characterisation snapshots unchanged since M54 ($base)"

# FR-317-FR-321 (§3.10): the golden prompt files pin all eight SDLC agents' templates and rendered prompts. They were
# committed in b593268 (M59) and stay frozen while the SDLC moves onto the engine runner.
golden_base=b593268b11278a6912b0685f9e7e5885a916f7ce
golden_dir=backend/tests/Aide.UnitTests/Agents/Golden

if ! git cat-file -e "${golden_base}^{commit}" 2>/dev/null; then
  git fetch --quiet --depth=1 origin "$golden_base"
fi

if ! git diff --exit-code --stat "$golden_base" -- "$golden_dir"; then
  echo "::error::The golden prompt files in $golden_dir differ from M59 ($golden_base). They are frozen for 0.7.0 (§3.10): fix the prompt change instead." >&2
  exit 1
fi
echo "Golden prompt files unchanged since M59 ($golden_base)"
