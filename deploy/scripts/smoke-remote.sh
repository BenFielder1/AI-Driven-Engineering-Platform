#!/usr/bin/env bash
# FR-111: smoke test for a deployed environment (Railway preview/production), through the public web URL only.
# Usage: deploy/scripts/smoke-remote.sh <base-url>        e.g. https://web-preview-1234.up.railway.app
#
# Read-only checks (always):
#   1. GET  /healthz              -> 200, body "ok"
#   2. GET  /api/health/ready     -> 200 via the web server's /api proxy (DB reachable, migrations applied)
#   3. GET  /login                -> 200 with the FR-99 headers (CSP 'nonce-…', X-Frame-Options DENY, nosniff)
#   4. GET  /api/auth/me          -> 401 application/problem+json, errorCode UNAUTHENTICATED (no cookie sent)
#   5. API not public             -> with API_PUBLIC_URL set (a URL the API must NOT answer on), GET
#                                    $API_PUBLIC_URL/api/health/live must not reach the API; skipped otherwise. In CI,
#                                    deploy.yml asserts it from Railway first (api has no domain and no TCP proxy).
# Sign-in round trip (only when SMOKE_EMAIL and SMOKE_PASSWORD are set; an existing, non-admin account):
#   6. GET  /api/auth/antiforgery -> 204 (XSRF-TOKEN cookie)
#   7. POST /api/auth/login       -> 204 (auth cookie)
#   8. GET  /api/projects         -> 200
#   9. WS   /api/hubs/projects    -> SignalR handshake completes (deploy/scripts/smoke-ws.mjs, Node 24)
#  10. POST /api/auth/logout      -> 204 with X-XSRF-TOKEN
# Never registers accounts (FR-3: the first account becomes Admin). Never prints cookies, tokens or the password.
# Retries failing checks until SMOKE_TIMEOUT seconds (default 180) after the start, then exits 1 naming the checks.
# Needs: bash, curl (7.55+ for `-H @file`), grep, sed, awk; node 24 for checks 7 and 9.
set -euo pipefail

