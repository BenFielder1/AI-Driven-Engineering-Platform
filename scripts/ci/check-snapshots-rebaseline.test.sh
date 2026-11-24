#!/usr/bin/env bash
# FR-328: tests for scripts/ci/check-snapshots-rebaseline.sh against a throwaway Git repo holding pre-rename snapshots.
# Usage: bash scripts/ci/check-snapshots-rebaseline.test.sh (from the repo root; CI runs it in the `scripts` job).
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/check-snapshots-rebaseline.sh"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
failures=0

old=old/Snapshots
new=new/Snapshots
hash_a=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
hash_b=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb

repo="$work/repo"
mkdir -p "$repo/$old"
printf '{"actor":"aide-system","mergeStrategy":"aide_merges","branch":"tevrin/app/v1","contentHash":"%s"}\n' "$hash_a" > "$repo/$old/flow.json"
git -C "$repo" init --quiet
git -C "$repo" -c core.autocrlf=false add -A
git -C "$repo" -c user.email=ci@example.invalid -c user.name=ci commit --quiet -m base
base="$(git -C "$repo" rev-parse HEAD)"

# expect NAME WANT CONTENT: writes CONTENT as the new flow.json and checks the script's exit status is WANT (0 or 1).
expect() {
  local name="$1" want="$2" content="$3" got=0
  rm -rf -- "$repo/new"
  mkdir -p "$repo/$new"
  printf '%s\n' "$content" > "$repo/$new/flow.json"
  (cd "$repo" && bash "$script" "$base" "$old" "$new" >/dev/null 2>&1) || got=1
  if [ "$got" != "$want" ]; then
    echo "FAIL: $name (exit $got, wanted $want)" >&2
    failures=$((failures + 1))
  else
    echo "ok: $name"
  fi
}

expect "renamed names and a new content hash pass" 0 \
  "{\"actor\":\"tevrin-system\",\"mergeStrategy\":\"tevrin_merges\",\"branch\":\"tevrin/app/v1\",\"contentHash\":\"$hash_b\"}"
expect "an unchanged file passes" 0 \
  "{\"actor\":\"aide-system\",\"mergeStrategy\":\"aide_merges\",\"branch\":\"tevrin/app/v1\",\"contentHash\":\"$hash_a\"}"
expect "a behaviour change fails" 1 \
  "{\"actor\":\"tevrin-system\",\"mergeStrategy\":\"manual\",\"branch\":\"tevrin/app/v1\",\"contentHash\":\"$hash_b\"}"
expect "a renamed value that isn't the old one fails" 1 \
  "{\"actor\":\"tevrin-robot\",\"mergeStrategy\":\"tevrin_merges\",\"branch\":\"tevrin/app/v1\",\"contentHash\":\"$hash_b\"}"

# An added file fails.
rm -rf -- "$repo/new"; mkdir -p "$repo/$new"
git -C "$repo" show "$base:$old/flow.json" > "$repo/$new/flow.json"
echo '{}' > "$repo/$new/extra.json"
if (cd "$repo" && bash "$script" "$base" "$old" "$new" >/dev/null 2>&1); then
  echo "FAIL: an added file passed" >&2; failures=$((failures + 1))
else
  echo "ok: an added file fails"
fi

# A removed file fails.
rm -rf -- "$repo/new"; mkdir -p "$repo/$new"
if (cd "$repo" && bash "$script" "$base" "$old" "$new" >/dev/null 2>&1); then
  echo "FAIL: a removed file passed" >&2; failures=$((failures + 1))
else
  echo "ok: a removed file fails"
fi

if [ "$failures" -gt 0 ]; then
  echo "$failures check-snapshots-rebaseline test(s) failed" >&2
  exit 1
fi
echo "check-snapshots-rebaseline tests passed"
