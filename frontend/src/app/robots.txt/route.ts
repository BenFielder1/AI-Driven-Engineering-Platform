import { marketingPathList } from '../../features/marketing/content/pages'
import { AUTH_ROUTES, requestOrigin, robotsTxt } from '../../server/seo'

/** Built per request from the Host header. */
export const dynamic = 'force-dynamic'

/** FR-221: GET /robots.txt: marketing and auth routes allowed, app routes and /api disallowed, sitemap on this host. */
export function GET(request: Request): Response {
  const origin = requestOrigin(request.headers.get('host'), request.headers.get('x-forwarded-proto'))
  return new Response(robotsTxt(origin, [...marketingPathList, ...AUTH_ROUTES]), {
    headers: { 'Content-Type': 'text/plain; charset=utf-8' },
  })
}
