/**
 * Navigation adapter (FR-94): the only place that imports `next/navigation` and `next/link` for app code. Feature code
 * imports routing helpers from here, so the TanStack Router → Next.js port is mostly an import swap.
 *
 * Porting recipe (TanStack → adapter):
 * - `import { Link } from '@tanstack/react-router'` → `import { Link } from '<rel>/lib/navigation'`. Same props: `to`
 *   (a pattern such as `/projects/$projectId/runs`, or a plain path), `params`, `search` (object, updater or `true`),
 *   `hash`, `replace`, `activeOptions` ({ exact, includeSearch, includeHash }), `activeProps` / `inactiveProps`. Active links get
 *   `aria-current="page"` and `data-status="active"` like TanStack (an explicit `aria-current` prop wins).
 * - `useNavigate()` → `useNavigate()`: `navigate({ to?, href?, params?, search?, hash?, replace?, scroll? })`. Omitting
 *   `to` (or `to: '.'`) keeps the current path. Returns a promise that settles once the URL has changed.
 * - `useRouter()` (TanStack) → `useAppRouter()`: `router.navigate(...)`, `router.history.push(href)` / `.replace` /
 *   `.back()` / `.forward()`, and `router.state.location` ({ pathname, search, searchStr, href }).
 * - `useRouterState({ select: (s) => s.location.pathname })` → `usePathname()`; `...location.href` →
 *   `useLocation().href`; `useLocation()` → `useLocation()`.
 * - `Route.useParams()` → `useParams<{ projectId: string }>()`.
 * - `validateSearch` + `Route.useSearch()` → `useSearch(validate)`, where `validate` is the same function
 *   `(raw: Record<string, unknown>) => T` or a Zod schema (`.parse`). Raw values are strings (TanStack parsed JSON);
 *   the v2 validators already accept strings.
 * - `Route.useNavigate()` + `navigate({ search, replace })` → `useSetSearch()` → `setSearch(next, { replace })`.
 * - `useSearch({ strict: false })` → `useSearch()` (raw strings).
 * - `useMatchRoute()({ to, params, fuzzy })` → `useMatchRoute()` (same call shape).
 * - `<Navigate to params search replace />` → `<Navigate ... />` (same props). `throw redirect(...)` in `beforeLoad`
 *   → render `<Navigate replace ... />` from the page, or call `redirect()` from `next/navigation` in a server page.
 * - `retainSearchParams(['release'])` on `/projects/$projectId` is built in: see `retainedSearchParams` below.
 * - Route files: `src/routes/_app/x/$id.tsx` → `src/app/(app)/x/[id]/page.tsx` with `'use client'` and a default
 *   export that reads params/search through these hooks. Layout routes → `layout.tsx` ({ children }).
 * Tests: `await renderRoute(path)` in src/test/render.tsx renders the src/app tree under an in-memory router.
 */
import NextLink from 'next/link'
import {
  useParams as useNextParams,
  usePathname as useNextPathname,
  useRouter as useNextRouter,
  useSearchParams as useNextSearchParams,
} from 'next/navigation'
import {
  createElement,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useSyncExternalStore,
  type ComponentProps,
  type MouseEvent,
  type ReactElement,
} from 'react'

/** Raw search params: last value wins, as strings. */
export type RawSearch = Record<string, string>
/** A search object to write (typed route searches are interfaces, hence `object`). undefined/null values are dropped. */
export type SearchInput = object
export type SearchOption = SearchInput | ((prev: RawSearch) => SearchInput) | true
export type RouteParams = Readonly<Record<string, string | number>>

export interface NavigateOptions {
  /** Pattern (`/projects/$projectId`, `/projects/[projectId]`) or path. Omitted or `'.'`: the current path. */
  to?: string
  /** A concrete path + search + hash (TanStack `navigate({ href })`). Takes precedence over `to`/`search`/`hash`. */
  href?: string
  params?: RouteParams
  /** `true` keeps the current search; omitted clears it (TanStack semantics, except retained params). */
  search?: SearchOption
  hash?: string
  replace?: boolean
  /** Scroll to top after navigating (default true, like TanStack's resetScroll). */
  scroll?: boolean
}

