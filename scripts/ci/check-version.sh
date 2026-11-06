#!/usr/bin/env bash
# FR-229 / FR-231: the Tevrin release version is one semver x.y.z, written in three places that must agree:
#   backend/Directory.Build.props                          <Version> (every assembly and both CLI packages)
#   frontend/package.json                                  "version" (and both versions in frontend/package-lock.json)
#   frontend/src/features/marketing/content/changelog.ts   the newest entry (the first `version: '…'`)
# Usage: scripts/ci/check-version.sh [EXPECTED]
#   With EXPECTED (a release tag, e.g. 0.6.0), the version must also equal it, and the newest changelog entry must have
#   its release date (not `date: null`). Run from the repo root. Tests: scripts/ci/check-version.test.sh.
# CI runs it on every push; the deploy workflow runs it on the tagged commit before a release reaches production.
set -euo pipefail

semver='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
iso_date="^'[0-9]{4}-[0-9]{2}-[0-9]{2}'$"
fail() { echo "::error::$1" >&2; exit 1; }

props="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' backend/Directory.Build.props)"
package="$(sed -n 's/^  "version": "\(.*\)",$/\1/p' frontend/package.json)"
lock="$(sed -n 's/^  "version": "\(.*\)",$/\1/p' frontend/package-lock.json | head -n 1)"
# packages[""] in the lock: the first 6-space-indented "version" (the root package is listed first).
lock_root="$(sed -n 's/^      "version": "\(.*\)",$/\1/p' frontend/package-lock.json | head -n 1)"
changelog_file=frontend/src/features/marketing/content/changelog.ts
changelog="$(sed -n "s/^ *version: '\(.*\)',$/\1/p" "$changelog_file" | head -n 1)"
changelog_date="$(sed -n 's/^ *date: \(.*\),$/\1/p' "$changelog_file" | head -n 1)"

[ "$(printf '%s\n' "$props" | grep -c .)" = 1 ] || fail "backend/Directory.Build.props must have exactly one <Version> (found: '${props}')"
[[ "$props" =~ $semver ]] || fail "backend/Directory.Build.props <Version> '$props' is not x.y.z"
[ "$package" = "$props" ] || fail "frontend/package.json version '$package' differs from backend/Directory.Build.props '$props'"
[ "$lock" = "$props" ] && [ "$lock_root" = "$props" ] \
  || fail "frontend/package-lock.json versions ('$lock', root package '$lock_root') differ from '$props' (run npm install in frontend)"
[ "$changelog" = "$props" ] || fail "the newest changelog entry (changelog.ts) is '$changelog', not '$props'"

if [ "$#" -gt 0 ]; then
  [[ "$1" =~ $semver ]] || fail "release tag '$1' is not x.y.z (e.g. 0.6.0, no 'v' prefix)"
  [ "$1" = "$props" ] || fail "release tag '$1' differs from the version in the tagged commit ('$props')"
  [[ "$changelog_date" =~ $iso_date ]] \
    || fail "the $props changelog entry has no release date (date: $changelog_date): set it before tagging (FR-230)"
fi

echo "Version $props (Directory.Build.props, package.json, changelog)${1:+ matches tag $1}"
