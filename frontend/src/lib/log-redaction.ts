/**
 * FR-100 / NFR-SEC: one-click approval links carry a bearer token in the path (`/approvals/<token>` page,
 * `/api/approvals/<token>` API). The web server's log lines show that segment as `[redacted]`.
 *
 * Broader than the v2 nginx maps (which only matched at the start of the path, and before the query in a Referer): the
 * token also travels in query strings, e.g. a signed-out visit is sent to `/login?returnUrl=%2Fapprovals%2F<token>`, and
 * the login page's RSC fetches and /api calls then carry that URL in their path or Referer. So every `approvals/<token>`
 * after a `/` separator is redacted, wherever it appears (path, query, fragment), with the separator raw (`/`),
 * percent-encoded (`%2F`, any case) or double-encoded (`%252F`). `/api/approvals/` is covered because `approvals`
 * follows a separator there too. The token (which may contain percent-encoded characters) ends at the next separator,
 * raw (`/ ? # & =`, whitespace, quote) or encoded (`%2F %3F %23 %26`, also as `%25…`).
 *
 * Pure and dependency-free: imported by the custom server (server.ts, run by Node's type stripping) and unit-tested.
 */

const SEPARATOR = String.raw`(?:\/|%2F|%252F)`
/** A token character: anything but a real separator, raw or percent-encoded once or twice (`%2F %3F %23 %26`). */
const TOKEN_CHAR = String.raw`(?:[^/?#&=%\s"'<>]|%(?!(?:25)?(?:2f|3f|23|26)))`
const TOKEN_PATTERN = new RegExp(String.raw`(${SEPARATOR}approvals${SEPARATOR})${TOKEN_CHAR}+`, 'gi')

export const REDACTED = '[redacted]'

/** Redacts every approval token in a string (path, URL, query or free text). */
export function redactApprovalTokens(value: string): string {
  return value.replace(TOKEN_PATTERN, `$1${REDACTED}`)
}

/** Redacts approval tokens in a request path (with optional query/hash). */
export function redactPath(path: string): string {
  return redactApprovalTokens(path)
}

/** Redacts approval tokens in a Referer value (absolute URL or path). */
export function redactReferer(referer: string): string {
  return redactApprovalTokens(referer)
}