export interface AppLocation {
  pathname: string
  search: RawSearch
  /** `?a=b` or ''. */
  searchStr: string
  /** pathname + searchStr. */
  href: string
}

/**
 * Search params kept across navigation to matching paths unless the destination sets the key itself (even to
 * `undefined`). v2: `retainSearchParams(['release'])` on /projects/$projectId (FR-62).
 */
export const retainedSearchParams: readonly { path: RegExp; keys: readonly string[] }[] = [
  { path: /^\/projects\/(?!new(?:\/|$))[^/]+(?:\/|$)/, keys: ['release'] },
]

// ---------------------------------------------------------------------------------------------------------------------
// Pure helpers (unit-tested)
// ---------------------------------------------------------------------------------------------------------------------

function encodeValue(value: unknown): string {
  if (typeof value === 'string') {
    return value
  }
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') {
    return String(value)
  }
  return JSON.stringify(value)
}

/** `{ a: 'x y', b: undefined, c: 2 }` → `?a=x%20y&c=2` ('' when empty). Keys keep insertion order. */
export function stringifySearch(search: SearchInput): string {
  const parts: string[] = []
  for (const [key, value] of Object.entries(search)) {
    if (value === undefined || value === null) {
      continue
    }
    const values: unknown[] = Array.isArray(value) ? value : [value]
    for (const v of values) {
      if (v !== undefined && v !== null) {
        parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(encodeValue(v))}`)
      }
    }
  }
  return parts.length > 0 ? `?${parts.join('&')}` : ''
}

/** `?a=1&b=x` (or a URLSearchParams) → `{ a: '1', b: 'x' }`. */
export function parseSearch(search: string | URLSearchParams): RawSearch {
  const params = typeof search === 'string' ? new URLSearchParams(search.startsWith('?') ? search.slice(1) : search) : search
  const result: RawSearch = {}
  params.forEach((value, key) => {
    result[key] = value
  })
  return result
}

/** Fills `$name` / `[name]` segments from `params` (URI-encoded). Throws when a param is missing. */
export function interpolatePath(pattern: string, params: RouteParams = {}): string {
  return pattern.replace(/\$([A-Za-z0-9_]+)|\[([A-Za-z0-9_]+)\]/g, (_, dollar: string | undefined, bracket: string | undefined) => {
    const name = dollar ?? bracket ?? ''
    const value = params[name]
    if (value === undefined || value === '') {
      throw new Error(`Missing route param "${name}" for ${pattern}`)
    }
    return encodeURIComponent(String(value))
  })
}

function splitHref(href: string): { pathname: string; search: string; hash: string } {
  const hashAt = href.indexOf('#')
  const hash = hashAt >= 0 ? href.slice(hashAt) : ''
  const rest = hashAt >= 0 ? href.slice(0, hashAt) : href
  const queryAt = rest.indexOf('?')
  return {
    pathname: queryAt >= 0 ? rest.slice(0, queryAt) : rest,
    search: queryAt >= 0 ? rest.slice(queryAt) : '',
    hash,
  }
}

function withRetained(pathname: string, next: Record<string, unknown>, current: RawSearch): Record<string, unknown> {
  let result = next
  for (const rule of retainedSearchParams) {
    if (!rule.path.test(pathname)) {
      continue
    }
    for (const key of rule.keys) {
      if (!(key in result) && current[key] !== undefined) {
        result = { ...result, [key]: current[key] }
      }
    }
  }
  return result
}

function isSearchUpdater(search: SearchOption | undefined): search is (prev: RawSearch) => SearchInput {
  return typeof search === 'function'
}

/** Resolves navigation options against the current location into a concrete `path?search#hash`. */
export function buildHref(options: NavigateOptions, current: Pick<AppLocation, 'pathname' | 'search'>): string {
  if (options.href !== undefined) {
    const parts = splitHref(options.href)
    const search = withRetained(parts.pathname, parseSearch(parts.search), current.search)
    return `${parts.pathname}${stringifySearch(search)}${parts.hash}`
  }
  const to = options.to === undefined || options.to === '.' ? current.pathname : options.to
  if (!to.startsWith('/')) {
    throw new Error(`Relative navigation targets are not supported: "${to}"`)
  }
  const pathname = interpolatePath(to, options.params)
  let search: Record<string, unknown>
  if (options.search === true) {
    search = { ...current.search }
  } else if (isSearchUpdater(options.search)) {
    search = { ...options.search({ ...current.search }) }
  } else {
    search = options.search ? { ...options.search } : {}
  }
  search = withRetained(pathname, search, current.search)
  const hash = options.hash ? `#${options.hash.replace(/^#/, '')}` : ''
  return `${pathname}${stringifySearch(search)}${hash}`
}

function trimSlash(path: string): string {
  return path.length > 1 ? path.replace(/\/+$/, '') : path
}

/**
 * Matches `pathname` against a pattern (`$name` or `[name]` segments). Exact unless `fuzzy` (then the pattern may be
 * a prefix at a segment boundary). Returns the decoded params, or null.
 */
export function matchPath(pattern: string, pathname: string, options: { fuzzy?: boolean } = {}): Record<string, string> | null {
  const want = trimSlash(pattern).split('/').filter(Boolean)
  const have = trimSlash(pathname).split('/').filter(Boolean)
  if (have.length < want.length || (!options.fuzzy && have.length !== want.length)) {
    return null
  }
  const params: Record<string, string> = {}
  for (let i = 0; i < want.length; i++) {
    const segment = want[i] ?? ''
    const actual = have[i] ?? ''
    const name = /^\$(.+)$/.exec(segment)?.[1] ?? /^\[(.+)\]$/.exec(segment)?.[1]
    if (name) {
      params[name] = safeDecode(actual)
    } else if (segment !== actual) {
      return null
    }
  }
  return params
}

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value)
  } catch {
    return value
  }
}

