/**
 * NFR observability + FR-100: one JSON line per request from the web server (method, redacted path, status, duration,
 * redacted Referer). Approval tokens never reach the log. Run by Node's type stripping: erasable TypeScript only.
 */
import { redactApprovalTokens, redactPath, redactReferer } from '../lib/log-redaction.ts'

export interface RequestLogInput {
  method: string | undefined
  url: string | undefined
  status: number
  durationMs: number
  referer?: string | undefined
  /** When the client went away before the response finished. */
  aborted?: boolean
}

/** Builds the log line (without a trailing newline). `now` is injectable for tests. */
export function requestLogLine(input: RequestLogInput, now: Date = new Date()): string {
  const entry: Record<string, unknown> = {
    timestamp: now.toISOString(),
    level: input.status >= 500 ? 'error' : 'info',
    msg: 'request',
    method: input.method ?? 'GET',
    path: redactPath(input.url ?? '/'),
    status: input.status,
    durationMs: Math.round(input.durationMs * 10) / 10,
  }
  if (input.referer) {
    entry.referer = redactReferer(input.referer)
  }
  if (input.aborted) {
    entry.aborted = true
  }
  return JSON.stringify(entry)
}

/** Static assets and probes are not logged (noise); every other request is. */
export function shouldLogRequest(url: string | undefined): boolean {
  const path = url ?? '/'
  return !path.startsWith('/_next/static/') && !path.startsWith('/_next/webpack-hmr') && !path.startsWith('/_next/hmr') && path !== '/healthz'
}

/** A one-line JSON error log without request URLs (they may carry approval tokens). */
export function errorLogLine(message: string, error: unknown, now: Date = new Date()): string {
  const detail = error instanceof Error ? `${error.name}: ${error.message}` : String(error)
  return JSON.stringify({ timestamp: now.toISOString(), level: 'error', msg: message, error: redactApprovalTokens(detail) })
}
