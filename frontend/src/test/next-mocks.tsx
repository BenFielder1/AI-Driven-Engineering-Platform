/**
 * Test doubles for `next/navigation` and `next/link`, backed by the in-memory router (src/test/memory-router.ts).
 * Registered for every test file in src/test/setup.ts with `vi.mock`.
 */
import { createContext, use, useMemo, useSyncExternalStore, type ComponentProps, type MouseEvent } from 'react'

import { matchAppRoute } from './app-routes'
import { router } from './memory-router'

/** Thrown by the mocked `redirect()`; `renderRoute` catches it and replaces the URL. */
export class RedirectSignal extends Error {
  readonly url: string
  readonly type: 'push' | 'replace'
  constructor(url: string, type: 'push' | 'replace' = 'replace') {
    super(`NEXT_REDIRECT ${url}`)
    this.url = url
    this.type = type
  }
}

/** Thrown by the mocked `notFound()`; `renderRoute` renders the nearest not-found file. */
export class NotFoundSignal extends Error {
  constructor() {
    super('NEXT_NOT_FOUND')
  }
}

/**
 * The href the app tree has committed to, provided by `renderRoute` (navigations are transitions there, as in Next).
 * Outside it (`renderWithProviders`), hooks follow the in-memory router directly.
 */
export const RouterHrefContext = createContext<string | null>(null)

function useHref(): string {
  const committed = use(RouterHrefContext)
  const live = useSyncExternalStore(router.subscribe, () => router.href, () => router.href)
  return committed ?? live
}

const appRouterInstance = {
  push: (href: string) => {
    router.push(href)
  },
  replace: (href: string) => {
    router.replace(href)
  },
  back: () => {
    router.back()
  },
  forward: () => {
    router.forward()
  },
  refresh: () => {
    router.refresh()
  },
  prefetch: () => undefined,
}

export const navigationMock = {
  useRouter: () => appRouterInstance,
  usePathname: (): string => {
    const href = useHref()
    return new URL(href, 'http://localhost').pathname
  },
  useSearchParams: (): URLSearchParams => {
    const href = useHref()
    const search = new URL(href, 'http://localhost').search
    return useMemo(() => new URLSearchParams(search), [search])
  },
  useParams: (): Record<string, string> => {
    const href = useHref()
    const pathname = new URL(href, 'http://localhost').pathname
    return useMemo(() => matchAppRoute(pathname)?.params ?? {}, [pathname])
  },
  useSelectedLayoutSegment: () => null,
  useSelectedLayoutSegments: () => [],
  redirect: (url: string, type?: 'push' | 'replace'): never => {
    throw new RedirectSignal(url, type ?? 'replace')
  },
  permanentRedirect: (url: string): never => {
    throw new RedirectSignal(url, 'replace')
  },
  notFound: (): never => {
    throw new NotFoundSignal()
  },
  RedirectType: { push: 'push', replace: 'replace' },
  ReadonlyURLSearchParams: URLSearchParams,
}

type LinkMockProps = Omit<ComponentProps<'a'>, 'href'> & {
  href: string
  replace?: boolean
  scroll?: boolean
  prefetch?: boolean | null
}

/** `next/link` double: an anchor that navigates the memory router on a plain left click. */
export function LinkMock(props: LinkMockProps) {
  const { href, replace, onClick, ...anchorProps } = props
  const rest: ComponentProps<'a'> = { ...anchorProps }
  delete (rest as { scroll?: unknown }).scroll
  delete (rest as { prefetch?: unknown }).prefetch
  function handleClick(event: MouseEvent<HTMLAnchorElement>) {
    onClick?.(event)
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.metaKey ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey ||
      (rest.target && rest.target !== '_self')
    ) {
      return
    }
    event.preventDefault()
    if (replace) {
      router.replace(href)
    } else {
      router.push(href)
    }
  }
  return <a href={href} onClick={handleClick} {...rest} />
}
