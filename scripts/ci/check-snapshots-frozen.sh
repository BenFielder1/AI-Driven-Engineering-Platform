#!/usr/bin/env bash
# FR-261 (0.7.0): the SDLC characterisation snapshots are the behaviour contract of the engine extraction. They were
# committed in 8dce91a (milestone M54, from CI run 44108300811) and re-sorted once in d4d84f8 (D-0.7.0-22: id-label arrays sorted as sets).
# FR-328 (0.8.0): the rename re-baselined them and the golden prompt files once, in the commit named by `rebaseline`
# below (checked by check-snapshots-rebaseline.sh). No later commit may change, add or remove one.
# While `rebaseline` is empty (the re-baseline commit itself, which can't name its own SHA), the bytes aren't compared:
# check-snapshots-rebaseline.sh stands in, and the next commit sets `rebaseline`.
# Usage: scripts/ci/check-snapshots-frozen.sh   (run from the repo root; fetches the base commit if it's missing)
set -euo pipefail

# frozen BASE OLD_DIR DIR: the files in DIR (working tree) equal the files in OLD_DIR at BASE, byte for byte, with none added
# or removed.
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

rebaseline=
if [ -z "$rebaseline" ]; then
  echo "FR-328 re-baseline commit: byte freeze deferred to the next commit; check-snapshots-rebaseline.sh checks the change"
  exit 0
fi

dir=backend/tests/Tevrin.IntegrationTests/Characterisation/Snapshots
if ! frozen "$rebaseline" "$dir" "$dir"; then
  echo "::error::The characterisation snapshots in $dir differ from the FR-328 re-baseline ($rebaseline). They are frozen (FR-261, FR-328): fix the behaviour change instead." >&2
  exit 1
fi
echo "Characterisation snapshots unchanged since the FR-328 re-baseline ($rebaseline)"

golden_dir=backend/tests/Tevrin.UnitTests/Agents/Golden
if ! frozen "$rebaseline" "$golden_dir" "$golden_dir"; then
  echo "::error::The golden prompt files in $golden_dir differ from the FR-328 re-baseline ($rebaseline). They are frozen (§3.10, FR-328): fix the prompt change instead." >&2
  exit 1
fi
echo "Golden prompt files unchanged since the FR-328 re-baseline ($rebaseline)"
