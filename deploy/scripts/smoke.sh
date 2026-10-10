#!/usr/bin/env bash
# Smoke test for the app on Docker Desktop Kubernetes, through the web entry point (http://localhost:8080):
#   1. GET /                  -> 200 and the SPA shell (<div id="root">)
#   2. GET /api/health/ready  -> 200 (via the nginx /api proxy: DB reachable, migrations applied)
# Retries for up to SMOKE_TIMEOUT seconds (default 60).
set -euo pipefail
BASE_URL="${BASE_URL:-http://localhost:${APP_PORT:-8080}}"
TIMEOUT="${SMOKE_TIMEOUT:-60}"

check() { # $1 = name, $2 = path, $3 = grep pattern for the body (optional)
  local name="$1" path="$2" pattern="${3:-}" deadline body code
  deadline=$(( $(date +%s) + TIMEOUT ))
  while :; do
    code=0
    body="$(curl -sS --max-time 5 -w $'\n%{http_code}' "$BASE_URL$path" 2>&1)" || code=$?
    local status="${body##*$'\n'}"
    body="${body%$'\n'*}"
    if [[ $code -eq 0 && "$status" == "200" ]] && { [[ -z "$pattern" ]] || grep -q "$pattern" <<<"$body"; }; then
      printf 'PASS  %-28s %s%s -> 200\n' "$name" "$BASE_URL" "$path"
      return 0
    fi
    if [[ $(date +%s) -ge $deadline ]]; then
      printf 'FAIL  %-28s %s%s -> %s (curl exit %s)\n' "$name" "$BASE_URL" "$path" "$status" "$code" >&2
      printf '%s\n' "$body" | head -c 2000 >&2; echo >&2
      return 1
    fi
    sleep 3
  done
}

fail=0
check "SPA shell" "/" '<div id="root">' || fail=1
check "SPA history fallback" "/projects/does-not-exist" '<div id="root">' || fail=1
check "API readiness (via proxy)" "/api/health/ready" '"status"' || fail=1

if [[ $fail -ne 0 ]]; then
  echo "Smoke test FAILED. Try: kubectl --context docker-desktop -n aide get pods,svc; ... logs deploy/api" >&2
  exit 1
fi
echo "Smoke test passed: $BASE_URL"
