/**
 * An in-memory router standing in for the Next.js App Router in Vitest (src/test/setup.ts mocks `next/navigation` and
 * `next/link` with it). One instance per test run, reset before each test. Its shape also covers what v2 tests read
 * from the TanStack router: `router.state.location.{pathname,search,href}`, `router.history.back()`,
 * `router.navigate({ to | href })`.
 */

type Listener = () => void

/** TanStack-style search parsing for test assertions: numbers and booleans come back typed (`?version=4` → 4). */
export function parseSearchTyped(searchStr: string): Record<string, unknown> {
  const result: Record<string, unknown> = {}
  new URLSearchParams(searchStr).forEach((value, key) => {
    if (value === 'true' || value === 'false') {
      result[key] = value === 'true'
    } else if (value.trim() !== '' && /^-?\d+(\.\d+)?$/.test(value)) {
      result[key] = Number(value)
    } else {
      result[key] = value
    }
  })
  return result
}

function normalise(href: string, base: string): string {
  const url = new URL(href, `http://localhost${base}`)
  return `${url.pathname}${url.search}${url.hash}`
}

export interface MemoryLocation {
  pathname: string
  /** Parsed like TanStack (numbers/booleans typed). */
  search: Record<string, unknown>
  /** `?a=b` or ''. */
  searchStr: string
  hash: string
  /** pathname + search + hash. */
  href: string
}

export class MemoryRouter {
  private entries: string[] = ['/']
  private index = 0
  private listeners = new Set<Listener>()
  /** Every navigation, in order (for assertions): `{ type: 'push' | 'replace' | 'back' | 'forward', href }`. */
  readonly calls: { type: 'push' | 'replace' | 'back' | 'forward' | 'refresh'; href: string }[] = []

  get href(): string {
    return this.entries[this.index] ?? '/'
  }

  get location(): MemoryLocation {
    const url = new URL(this.href, 'http://localhost')
    return {
      pathname: url.pathname,
      search: parseSearchTyped(url.search),
      searchStr: url.search,
      hash: url.hash,
      href: this.href,
    }
  }

  /** TanStack-compatible `router.state.location`. */
  get state(): { location: MemoryLocation } {
    return { location: this.location }
  }

  /** TanStack-compatible `router.history`. */
  get history() {
    return {
      push: (href: string) => {
        this.push(href)
      },
      replace: (href: string) => {
        this.replace(href)
      },
      back: () => {
        this.back()
      },
      forward: () => {
        this.forward()
      },
      get length(): number {
        return router.entries.length
      },
      /** v2 TanStack history: `location.state.__TSR_index` is the entry's index in the stack. */
      get location() {
        return { state: { __TSR_index: router.index } }
      },
    }
  }

  reset(initial = '/'): void {
    this.entries = [normalise(initial, '/')]
    this.index = 0
    this.calls.length = 0
    this.emit()
  }

  push(href: string): void {
    const next = normalise(href, this.href)
    this.entries = [...this.entries.slice(0, this.index + 1), next]
    this.index = this.entries.length - 1
    this.calls.push({ type: 'push', href: next })
    this.emit()
  }

  replace(href: string): void {
    const next = normalise(href, this.href)
    this.entries = [...this.entries.slice(0, this.index), next, ...this.entries.slice(this.index + 1)]
    this.calls.push({ type: 'replace', href: next })
    this.emit()
  }

  back(): void {
    if (this.index > 0) {
      this.index -= 1
      this.calls.push({ type: 'back', href: this.href })
      this.emit()
    }
  }

  forward(): void {
    if (this.index < this.entries.length - 1) {
      this.index += 1
      this.calls.push({ type: 'forward', href: this.href })
      this.emit()
    }
  }

  refresh(): void {
    this.calls.push({ type: 'refresh', href: this.href })
  }

  /** TanStack-compatible `router.navigate({ to, search, replace } | { href })` for tests (plain paths only). */
  navigate(options: { to?: string; href?: string; search?: Record<string, string>; replace?: boolean }): Promise<void> {
    const query = options.search ? `?${new URLSearchParams(options.search).toString()}` : ''
    const href = options.href ?? `${options.to ?? this.location.pathname}${query}`
    if (options.replace) {
      this.replace(href)
    } else {
      this.push(href)
    }
    return Promise.resolve()
  }

  subscribe = (listener: Listener): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  private emit(): void {
    // Mirror the URL into jsdom's location, like the App Router writes browser history: code that reads
    // window.location (the adapter's useHash, the session-ended redirect) sees the in-memory URL.
    if (typeof window !== 'undefined') {
      const hashBefore = window.location.hash
      window.history.replaceState(window.history.state, '', this.href)
      if (window.location.hash !== hashBefore) {
        window.dispatchEvent(new HashChangeEvent('hashchange'))
      }
    }
    for (const listener of [...this.listeners]) {
      listener()
    }
  }
}

/** The shared test router (reset in src/test/setup.ts after each test, and by `renderRoute`). */
export const router = new MemoryRouter()
