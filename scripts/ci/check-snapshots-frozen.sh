#!/usr/bin/env bash
# FR-261 (0.7.0): the SDLC characterisation snapshots are the behaviour contract of the engine extraction. They were
# committed in 8dce91a (milestone M54, from CI run 44108300811) and re-sorted once in d4d84f8 (D-0.7.0-22: id-label arrays sorted as sets) and no later commit may change, add or remove one.
# Usage: scripts/ci/check-snapshots-frozen.sh   (run from the repo root; fetches the base commit if it's missing)
set -euo pipefail

# frozen BASE OLD_DIR DIR: the files in DIR (working tree) equal the files in OLD_DIR at BASE, byte for byte, with none added
# or removed. FR-323 (0.8.0) moved the folders with their projects (Aide.* -> Tevrin.*); the base commits hold the old paths.
frozen() {
  local base="$1" old_dir="$2" dir="$3" bad=0 name
  if ! git cat-file -e "${base}^{commit}" 2>/dev/null; then
    git fetch --quiet --depth=1 origin "$base"
  fi
  local expected actual
  expected="$(git ls-tree -r --name-only "$base" -- "$old_dir/" | sed "s|^$old_dir/||" | LC_ALL=C sort)"
  actual="$( (cd "$dir" 2>/dev/null && find . -type f | sed 's|^\./||') | LC_ALL=C sort)"
  if [ -z "$expected" ]; then
    echo "No files under $old_dir at $base" >&2
    return 1
  fi
  if [ "$expected" != "$actual" ]; then
    echo "The file list in $dir differs from $old_dir at $base:" >&2
    diff <(echo "$expected") <(echo "$actual") >&2 || true
    bad=1
  fi
  while IFS= read -r name; do
    if [ -f "$dir/$name" ] && ! git show "$base:$old_dir/$name" | cmp -s - "$dir/$name"; then
      echo "changed: $dir/$name" >&2
      bad=1
    fi
  done <<< "$expected"
  return "$bad"
}

base=d4d84f86b1504b983f287d904551d8b372b6e3df
old_dir=backend/tests/Aide.IntegrationTests/Characterisation/Snapshots
dir=backend/tests/Tevrin.IntegrationTests/Characterisation/Snapshots

if ! frozen "$base" "$old_dir" "$dir"; then
  echo "::error::The characterisation snapshots in $dir differ from M54 ($base). They are frozen for 0.7.0 (FR-261): fix the behaviour change instead." >&2
  exit 1
fi
echo "Characterisation snapshots unchanged since M54 ($base)"

# FR-317-FR-321 (§3.10): the golden prompt files pin all eight SDLC agents' templates and rendered prompts. They were
# committed in b593268 (M59) and stay frozen while the SDLC moves onto the engine runner.
golden_base=b593268b11278a6912b0685f9e7e5885a916f7ce
golden_old_dir=backend/tests/Aide.UnitTests/Agents/Golden
golden_dir=backend/tests/Tevrin.UnitTests/Agents/Golden

if ! frozen "$golden_base" "$golden_old_dir" "$golden_dir"; then
  echo "::error::The golden prompt files in $golden_dir differ from M59 ($golden_base). They are frozen for 0.7.0 (§3.10): fix the prompt change instead." >&2
  exit 1
fi
echo "Golden prompt files unchanged since M59 ($golden_base)"
