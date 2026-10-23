/**
 * Test render helpers (Vitest + Testing Library + MSW).
 *
 * - `renderWithProviders(ui, { url })`: a component with a fresh retry-free QueryClient, a fake live hub, the toast
 *   region and the in-memory router at `url` (default `/`), so `Link`/`useNavigate`/`useSearch` from
 *   src/lib/navigation work.
 * - `await renderRoute(path)`: the whole app at `path`: the src/app tree (group layouts with their guards, nested layouts,
 *   loading/error/not-found files and the page), resolved from the file system like Next.js. Returns `router` (the
 *   in-memory router: `router.state.location.pathname/search/href`, `router.history.back()`, `router.calls`),
 *   `queryClient` and `live`, like the v2 TanStack helper. The root layout (html/body, fonts, nonce) is not rendered.
 * - `currentHref(router)`: path + search + hash, e.g. `/login?returnUrl=%2Faccount`.
 *
 * `next/navigation` and `next/link` are mocked for every test in src/test/setup.ts; the router resets after each test.
 */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, type RenderOptions } from '@testing-library/react'
import {
  Component,
  createElement,
  Fragment,
  startTransition,
  Suspense,
  use,
  useEffect,
  useState,
  type ComponentType,
  type ReactElement,
  type ReactNode,
} from 'react'

import { Toaster } from '../components/ui/sonner'
import { LiveConnectionContext } from '../features/live/live-connection'
import { NavigationEvents } from '../lib/navigation'
import { matchAppRoute, pageFiles } from './app-routes'
import { FakeLiveHub } from './fake-live'
import { router, type MemoryRouter } from './memory-router'
import { NotFoundSignal, RedirectSignal, RouterHrefContext } from './next-mocks'

export function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } },
  })
}

function TestProviders({ queryClient, live, children }: { queryClient: QueryClient; live: FakeLiveHub; children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <LiveConnectionContext value={live.factory}>
        <NavigationEvents />
        {children}
        <Toaster />
      </LiveConnectionContext>
    </QueryClientProvider>
  )
}

/**
 * Renders with a fresh, retry-free QueryClient per test, a fake live hub (no real SignalR connection), the toast
 * region (async results are toasts, SPEC 7.1) and the in-memory router at `url`.
 */
