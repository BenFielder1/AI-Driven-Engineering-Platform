#!/usr/bin/env bash
# Smoke test for the app on Docker Desktop Kubernetes, through the web entry point (http://localhost:8080), which is
# the Next.js server (FR-103, FR-104). Only HTTP requests to BASE_URL: no kubectl, so it never touches a cluster
# context (diagnostic hints name --context docker-desktop only).
#   1. GET /healthz           -> 200, body "ok" (web liveness, does not call the API)
#   2. GET /                  -> 307 (redirect to /projects) or 200
#   3. GET /login             -> 200 with the FR-99 headers: Content-Security-Policy with a 'nonce-…' source,
#                                X-Frame-Options DENY, X-Content-Type-Options nosniff
#   4. GET /projects/<uuid>   -> 200 HTML (dynamic client route; the auth guard runs in the browser)
#   5. GET /api/health/ready  -> 200 (via the web server's /api proxy: DB reachable, migrations applied)
# Each check retries for up to SMOKE_TIMEOUT seconds (default 60).
set -euo pipefail
BASE_URL="${BASE_URL:-http://localhost:${APP_PORT:-8080}}"
TIMEOUT="${SMOKE_TIMEOUT:-60}"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/aide-smoke.XXXXXX")"
cleanup() { [[ -n "${WORK:-}" && -d "$WORK" && "$WORK" == *aide-smoke.* ]] && rm -rf -- "$WORK"; }
trap cleanup EXIT

# check NAME PATH STATUS_REGEX [BODY_REGEX] [HEADER_REGEX...]
#   STATUS_REGEX: extended regex for the status code (e.g. '200', '200|307'). Redirects are not followed.
#   BODY_REGEX:   extended regex the body must match ('' = no body check).
#   HEADER_REGEX: case-insensitive extended regexes; each must match one response header line (CR stripped).
check() {
  local name="$1" path="$2" want_status="$3" body_re="${4:-}"
  shift 3; [[ $# -gt 0 ]] && shift
  local headers_file="$WORK/headers" body_file="$WORK/body" deadline status code reason re
  deadline=$(( $(date +%s) + TIMEOUT ))
  while :; do
    code=0; reason=""
    status="$(curl -sS --max-time 5 -o "$body_file" -D "$headers_file" -w '%{http_code}' "$BASE_URL$path" 2>"$WORK/err")" || code=$?
    if [[ $code -ne 0 ]]; then
      reason="curl exit $code: $(head -c 300 "$WORK/err")"
    elif ! [[ "$status" =~ ^($want_status)$ ]]; then
      reason="status $status, want $want_status"
    elif [[ -n "$body_re" ]] && ! grep -qE "$body_re" "$body_file"; then
      reason="body does not match /$body_re/"
    else
      for re in "$@"; do
        if ! tr -d '\r' <"$headers_file" | grep -qiE "$re"; then reason="missing header /$re/"; break; fi
      done
    fi
    if [[ -z "$reason" ]]; then
      printf 'PASS  %-30s %s%s -> %s\n' "$name" "$BASE_URL" "$path" "$status"
      return 0
    fi
    if [[ $(date +%s) -ge $deadline ]]; then
      printf 'FAIL  %-30s %s%s -> %s\n' "$name" "$BASE_URL" "$path" "$reason" >&2
      if [[ -s "$headers_file" ]]; then tr -d '\r' <"$headers_file" | head -n 30 >&2; fi
      if [[ -s "$body_file" ]]; then head -c 2000 "$body_file" >&2; echo >&2; fi
      return 1
    fi
    sleep 3
  done
}

fail=0
check "Web liveness" "/healthz" '200' '^ok$' || fail=1
check "Home (redirect or page)" "/" '200|307' || fail=1
check "App route + FR-99 headers" "/login" '200' '' \
  "^content-security-policy:.*'nonce-[A-Za-z0-9+/=]+'" \
  '^x-frame-options:[[:space:]]*DENY[[:space:]]*$' \
  '^x-content-type-options:[[:space:]]*nosniff[[:space:]]*$' || fail=1
# Dynamic route: client-only page (no server redirect, no middleware), so signed out it is 200 HTML; the client-side
# (app) guard then sends the browser to /login.
check "Dynamic route" "/projects/00000000-0000-0000-0000-000000000000" '200' '<html' || fail=1
check "API readiness (via proxy)" "/api/health/ready" '200' '"status"' || fail=1

if [[ $fail -ne 0 ]]; then
  echo "Smoke test FAILED. Try: kubectl --context docker-desktop -n aide get pods,svc; ... logs deploy/web; ... logs deploy/api" >&2
  exit 1
fi
echo "Smoke test passed: $BASE_URL"
