#!/usr/bin/env bash
# FR-229 / FR-231: tests for scripts/ci/check-version.sh against fixture copies of the three version sources.
# Usage: bash scripts/ci/check-version.test.sh (from the repo root; CI runs it in the `scripts` job).
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/check-version.sh"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
failures=0

# fixture VERSION PACKAGE LOCK LOCK_ROOT CHANGELOG_VERSION CHANGELOG_DATE: writes the three sources into a fresh dir.
fixture() {
  local dir="$work/case"
  rm -rf -- "$dir"
  mkdir -p "$dir/backend" "$dir/frontend/src/features/marketing/content"
  printf '<Project>\n  <PropertyGroup>\n    <Version>%s</Version>\n  </PropertyGroup>\n</Project>\n' "$1" \
    > "$dir/backend/Directory.Build.props"
  printf '{\n  "name": "aide-frontend",\n  "version": "%s",\n  "private": true\n}\n' "$2" > "$dir/frontend/package.json"
  printf '{\n  "name": "aide-frontend",\n  "version": "%s",\n  "packages": {\n    "": {\n      "name": "aide-frontend",\n      "version": "%s",\n      "dependencies": {}\n    }\n  }\n}\n' \
    "$3" "$4" > "$dir/frontend/package-lock.json"
  # The interface's `version: string` / `date: string | null` come first, as in the real file, and must not match.
  printf "export interface ChangelogEntry {\n  version: string\n  date: string | null\n}\n\nexport const changelog = [\n  {\n    version: '%s',\n    date: %s,\n  },\n  {\n    version: '0.1.0',\n    date: '2026-10-10',\n  },\n]\n" \
    "$5" "$6" > "$dir/frontend/src/features/marketing/content/changelog.ts"
  echo "$dir"
}

# expect pass|fail NAME DIR [ARGS...]
expect() {
  local want="$1" name="$2" dir="$3" got
  shift 3
  if (cd "$dir" && bash "$script" "$@") >/dev/null 2>&1; then got=pass; else got=fail; fi
  if [ "$got" = "$want" ]; then
    echo "ok    $name"
  else
    echo "FAIL  $name (expected $want, got $got)"
    failures=$((failures + 1))
  fi
}

ok="$(fixture 0.6.0 0.6.0 0.6.0 0.6.0 0.6.0 "'2026-11-07'")"
expect pass FR229_consistent_sources "$ok"
expect pass FR231_tag_matches_version "$ok" 0.6.0
expect fail FR231_v_prefixed_tag "$ok" v0.6.0
expect fail FR231_tag_differs_from_version "$ok" 0.7.0

expect fail FR229_package_json_differs "$(fixture 0.6.0 0.5.0 0.6.0 0.6.0 0.6.0 "'2026-11-07'")"
expect fail FR229_lock_differs "$(fixture 0.6.0 0.6.0 0.5.0 0.6.0 0.6.0 "'2026-11-07'")"
expect fail FR229_lock_root_package_differs "$(fixture 0.6.0 0.6.0 0.6.0 0.5.0 0.6.0 "'2026-11-07'")"
expect fail FR229_changelog_differs "$(fixture 0.6.0 0.6.0 0.6.0 0.6.0 0.5.0 "'2026-11-07'")"
expect fail FR229_not_semver "$(fixture 0.6 0.6 0.6 0.6 0.6 "'2026-11-07'")"

undated="$(fixture 0.6.0 0.6.0 0.6.0 0.6.0 0.6.0 null)"
expect pass FR229_undated_entry_ok_while_in_development "$undated"
expect fail FR230_release_needs_changelog_date "$undated" 0.6.0

[ "$failures" = 0 ] || { echo "$failures check-version test(s) failed"; exit 1; }
echo "check-version: all tests passed"
