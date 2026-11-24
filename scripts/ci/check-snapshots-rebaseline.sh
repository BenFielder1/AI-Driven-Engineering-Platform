#!/usr/bin/env bash
# FR-328 (0.8.0): the one rename re-baseline of the SDLC characterisation snapshots and golden prompt files. Each current
# file must equal its pre-rename copy once both are mapped: `tevrin` becomes `aide` (any case) and content hashes
# (64 hex digits) are dropped. The same files must exist on both sides. So the re-baseline changes names only, never
# behaviour. The bases are the 0.7.0 freeze commits (M54 for the snapshots, M59 for the golden prompts).
# Usage: scripts/ci/check-snapshots-rebaseline.sh   (run from the repo root; fetches a base commit if it's missing)
#        scripts/ci/check-snapshots-rebaseline.sh BASE OLD_DIR DIR   (one comparison; used by the .test.sh)
set -euo pipefail

# The mapping applied to both sides before comparing.
rename_map() {
  sed -E 's/tevrin/aide/g; s/Tevrin/Aide/g; s/TEVRIN/AIDE/g; s/[0-9a-f]{64}/<hash>/g'
}

# mapped_equal BASE OLD_DIR DIR: every file in DIR (working tree) equals the file of the same name in OLD_DIR at BASE
# after rename_map, with none added or removed.
mapped_equal() {
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
    [ -f "$dir/$name" ] || continue
    if ! diff -q <(git show "$base:$old_dir/$name" | rename_map) <(rename_map < "$dir/$name") >/dev/null; then
      echo "differs beyond the rename: $dir/$name" >&2
      diff <(git show "$base:$old_dir/$name" | rename_map) <(rename_map < "$dir/$name") | head -20 >&2 || true
      bad=1
    fi
  done <<< "$expected"
  return "$bad"
}

if [ "$#" -eq 3 ]; then
  mapped_equal "$1" "$2" "$3"
  exit $?
fi

status=0
if mapped_equal d4d84f86b1504b983f287d904551d8b372b6e3df \
  backend/tests/Aide.IntegrationTests/Characterisation/Snapshots \
  backend/tests/Tevrin.IntegrationTests/Characterisation/Snapshots; then
  echo "Characterisation snapshots equal M54 apart from the rename (FR-328)"
else
  echo "::error::A characterisation snapshot differs from M54 beyond the tevrin/aide rename and content hashes (FR-328)." >&2
  status=1
fi

if mapped_equal b593268b11278a6912b0685f9e7e5885a916f7ce \
  backend/tests/Aide.UnitTests/Agents/Golden \
  backend/tests/Tevrin.UnitTests/Agents/Golden; then
  echo "Golden prompt files equal M59 apart from the rename (FR-328)"
else
  echo "::error::A golden prompt file differs from M59 beyond the tevrin/aide rename and content hashes (FR-328)." >&2
  status=1
fi
# The mapping can't tell `aide` from `tevrin`, so also require that no snapshot or golden prompt holds a value 0.8.0
# writes under its new name (a writer that went back to the old name would otherwise pass).
old_writers='aide-system|aide_merges|aide-plan|aide-stack-profile|\.aide-fake/|`\.aide/`'
if grep -rlE "$old_writers" backend/tests/Tevrin.IntegrationTests/Characterisation/Snapshots backend/tests/Tevrin.UnitTests/Agents/Golden >&2; then
  echo "::error::A snapshot or golden prompt (listed above) holds an old name that 0.8.0 writes as tevrin (FR-326, FR-328)." >&2
  status=1
else
  echo "No snapshot or golden prompt holds an old written name (FR-326)"
fi
exit "$status"
