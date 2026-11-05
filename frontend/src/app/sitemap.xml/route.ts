import { marketingPathList } from '../../features/marketing/content/pages'
import { requestOrigin, sitemapXml } from '../../server/seo'

/** Built per request from the Host header. */
export const dynamic = 'force-dynamic'

/** FR-221: GET /sitemap.xml: the marketing routes as absolute URLs on the request's host. */
export function GET(request: Request): Response {
  const origin = requestOrigin(request.headers.get('host'), request.headers.get('x-forwarded-proto'))
  return new Response(sitemapXml(origin, marketingPathList), {
    headers: { 'Content-Type': 'application/xml; charset=utf-8' },
  })
}
