/**
 * FR-221: search-engine rules. App routes (signed-in pages) are not indexed: server.ts adds `X-Robots-Tag: noindex` to
 * them and robots.txt disallows them; marketing and auth routes stay indexable. robots.txt and sitemap.xml are built
 * from the request's own host (the public origin is not a build-time constant), validated like the root layout's
 * `metadataBase`. Pure; run by Node's type stripping too (erasable TypeScript only, no imports).
 */

/** The first path segment of every app route (src/app/(app)). */
export const APP_ROUTE_PREFIXES = [
  '/home',
  '/projects',
  '/account',
  '/connections',
  '/notifications',
  '/approvals',
  '/admin',
] as const

/** Auth routes: indexable, listed in robots.txt `Allow`, not in the sitemap. */
export const AUTH_ROUTES = ['/login', '/register'] as const

/** Whether a request URL (path, optional query) is an app route. */
export function isAppRoute(url: string): boolean {
  const pathname = url.split(/[?#]/, 1)[0] ?? ''
  return APP_ROUTE_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))
}

/** FR-221: `X-Robots-Tag: noindex` for app routes, nothing for marketing, auth and other pages. */
export function robotsHeaders(url: string): Record<string, string> {
  return isAppRoute(url) ? { 'X-Robots-Tag': 'noindex' } : {}
}

/**
 * The public origin from the request's Host (a plain host name, optional port) and `X-Forwarded-Proto`, or
 * `http://localhost` when the Host is missing or malformed (never echo an arbitrary header into the output).
 */
export function requestOrigin(host: string | null, forwardedProto: string | null): string {
  const proto = forwardedProto === 'https' ? 'https' : 'http'
  return host && /^[a-z0-9.-]+(:\d{1,5})?$/i.test(host) ? `${proto}://${host.toLowerCase()}` : 'http://localhost'
}

/** robots.txt: allow marketing and auth routes, disallow app routes and the API, point at the sitemap. */
export function robotsTxt(origin: string, allow: readonly string[]): string {
  return [
    'User-agent: *',
    ...allow.map((path) => `Allow: ${path}`),
    ...APP_ROUTE_PREFIXES.map((path) => `Disallow: ${path}`),
    'Disallow: /api/',
    '',
    `Sitemap: ${origin}/sitemap.xml`,
    '',
  ].join('\n')
}

/** sitemap.xml (sitemaps.org 0.9) listing `paths` as absolute URLs on `origin`. */
export function sitemapXml(origin: string, paths: readonly string[]): string {
  const urls = paths.map((path) => `  <url><loc>${origin}${path}</loc></url>`)
  return [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">',
    ...urls,
    '</urlset>',
    '',
  ].join('\n')
}