usage() { echo "Usage: $0 <base-url>   (env: SMOKE_EMAIL, SMOKE_PASSWORD, API_PUBLIC_URL, SMOKE_TIMEOUT)" >&2; exit 2; }
[[ $# -eq 1 ]] || usage
BASE_URL="${1%/}"
[[ "$BASE_URL" =~ ^https?://[^/]+$ ]] || { echo "base-url must be an origin like https://host (no path)" >&2; usage; }

TIMEOUT="${SMOKE_TIMEOUT:-180}"
START="$(date +%s)"
DEADLINE=$(( START + TIMEOUT ))
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/aide-smoke-remote.XXXXXX")"
chmod 700 "$WORK"
cleanup() { [[ -n "${WORK:-}" && -d "$WORK" && "$WORK" == *aide-smoke-remote.* ]] && rm -rf -- "$WORK"; }
trap cleanup EXIT
JAR="$WORK/cookies"
FAILED=()

pass() { printf 'PASS  %-28s %s\n' "$1" "$2"; }
skip() { printf 'SKIP  %-28s %s\n' "$1" "$2"; }
failed() {
  printf 'FAIL  %-28s %s\n' "$1" "$2" >&2
  FAILED+=("$1")
}
out_of_time() { [[ $(date +%s) -ge $DEADLINE ]]; }

# request METHOD PATH [curl args...]: one request to BASE_URL with the session cookie jar. Sets STATUS (000 on a
# transport error) and CURL_ERR; body in $WORK/body, headers in $WORK/headers (CR stripped).
request() {
  local method="$1" path="$2"
  shift 2
  local code=0
  STATUS="$(curl -sS --max-time 15 -X "$method" -b "$JAR" -c "$JAR" -o "$WORK/body" -D "$WORK/headers.raw" \
    -w '%{http_code}' "$@" "$BASE_URL$path" 2>"$WORK/err")" || code=$?
  CURL_ERR=""
  if [[ $code -ne 0 ]]; then
    STATUS="000"
    CURL_ERR="curl exit $code: $(head -c 200 "$WORK/err")"
  fi
  tr -d '\r' <"$WORK/headers.raw" >"$WORK/headers" 2>/dev/null || : >"$WORK/headers"
}

has_header() { grep -qiE "$1" "$WORK/headers"; }

# xsrf_header_file: writes the X-XSRF-TOKEN request header to a file in the private temp dir and prints its path. curl
# reads it with `-H @file`, so the token never appears in argv (visible to other processes on the machine).
xsrf_header_file() {
  printf 'X-XSRF-TOKEN: %s\n' "$(cookie XSRF-TOKEN)" >"$WORK/xsrf-header"
  printf '%s' "$WORK/xsrf-header"
}

# cookie NAME: the value of a cookie in the jar (Netscape format; HttpOnly lines start with #HttpOnly_).
cookie() {
  awk -v n="$1" 'BEGIN { FS = "\t" } { sub(/^#HttpOnly_/, "", $1) } $0 !~ /^#/ && NF >= 7 && $6 == n { v = $7 } END { print v }' "$JAR" 2>/dev/null
}

# cookie_header: "a=1; b=2" for every cookie in the jar (for the WebSocket request).
cookie_header() {
  awk 'BEGIN { FS = "\t"; s = "" } { sub(/^#HttpOnly_/, "", $1) } $0 !~ /^#/ && NF >= 7 { s = s (s == "" ? "" : "; ") $6 "=" $7 } END { print s }' "$JAR" 2>/dev/null
}

# retry NAME FN: runs FN (which sets REASON, empty on success) until it passes or the deadline is reached.
# Every check gets at least one attempt. FN may set FATAL=1 for failures that retrying can't fix.
retry() {
  local name="$1" fn="$2"
  while :; do
    REASON="" FATAL=0
    "$fn"
    if [[ -z "$REASON" ]]; then pass "$name" "$DETAIL"; return 0; fi
    if [[ $FATAL -eq 1 ]] || out_of_time; then failed "$name" "$REASON"; return 1; fi
    sleep 5
  done
}

# --- Read-only checks --------------------------------------------------------------------------------------------
check_healthz() {
  request GET /healthz; DETAIL="/healthz -> $STATUS"
  if [[ $STATUS != 200 ]]; then REASON="status $STATUS, want 200 ${CURL_ERR}"
  elif ! grep -qE '^ok$' "$WORK/body"; then REASON='body is not "ok"'; fi
}

check_ready() {
  request GET /api/health/ready; DETAIL="/api/health/ready -> $STATUS"
  if [[ $STATUS != 200 ]]; then REASON="status $STATUS, want 200 (502: api down; 503: DB or migrations) ${CURL_ERR}"; fi
}

check_login_page() {
  request GET /login; DETAIL="/login -> $STATUS (CSP nonce, XFO DENY, nosniff)"
  if [[ $STATUS != 200 ]]; then REASON="status $STATUS, want 200 ${CURL_ERR}"
  elif ! has_header "^content-security-policy:.*'nonce-[A-Za-z0-9+/=]+'"; then REASON="no CSP header with a 'nonce-…' source"
  elif ! has_header '^x-frame-options:[[:space:]]*DENY[[:space:]]*$'; then REASON='X-Frame-Options is not DENY'
  elif ! has_header '^x-content-type-options:[[:space:]]*nosniff[[:space:]]*$'; then REASON='X-Content-Type-Options is not nosniff'; fi
}

check_me_anonymous() {
  : >"$JAR"  # no cookies at all
  request GET /api/auth/me; DETAIL="/api/auth/me (no cookie) -> $STATUS problem+json"
  if [[ $STATUS != 401 ]]; then REASON="status $STATUS, want 401 ${CURL_ERR}"
  elif ! has_header '^content-type:[[:space:]]*application/problem\+json'; then REASON='content type is not application/problem+json'
  elif ! grep -q '"errorCode":"UNAUTHENTICATED"' "$WORK/body"; then REASON='body has no errorCode UNAUTHENTICATED'; fi
}

check_api_not_public() {
  local url="${API_PUBLIC_URL%/}/api/health/live" status code=0
  DETAIL="$url not served by the API"
  status="$(curl -sS --max-time 10 -o "$WORK/api-body" -w '%{http_code}' "$url" 2>/dev/null)" || code=$?
  # A transport error or Railway's edge 404 ("Application not found") means nothing public answers. A 2xx, or a
  # problem+json from ASP.NET, means the API is publicly reachable. Retrying can't change that, so it fails at once.
  if [[ $code -eq 0 && ( $status == 2* || $status == 401 || $status == 403 ) ]]; then
    REASON="the API answered $status at a public URL: remove its public domain/TCP proxy"; FATAL=1
  fi
  DETAIL="$DETAIL (${status:-no response}"; [[ $code -ne 0 ]] && DETAIL="$DETAIL, curl exit $code"; DETAIL="$DETAIL)"
}

# --- Sign-in round trip ------------------------------------------------------------------------------------------
check_antiforgery() {
  : >"$JAR"
  request GET /api/auth/antiforgery; DETAIL="/api/auth/antiforgery -> $STATUS"
  if [[ $STATUS != 204 ]]; then REASON="status $STATUS, want 204 ${CURL_ERR}"
  elif [[ -z "$(cookie XSRF-TOKEN)" ]]; then REASON='no XSRF-TOKEN cookie'; fi
}

check_login() {
  # JSON-encode the credentials with node (any character is safe); the body file stays in the private temp dir.
  SMOKE_EMAIL="$SMOKE_EMAIL" SMOKE_PASSWORD="$SMOKE_PASSWORD" node -e \
    'process.stdout.write(JSON.stringify({ email: process.env.SMOKE_EMAIL, password: process.env.SMOKE_PASSWORD }))' \
    >"$WORK/login.json"
  request POST /api/auth/login -H 'Content-Type: application/json' -H "@$(xsrf_header_file)" \
    --data-binary "@$WORK/login.json"
  rm -f -- "$WORK/login.json"
  DETAIL="/api/auth/login -> $STATUS"
  if [[ $STATUS == 204 ]]; then return; fi
  REASON="status $STATUS, want 204 ${CURL_ERR}"
  # Wrong credentials and the per-IP rate limit don't get better by retrying (and retries would lock the account).
  if [[ $STATUS == 400 || $STATUS == 401 || $STATUS == 429 ]]; then
    REASON="$REASON (check the SMOKE_EMAIL account exists and the password is right; 429 = rate limited)"; FATAL=1
  fi
}

check_projects() {
  request GET /api/projects; DETAIL="/api/projects -> $STATUS"
  if [[ $STATUS != 200 ]]; then REASON="status $STATUS, want 200 ${CURL_ERR}"; fi
}

check_hub() {
  local out code=0
  DETAIL="/api/hubs/projects WebSocket + SignalR handshake"
  out="$(SMOKE_WS_BASE_URL="$BASE_URL" SMOKE_WS_COOKIE="$(cookie_header)" node "$SCRIPT_DIR/smoke-ws.mjs" 2>&1)" || code=$?
  if [[ $code -ne 0 ]]; then REASON="${out:-node exit $code}"; fi
}

check_logout() {
  request POST /api/auth/logout -H "@$(xsrf_header_file)"; DETAIL="/api/auth/logout -> $STATUS"
  if [[ $STATUS != 204 ]]; then REASON="status $STATUS, want 204 (403: antiforgery) ${CURL_ERR}"; fi
}

# --- Run -----------------------------------------------------------------------------------------------------------
echo "Smoke test (FR-111): $BASE_URL (retrying for up to ${TIMEOUT}s)"
retry "Web liveness" check_healthz || true
retry "API readiness (via web)" check_ready || true
retry "Login page + FR-99 headers" check_login_page || true
retry "Anonymous /api/auth/me" check_me_anonymous || true
if [[ -n "${API_PUBLIC_URL:-}" ]]; then
  retry "API has no public URL" check_api_not_public || true
else
  skip "API has no public URL" "set API_PUBLIC_URL to probe a URL; the deploy workflow checks api has no domain or TCP proxy"
fi

if [[ -n "${SMOKE_EMAIL:-}" && -n "${SMOKE_PASSWORD:-}" ]]; then
  command -v node >/dev/null 2>&1 || { failed "Sign-in round trip" "node (24+) is required"; }
  if command -v node >/dev/null 2>&1; then
    if retry "Antiforgery cookie" check_antiforgery && retry "Sign in (smoke account)" check_login; then
      retry "Projects list" check_projects || true
      retry "SignalR WebSocket" check_hub || true
      retry "Sign out" check_logout || true
    fi
  fi
else
  skip "Sign-in round trip" "SMOKE_EMAIL and SMOKE_PASSWORD not set"
fi

if [[ ${#FAILED[@]} -gt 0 ]]; then
  {
    echo "Smoke test FAILED ($BASE_URL): ${FAILED[*]}"
    echo "Look at the Railway deploy and runtime logs for web and api (railway logs --service <name>)."
    echo "Rollback: deploy/railway/README.md#rollback (redeploy the previous deployment in Railway)."
  } >&2
  exit 1
fi
echo "Smoke test passed: $BASE_URL"