export interface ActiveOptions {
  /** Pathname must equal (default: the link's path may be a prefix of the current one). */
  exact?: boolean
  /** The link's search params must be present in the current URL (default true, like TanStack). */
  includeSearch?: boolean
  /** The current hash must equal the link's hash (both may be empty). Default false, like TanStack. */
  includeHash?: boolean
}

/**
 * TanStack's Link active rule: path prefix (or exact), plus the link's search as a subset of the current search, plus
 * (with `includeHash`) the same hash. `current.hash` is with or without the leading `#`.
 */
export function isActiveHref(
  current: Pick<AppLocation, 'pathname' | 'search'> & { hash?: string },
  href: string,
  options: ActiveOptions = {},
): boolean {
  const target = splitHref(href)
  const here = trimSlash(current.pathname)
  const there = trimSlash(target.pathname)
  const pathOk = options.exact ? here === there : there === '/' ? here === '/' : here === there || here.startsWith(`${there}/`)
  if (!pathOk) {
    return false
  }
  if (options.includeHash && (current.hash ?? '').replace(/^#/, '') !== target.hash.replace(/^#/, '')) {
    return false
  }
  if (options.includeSearch === false) {
    return true
  }
  const wanted = parseSearch(target.search)
  return Object.entries(wanted).every(([key, value]) => current.search[key] === value)
}

// ---------------------------------------------------------------------------------------------------------------------
// Settling navigations: navigate() resolves once the new URL has rendered (NavigationEvents, mounted in Providers).
// ---------------------------------------------------------------------------------------------------------------------

const pending = new Set<() => void>()
const SETTLE_TIMEOUT_MS = 5000

function waitForNavigation(): Promise<void> {
  return new Promise((resolve) => {
    const done = () => {
      pending.delete(done)
      clearTimeout(timer)
      resolve()
    }
    const timer = setTimeout(done, SETTLE_TIMEOUT_MS)
    pending.add(done)
  })
}

/** Resolves pending `navigate()` promises when the location changes. Render once near the root (Providers). */
export function NavigationEvents(): null {
  const { href } = useLocation()
  useEffect(() => {
    for (const done of [...pending]) {
      done()
    }
  }, [href])
  return null
}

// ---------------------------------------------------------------------------------------------------------------------
// The URL hash. Next's hooks don't expose it, and a hash-only navigation (`router.push('#x')`, a `#x` link) updates
// history without a `hashchange` event or a pathname/search change, so listeners are also re-checked after our own
// navigations (Next writes history when the navigation commits, hence the short delays).
// ---------------------------------------------------------------------------------------------------------------------

const hashListeners = new Set<() => void>()
const HASH_RECHECK_DELAYS_MS = [0, 50, 250]

function readHash(): string {
  return typeof window === 'undefined' ? '' : window.location.hash.replace(/^#/, '')
}

function subscribeHash(listener: () => void): () => void {
  hashListeners.add(listener)
  window.addEventListener('hashchange', listener)
  window.addEventListener('popstate', listener)
  return () => {
    hashListeners.delete(listener)
    window.removeEventListener('hashchange', listener)
    window.removeEventListener('popstate', listener)
  }
}

function recheckHashSoon(): void {
  if (typeof window === 'undefined' || hashListeners.size === 0) {
    return
  }
  for (const delay of HASH_RECHECK_DELAYS_MS) {
    setTimeout(() => {
      for (const listener of [...hashListeners]) {
        listener()
      }
    }, delay)
  }
}

/** The current hash without `#` ('' on the server and during hydration). */
export function useHash(): string {
  return useSyncExternalStore(subscribeHash, readHash, () => '')
}

// ---------------------------------------------------------------------------------------------------------------------
// Hooks
// ---------------------------------------------------------------------------------------------------------------------

/** The current pathname. */
export function usePathname(): string {
  return useNextPathname()
}

/** The current location (pathname, raw search, href). */
export function useLocation(): AppLocation {
  const pathname = useNextPathname()
  const searchParams = useNextSearchParams()
  const searchStr = searchParams.size > 0 ? `?${searchParams.toString()}` : ''
  return useMemo(
    () => ({ pathname, search: parseSearch(searchStr), searchStr, href: `${pathname}${searchStr}` }),
    [pathname, searchStr],
  )
}

/** Dynamic route params of the current page, e.g. `useParams<{ projectId: string }>()`. */
// eslint-disable-next-line @typescript-eslint/no-unnecessary-type-parameters -- TanStack-style typed params for callers
export function useParams<T extends Record<string, string> = Record<string, string>>(): T {
  const params = useNextParams()
  return useMemo(() => {
    const result: Record<string, string> = {}
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined) {
        result[key] = Array.isArray(value) ? value.join('/') : value
      }
    }
    return result as T
  }, [params])
}

