/**
 * FR-99: the v2 nginx security headers, now set by the custom web server (server.ts). Pure, unit-tested.
 * Run by Node's type stripping: erasable TypeScript only, relative imports with `.ts`.
 */
import { randomBytes } from 'node:crypto'

/** A fresh CSP nonce per request (128 bits, base64). */
export function createNonce(): string {
  return randomBytes(16).toString('base64')
}

/**
 * The page CSP (SPEC FR-99). Next.js reads the nonce from the request's CSP header and puts it on every inline script
 * it emits; the root layout reads `x-nonce` for the theme-init script. `'unsafe-eval'` only in `next dev` (React's
 * dev tooling), never in production.
 */
export function contentSecurityPolicy(nonce: string, dev: boolean): string {
  return [
    "default-src 'self'",
    "img-src 'self' data:",
    "connect-src 'self'",
    "style-src 'self' 'unsafe-inline'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'${dev ? " 'unsafe-eval'" : ''}`,
    "object-src 'none'",
    "base-uri 'self'",
    "frame-ancestors 'none'",
  ].join('; ')
}

export const IMMUTABLE_CACHE_CONTROL = 'public, max-age=31536000, immutable'

/**
 * FR-99 caching: content-hashed build assets (`/_next/static/*`) are immutable for a year in production; everything
 * else (HTML, `/healthz`, public files) must be revalidated. Next.js keeps a Cache-Control header that is already set,
 * so the server has to choose. In `next dev` the chunks aren't content-hashed, so they are revalidated too.
 */
export function cacheControlFor(url: string, dev: boolean): string {
  return !dev && url.startsWith('/_next/static/') ? IMMUTABLE_CACHE_CONTROL : 'no-cache'
}

/** Headers on every non-/api response the web server sends (the /api pass-through only gets nosniff). */
export function pageSecurityHeaders(nonce: string, dev: boolean, url = '/'): Record<string, string> {
  return {
    'Content-Security-Policy': contentSecurityPolicy(nonce, dev),
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'strict-origin-when-cross-origin',
    'X-Frame-Options': 'DENY',
    'Cache-Control': cacheControlFor(url, dev),
  }
}