export function renderWithProviders(ui: ReactElement, options?: RenderOptions & { url?: string }) {
  const { url, ...renderOptions } = options ?? {}
  if (url !== undefined) {
    router.reset(url)
  }
  const queryClient = createTestQueryClient()
  const live = new FakeLiveHub()
  return {
    queryClient,
    live,
    router,
    ...render(
      <TestProviders queryClient={queryClient} live={live}>
        {ui}
      </TestProviders>,
      renderOptions,
    ),
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// The src/app tree
// ---------------------------------------------------------------------------------------------------------------------

type Params = Record<string, string>
interface PageProps {
  params: Promise<Params>
  searchParams: Promise<Params>
}
interface LayoutProps {
  children: ReactNode
  params: Promise<Params>
}
interface ErrorProps {
  error: unknown
  reset: () => void
  retry: () => void
}
interface FileModule {
  default: ComponentType<never>
}
type Loader = () => Promise<FileModule>

// Route files are imported by `renderRoute` before it renders: its own route first, then every other route file, once
// per test file. So neither the first render nor a later client navigation (link, redirect) waits on a module import,
// which under parallel load could take seconds and eat into the findBy timeout. Files load on demand only outside that.
const loaders: Record<string, Loader> = {
  ...import.meta.glob<FileModule>('../app/**/page.tsx'),
  // The root layout renders <html>/<body> and reads request headers: tests render the providers it wraps instead.
  ...import.meta.glob<FileModule>(['../app/**/layout.tsx', '!../app/layout.tsx']),
  ...import.meta.glob<FileModule>('../app/**/loading.tsx'),
  ...import.meta.glob<FileModule>('../app/**/error.tsx'),
  ...import.meta.glob<FileModule>('../app/**/not-found.tsx'),
}
const loaded = new Map<string, FileModule>()
const loading = new Map<string, Promise<FileModule>>()

function loadFile(file: string): Promise<FileModule> {
  let promise = loading.get(file)
  if (!promise) {
    const load = loaders[file]
    if (!load) {
      return Promise.reject(new Error(`No route file ${file}`))
    }
    promise = load().then((fileModule) => {
      loaded.set(file, fileModule)
      return fileModule
    })
    loading.set(file, promise)
  }
  return promise
}

const hasFile = (file: string) => file in loaders

/** Renders a route file's default export: synchronously once loaded, otherwise suspends (`use`) until it is. */
function RouteFile({ file, ...props }: { file: string } & Record<string, unknown>) {
  const fileModule = loaded.get(file) ?? use(loadFile(file))
  return createElement(fileModule.default as ComponentType<object>, props)
}

function fileFor(dir: string, name: string): string {
  return dir === '' ? `../app/${name}.tsx` : `../app/${dir}/${name}.tsx`
}

const ROUTE_FILE_NAMES = ['layout', 'loading', 'error', 'not-found'] as const

/** Every src/app file the route for `href` renders (page, and each level's layout/loading/error/not-found). */
function routeFiles(href: string): string[] {
  const match = matchAppRoute(new URL(href, 'http://localhost').pathname, pageFiles)
  const files = [fileFor('', 'not-found')]
  if (match) {
    files.push(match.route.file)
    for (const dir of match.route.dirs) {
      files.push(...ROUTE_FILE_NAMES.map((name) => fileFor(dir, name)))
    }
  }
  return files.filter((file) => file in loaders)
}

/** Loads the route files for `href`; null when they are all loaded already. */
function preloadRoute(href: string): Promise<unknown> | null {
  const missing = routeFiles(href).filter((file) => !loaded.has(file))
  return missing.length > 0 ? Promise.all(missing.map(loadFile)) : null
}

interface BoundaryProps {
  resetKey: string
  catches: (error: unknown) => boolean
  fallback: (error: unknown, reset: () => void) => ReactNode
  children: ReactNode
}

/** Error boundary that only handles what `catches` accepts (rethrows the rest) and resets when `resetKey` changes. */
class Boundary extends Component<BoundaryProps, { failed: boolean; error: unknown }> {
  override state = { failed: false, error: undefined as unknown }

  static getDerivedStateFromError(error: unknown) {
    return { failed: true, error }
  }

  override componentDidUpdate(previous: BoundaryProps) {
    if (this.state.failed && previous.resetKey !== this.props.resetKey) {
      this.reset()
    }
  }

  reset = () => {
    this.setState({ failed: false, error: undefined })
  }

  override render() {
    if (this.state.failed) {
      if (!this.props.catches(this.state.error)) {
        throw this.state.error
      }
      return this.props.fallback(this.state.error, this.reset)
    }
    return this.props.children
  }
}

function FollowRedirect({ signal }: { signal: RedirectSignal }) {
  useEffect(() => {
    if (signal.type === 'push') {
      router.push(signal.url)
    } else {
      router.replace(signal.url)
    }
  }, [signal])
  return null
}

const isRedirect = (e: unknown): e is RedirectSignal => e instanceof RedirectSignal
const isNotFound = (e: unknown) => e instanceof NotFoundSignal
const isRouteError = (e: unknown) => !isRedirect(e) && !isNotFound(e)

const promises = new Map<string, Promise<Params>>()
/** Stable resolved promises per value, so a page calling `use(params)` doesn't suspend on every render. */
function resolved(value: Params): Promise<Params> {
  const key = JSON.stringify(value)
  let promise = promises.get(key)
  if (!promise) {
    promise = Promise.resolve(value)
    promises.set(key, promise)
  }
  return promise
}

/** Substitutes params into a directory path, so a layout remounts when its dynamic segment changes (as in Next). */
function segmentKey(dir: string, params: Params): string {
  return dir.replace(/\[(?:\.\.\.)?([^\]]+)\]/g, (_, name: string) => `[${params[name] ?? ''}]`)
}

/**
 * The App Router's committed state: the href the tree renders. Like Next, a navigation is a transition: the next
 * route's files load first and the previous page stays on screen (and `usePathname`/`useSearchParams`/`useParams`
 * keep reporting the old URL) until the new one can render. The in-memory router itself updates synchronously.
 */
function RouterState({ children }: { children: ReactNode }) {
  const [href, setHref] = useState(() => router.href)
  useEffect(() => {
    let disposed = false
    const commit = () => {
      if (!disposed) {
        startTransition(() => {
          setHref(router.href)
        })
      }
    }
    const unsubscribe = router.subscribe(() => {
      const pending = preloadRoute(router.href)
      if (pending) {
        void pending.then(commit, commit)
      } else {
        commit()
      }
    })
    // A navigation between the first render and this effect (e.g. a redirect in a child effect) isn't missed.
    commit()
    return () => {
      disposed = true
      unsubscribe()
    }
  }, [])
  return <RouterHrefContext value={href}>{children}</RouterHrefContext>
}

function AppTree() {
  const href = use(RouterHrefContext) ?? router.href
  const url = new URL(href, 'http://localhost')
  const match = matchAppRoute(url.pathname, pageFiles)
  let element: ReactNode
  if (!match) {
    const rootNotFound = fileFor('', 'not-found')
    element = hasFile(rootNotFound) ? <RouteFile file={rootNotFound} /> : <p>Not found</p>
  } else {
    const params = resolved(match.params)
    const searchParams = resolved(Object.fromEntries(url.searchParams))
    const pageProps: PageProps = { params, searchParams }
    element = <RouteFile file={match.route.file} {...pageProps} />
    const dirs = match.route.dirs
    for (let i = dirs.length - 1; i >= 0; i--) {
      const dir = dirs[i] ?? ''
      const notFound = fileFor(dir, 'not-found')
      if (hasFile(notFound)) {
        element = (
          <Boundary resetKey={url.pathname} catches={isNotFound} fallback={() => <RouteFile file={notFound} />}>
            {element}
          </Boundary>
        )
      }
      const loadingFile = fileFor(dir, 'loading')
      if (hasFile(loadingFile)) {
        element = <Suspense fallback={<RouteFile file={loadingFile} />}>{element}</Suspense>
      }
      const errorFile = fileFor(dir, 'error')
      if (hasFile(errorFile)) {
        const errorProps = (error: unknown, reset: () => void): ErrorProps => ({ error, reset, retry: reset })
        element = (
          <Boundary resetKey={url.pathname} catches={isRouteError} fallback={(error, reset) => <RouteFile file={errorFile} {...errorProps(error, reset)} />}>
            {element}
          </Boundary>
        )
      }
      const layoutFile = fileFor(dir, 'layout')
      if (hasFile(layoutFile)) {
        const layoutProps: LayoutProps = { params, children: element }
        element = <RouteFile file={layoutFile} {...layoutProps} />
      }
      element = <Fragment key={segmentKey(dir, match.params)}>{element}</Fragment>
    }
  }
  return (
    <Suspense fallback={null}>
      <Boundary resetKey={href} catches={isRedirect} fallback={(signal) => <FollowRedirect signal={signal as RedirectSignal} />}>
        {element}
      </Boundary>
    </Suspense>
  )
}

/**
 * Renders the whole app (src/app layouts, guards, shell and page) at `path`, with the in-memory router and a fake live
 * hub. Async: the route's files are loaded before the first render, so await it before querying the screen.
 */
export async function renderRoute(path: string) {
  router.reset(path)
  await preloadRoute(router.href)
  await Promise.all(Object.keys(loaders).map(loadFile))
  const queryClient = createTestQueryClient()
  const live = new FakeLiveHub()
  const utils = render(
    <RouterState>
      <TestProviders queryClient={queryClient} live={live}>
        <AppTree />
      </TestProviders>
    </RouterState>,
  )
  return { router, queryClient, live, ...utils }
}

/** Current path + search (+ hash) of the test router, e.g. `/login?returnUrl=%2Faccount`. */
export function currentHref(r: MemoryRouter = router): string {
  return r.href
}
