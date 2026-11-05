import { describe, expect, it } from 'vitest'

import { GET as getRobots } from '../app/robots.txt/route'
import { GET as getSitemap } from '../app/sitemap.xml/route'
import { marketingPathList } from '../features/marketing/content/pages'
import { appPaths, publicPaths } from '../lib/app-paths'
import { isAppRoute, requestOrigin } from './seo'
import { pageSecurityHeaders } from './security-headers'

function request(path: string, headers: Record<string, string>): Request {
  return new Request(`http://internal${path}`, { headers })
}

describe('FR-221 robots.txt, sitemap.xml and noindex', () => {
  it('FR221_robots_txt: 200, allows marketing and auth routes, disallows app routes and /api, names the sitemap', async () => {
    const response = getRobots(request('/robots.txt', { host: 'tevrin.example', 'x-forwarded-proto': 'https' }))

    expect(response.status).toBe(200)
    expect(response.headers.get('content-type')).toContain('text/plain')
    const lines = (await response.text()).split('\n')
    expect(lines[0]).toBe('User-agent: *')
    for (const path of [...marketingPathList, '/login', '/register']) {
      expect(lines).toContain(`Allow: ${path}`)
    }
    for (const path of ['/home', '/projects', '/account', '/connections', '/notifications', '/approvals', '/admin', '/api/']) {
      expect(lines).toContain(`Disallow: ${path}`)
    }
    expect(lines).toContain('Sitemap: https://tevrin.example/sitemap.xml')
  })

  it('FR221_sitemap_xml: 200, lists every marketing route as an absolute URL on the request host, and nothing else', async () => {
    const response = getSitemap(request('/sitemap.xml', { host: 'localhost:3000' }))

    expect(response.status).toBe(200)
    expect(response.headers.get('content-type')).toContain('application/xml')
    const body = await response.text()
    expect(body).toContain('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">')
    const locs = [...body.matchAll(/<loc>([^<]+)<\/loc>/g)].map((m) => m[1])
    expect(locs).toEqual(marketingPathList.map((p) => `http://localhost:3000${p}`))
  })

  it('FR221_request_host_validated: a malformed Host never reaches the output', () => {
    expect(requestOrigin('evil.example/<script>', 'https')).toBe('http://localhost')
    expect(requestOrigin(null, null)).toBe('http://localhost')
    expect(requestOrigin('Tevrin.Example:8080', 'http')).toBe('http://tevrin.example:8080')
    expect(requestOrigin('tevrin.example', 'ftp')).toBe('http://tevrin.example')
  })

  it('FR221_noindex_app_routes: app routes send X-Robots-Tag: noindex; marketing and auth routes do not', () => {
    const appRoutes = appPaths.filter((p) => !(publicPaths as readonly string[]).includes(p))
    expect(appRoutes.length).toBeGreaterThan(20)
    for (const path of appRoutes) {
      const concrete = path.replace(/\$[A-Za-z]+/g, 'x')
      expect(isAppRoute(concrete), path).toBe(true)
      expect(pageSecurityHeaders('n', false, `${concrete}?tab=1`)['X-Robots-Tag'], path).toBe('noindex')
    }
    for (const path of publicPaths) {
      expect(isAppRoute(path), path).toBe(false)
      expect(pageSecurityHeaders('n', false, path), path).not.toHaveProperty('X-Robots-Tag')
    }
    // Prefix matches are whole segments only.
    expect(isAppRoute('/homepage')).toBe(false)
    expect(isAppRoute('/robots.txt')).toBe(false)
  })
})
