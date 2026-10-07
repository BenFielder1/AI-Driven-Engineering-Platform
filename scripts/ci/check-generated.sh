#!/usr/bin/env bash
# Fails if any of the given paths differ from what is committed (modified, added or untracked).
# Usage: scripts/ci/check-generated.sh <hint> <path>...
# Run after regenerating generated files (openapi/api.json, src/api/generated, routeTree.gen.ts).
set -euo pipefail

hint="$1"
shift

changes="$(git status --porcelain --untracked-files=all -- "$@")"
if [[ -n "$changes" ]]; then
  echo "::error::Generated files are out of date. ${hint}"
  echo "$changes"
  git --no-pager diff --stat -- "$@"
  git --no-pager diff -- "$@" | head -n 200
  exit 1
fi
echo "Up to date: $*"
