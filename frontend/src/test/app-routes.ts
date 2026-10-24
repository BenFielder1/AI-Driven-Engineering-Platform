/**
 * The src/app route table for tests, derived from the file system like Next.js does: route groups `(x)` add a layout
 * level but no URL segment, `[id]` is dynamic, `[...x]` / `[[...x]]` catch-all. Used by the next/navigation mock
 * (`useParams`) and by `renderRoute` (src/test/render.tsx). Pure: the lazy glob only lists files.
 */

/** Every page file under src/app, keyed like `../app/(app)/projects/[projectId]/page.tsx`. */
export const pageFiles: string[] = Object.keys(import.meta.glob('../app/**/page.tsx'))

type SegmentKind = 'static' | 'dynamic' | 'catchall' | 'optional-catchall'

interface RouteSegment {
  kind: SegmentKind
  value: string
}

export interface AppRoute {
  /** The page file key. */
  file: string
  /** Directories from src/app to the page, e.g. ['', '(app)', '(app)/projects', '(app)/projects/[projectId]']. */
  dirs: string[]
  segments: RouteSegment[]
  /** URL pattern, e.g. `/projects/[projectId]`. */
  pattern: string
}

export interface RouteMatch {
  route: AppRoute
  params: Record<string, string>
}

const PREFIX = '../app/'

function parseSegment(part: string): RouteSegment | null {
  if (/^\(.+\)$/.test(part)) {
    return null
  }
  const optional = /^\[\[\.\.\.(.+)\]\]$/.exec(part)
  if (optional?.[1]) {
    return { kind: 'optional-catchall', value: optional[1] }
  }
  const catchall = /^\[\.\.\.(.+)\]$/.exec(part)
  if (catchall?.[1]) {
    return { kind: 'catchall', value: catchall[1] }
  }
  const dynamic = /^\[(.+)\]$/.exec(part)
  if (dynamic?.[1]) {
    return { kind: 'dynamic', value: dynamic[1] }
  }
  return { kind: 'static', value: part }
}

/** Builds a route from a page file key (`../app/<dirs>/page.tsx`). */
export function routeFromFile(file: string): AppRoute {
  const inner = file.slice(PREFIX.length).replace(/(^|\/)page\.tsx$/, '')
  const parts = inner === '' ? [] : inner.split('/')
  const dirs = ['']
  const segments: RouteSegment[] = []
  parts.forEach((part, i) => {
    dirs.push(parts.slice(0, i + 1).join('/'))
    const segment = parseSegment(part)
    if (segment) {
      segments.push(segment)
    }
  })
  const pattern = `/${segments.map((s) => (s.kind === 'static' ? s.value : `[${s.value}]`)).join('/')}`
  return { file, dirs, segments, pattern }
}

const rank: Record<SegmentKind, number> = { static: 0, dynamic: 1, catchall: 2, 'optional-catchall': 3 }

/** Matches a pathname against one route; params are URI-decoded. */
export function matchRoute(route: AppRoute, pathname: string): Record<string, string> | null {
  const parts = pathname.split('/').filter(Boolean).map((p) => {
    try {
      return decodeURIComponent(p)
    } catch {
      return p
    }
  })
  const params: Record<string, string> = {}
  for (let i = 0; i < route.segments.length; i++) {
    const segment = route.segments[i]
    if (!segment) {
      break
    }
    if (segment.kind === 'catchall' || segment.kind === 'optional-catchall') {
      const rest = parts.slice(i)
      if (rest.length === 0 && segment.kind === 'catchall') {
        return null
      }
      params[segment.value] = rest.join('/')
      return params
    }
    const part = parts[i]
    if (part === undefined) {
      return null
    }
    if (segment.kind === 'static' && segment.value !== part) {
      return null
    }
    if (segment.kind === 'dynamic') {
      params[segment.value] = part
    }
  }
  return parts.length === route.segments.length ? params : null
}

function compareSpecificity(a: AppRoute, b: AppRoute): number {
  const length = Math.max(a.segments.length, b.segments.length)
  for (let i = 0; i < length; i++) {
    const ra = a.segments[i] ? rank[a.segments[i]?.kind ?? 'static'] : 4
    const rb = b.segments[i] ? rank[b.segments[i]?.kind ?? 'static'] : 4
    if (ra !== rb) {
      return ra - rb
    }
  }
  return 0
}

/** The most specific page for `pathname` (static beats dynamic beats catch-all), or null. */
export function matchAppRoute(pathname: string, files: readonly string[] = pageFiles): RouteMatch | null {
  const routes = files.map(routeFromFile).sort(compareSpecificity)
  for (const route of routes) {
    const params = matchRoute(route, pathname)
    if (params) {
      return { route, params }
    }
  }
  return null
}
