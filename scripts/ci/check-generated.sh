#!/usr/bin/env bash
# Fails if generated files differ from what is committed (modified, added, deleted or untracked).
# Run AFTER regenerating them; this script only compares the working tree with the index/HEAD.
#
# Usage: scripts/ci/check-generated.sh [target|path]...
#   (no args)  all generated artefacts (same as: contract client)
#   contract   backend/openapi/api.json        regenerate: dotnet build backend/src/Aide.Api
#   client     frontend/src/api/generated/     regenerate: npm --prefix frontend run gen:api
#   all        both of the above
#   <path>     any other repo-relative path, checked as-is
#
# Works from any working directory, on ubuntu (CI) and Git Bash on Windows.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

paths=()
hints=()
add_target() {
  case "$1" in
    contract)
      paths+=("backend/openapi/api.json")
      hints+=("contract: run 'dotnet build backend/src/Aide.Api' and commit backend/openapi/api.json")
      ;;
    client)
      paths+=("frontend/src/api/generated")
      hints+=("client: run 'npm --prefix frontend run gen:api' and commit frontend/src/api/generated")
      ;;
    all)
      add_target contract
      add_target client
      ;;
    -h | --help)
      sed -n '2,12p' "${BASH_SOURCE[0]}"
      exit 0
      ;;
    *)
      if [[ ! -e "$1" ]] && ! git ls-files --error-unmatch -- "$1" >/dev/null 2>&1; then
        echo "ERROR: unknown target or path '$1' (targets: contract, client, all)." >&2
        exit 2
      fi
      paths+=("$1")
      hints+=("$1: regenerate it and commit the result")
      ;;
  esac
}

if [[ $# -eq 0 ]]; then
  add_target all
else
  for arg in "$@"; do add_target "$arg"; done
fi

changes="$(git status --porcelain --untracked-files=all -- "${paths[@]}")"
if [[ -n "$changes" ]]; then
  # ::error:: is a GitHub Actions annotation; elsewhere it is just a readable prefix.
  echo "::error::Generated files are out of date."
  printf '  %s\n' "${hints[@]}"
  echo "$changes"
  git --no-pager diff --stat -- "${paths[@]}"
  git --no-pager diff -- "${paths[@]}" | head -n 200
  exit 1
fi
echo "Generated files up to date: ${paths[*]}"