export type SearchValidator<T> = ((raw: Record<string, unknown>) => T) | { parse: (raw: unknown) => T }

/** Search params parsed by a route's validator (function or Zod schema); without one, the raw strings. */
export function useSearch(): RawSearch
export function useSearch<T>(validate: SearchValidator<T>): T
export function useSearch<T>(validate?: SearchValidator<T>): T | RawSearch {
  const { searchStr } = useLocation()
  // Parsed once per URL (validators are often inline functions): a stable object until the search changes.
  return useMemo(
    () => {
      const raw = parseSearch(searchStr)
      if (!validate) {
        return raw
      }
      return typeof validate === 'function' ? validate(raw) : validate.parse(raw)
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps -- see above
    [searchStr],
  )
}

export type Navigate = (options: NavigateOptions) => Promise<void>

/** TanStack-style `navigate(options)`; stable across renders. */
export function useNavigate(): Navigate {
  const router = useNextRouter()
  const location = useLocation()
  const locationRef = useRef(location)
  useEffect(() => {
    locationRef.current = location
  }, [location])
  return useCallback(
    (options: NavigateOptions) => {
      const href = buildHref(options, locationRef.current)
      if (href === locationRef.current.href) {
        return Promise.resolve()
      }
      const settled = waitForNavigation()
      const scroll = { scroll: options.scroll ?? true }
      if (options.replace) {
        router.replace(href, scroll)
      } else {
        router.push(href, scroll)
      }
      recheckHashSoon()
      return settled
    },
    [router],
  )
}

/** `Route.useNavigate()({ search, replace })` for the current page: writes search params to the URL. */
export function useSetSearch(): (search: SearchOption, options?: { replace?: boolean }) => Promise<void> {
  const navigate = useNavigate()
  return useCallback((search, options) => navigate({ search, replace: options?.replace }), [navigate])
}

/** `useMatchRoute()` → `(opts) => boolean` for the current pathname. */
export function useMatchRoute(): (options: { to: string; params?: RouteParams; fuzzy?: boolean }) => boolean {
  const pathname = useNextPathname()
  return useCallback(
    ({ to, params, fuzzy }) => {
      const matched = matchPath(to, pathname, { fuzzy })
      if (!matched) {
        return false
      }
      return Object.entries(params ?? {}).every(([key, value]) => matched[key] === String(value))
    },
    [pathname],
  )
}

export interface AppRouter {
  navigate: Navigate
  history: { push(href: string): void; replace(href: string): void; back(): void; forward(): void }
  /** The location at render time. */
  state: { location: AppLocation }
}

/** TanStack `useRouter()` facade: `navigate`, `history.push/replace/back/forward`, `state.location`. */
export function useAppRouter(): AppRouter {
  const router = useNextRouter()
  const navigate = useNavigate()
  const location = useLocation()
  return useMemo(
    () => ({
      navigate,
      history: {
        push: (href: string) => {
          void navigate({ href })
        },
        replace: (href: string) => {
          void navigate({ href, replace: true })
        },
        back: () => {
          router.back()
        },
        forward: () => {
          router.forward()
        },
      },
      state: { location },
    }),
    [navigate, router, location],
  )
}

// ---------------------------------------------------------------------------------------------------------------------
// Components
// ---------------------------------------------------------------------------------------------------------------------

type AnchorProps = Omit<ComponentProps<'a'>, 'href'>
type StateProps = Omit<AnchorProps, 'children' | 'ref'> & Record<`data-${string}`, string | undefined>

export interface LinkProps extends AnchorProps {
  to: string
  params?: RouteParams
  search?: SearchOption
  hash?: string
  replace?: boolean
  scroll?: boolean
  prefetch?: boolean | null
  activeOptions?: ActiveOptions
  activeProps?: StateProps
  inactiveProps?: StateProps
}

/** TanStack-compatible `<Link>` over `next/link` (client navigation, prefetch, modified clicks handled by the browser). */
export function Link({
  to,
  params,
  search,
  hash,
  replace,
  scroll,
  prefetch,
  activeOptions,
  activeProps,
  inactiveProps,
  className,
  onClick,
  ...rest
}: LinkProps): ReactElement {
  const location = useLocation()
  const currentHash = useHash()
  const href = buildHref({ to, params, search, hash }, location)
  const active = isActiveHref({ ...location, hash: currentHash }, href, activeOptions)
  const stateProps = active ? activeProps : inactiveProps
  const classes = [className, stateProps?.className].filter(Boolean).join(' ')
  const props = {
    'aria-current': active ? 'page' : undefined,
    'data-status': active ? 'active' : undefined,
    ...rest,
    ...stateProps,
    className: classes || undefined,
    onClick: (event: MouseEvent<HTMLAnchorElement>) => {
      onClick?.(event)
      recheckHashSoon()
    },
    href,
    replace,
    scroll,
    prefetch,
  } as ComponentProps<typeof NextLink>
  return createElement(NextLink, props)
}

/** Navigates on mount (TanStack `<Navigate>`; use for client-side redirects such as route aliases). */
export function Navigate(props: NavigateOptions): null {
  const navigate = useNavigate()
  const location = useLocation()
  const target = buildHref(props, location)
  const replace = props.replace ?? false
  useEffect(() => {
    void navigate({ href: target, replace })
  }, [navigate, target, replace])
  return null
}
