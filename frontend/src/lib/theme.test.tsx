import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { THEME_STORAGE_KEY, useTheme } from './theme'
import { themeInitScript } from './theme-init'

/** A controllable `prefers-color-scheme: dark` media query (the OS setting). */
function fakeOsScheme(initial: 'light' | 'dark') {
  let dark = initial === 'dark'
  const listeners = new Set<(e: MediaQueryListEvent) => void>()
  const original = globalThis.matchMedia
  globalThis.matchMedia = (query: string): MediaQueryList =>
    ({
      get matches() {
        return query === '(prefers-color-scheme: dark)' ? dark : query === '(prefers-color-scheme: light)' ? !dark : false
      },
      media: query,
      onchange: null,
      addEventListener: (_: string, l: (e: MediaQueryListEvent) => void) => listeners.add(l),
      removeEventListener: (_: string, l: (e: MediaQueryListEvent) => void) => listeners.delete(l),
      addListener: (l: (e: MediaQueryListEvent) => void) => listeners.add(l),
      removeListener: (l: (e: MediaQueryListEvent) => void) => listeners.delete(l),
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList
  return {
    switchTo(scheme: 'light' | 'dark') {
      dark = scheme === 'dark'
      listeners.forEach((l) => {
        l({ matches: dark } as MediaQueryListEvent)
      })
    },
    listenerCount: () => listeners.size,
    restore() {
      globalThis.matchMedia = original
    },
  }
}

/** Runs the inline init script exactly as the browser does (it is shipped as a string in <head>). */
function runInitScript() {
  // eslint-disable-next-line @typescript-eslint/no-implied-eval, @typescript-eslint/no-unsafe-call -- the shipped inline script is a string by design
  new Function(themeInitScript)()
}

const html = () => document.documentElement

function resetHtml() {
  html().className = ''
  html().style.colorScheme = ''
}

describe('FR-204: the theme init script (before first paint)', () => {
  let os: ReturnType<typeof fakeOsScheme> | undefined
  beforeEach(resetHtml)
  afterEach(() => {
    os?.restore()
    os = undefined
    resetHtml()
  })

  it('FR204_default_light: no stored value and an OS in light mode render light, with no dark class at any point', () => {
    os = fakeOsScheme('light')
    const seen: string[] = []
    const observer = new MutationObserver(() => seen.push(html().className))
    observer.observe(html(), { attributes: true, attributeFilter: ['class'] })
    runInitScript()
    // The script is synchronous: the class is set before it returns, i.e. before the first paint.
    expect(html()).toHaveClass('light')
    expect(html()).not.toHaveClass('dark')
    expect(html().style.colorScheme).toBe('light')
    observer.disconnect()
    expect(seen.join(' ')).not.toContain('dark')
  })

  it('FR204_default_dark: no stored value and an OS in dark mode render dark', () => {
    os = fakeOsScheme('dark')
    runInitScript()
    expect(html()).toHaveClass('dark')
    expect(html()).not.toHaveClass('light')
    expect(html().style.colorScheme).toBe('dark')
  })

  it('FR204_FR201_stored: a stored light or dark (including one saved before v6) wins over the OS', () => {
    os = fakeOsScheme('dark')
    localStorage.setItem(THEME_STORAGE_KEY, 'light')
    runInitScript()
    expect(html()).toHaveClass('light')
    expect(html()).not.toHaveClass('dark')

    resetHtml()
    os.restore()
    os = fakeOsScheme('light')
    localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    runInitScript()
    expect(html()).toHaveClass('dark')
    expect(html()).not.toHaveClass('light')
  })

  it('FR204_stored_system: a stored `system` and an unknown value both follow the OS', () => {
    os = fakeOsScheme('dark')
    localStorage.setItem(THEME_STORAGE_KEY, 'system')
    runInitScript()
    expect(html()).toHaveClass('dark')

    resetHtml()
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia')
    runInitScript()
    expect(html()).toHaveClass('dark')
  })

  it('FR204_live: with System chosen, an OS switch to dark (and back) switches the page without a reload', () => {
    os = fakeOsScheme('light')
    runInitScript()
    expect(html()).toHaveClass('light')

    os.switchTo('dark')
    expect(html()).toHaveClass('dark')
    expect(html()).not.toHaveClass('light')
    expect(html().style.colorScheme).toBe('dark')

    os.switchTo('light')
    expect(html()).toHaveClass('light')
    expect(html()).not.toHaveClass('dark')
  })

  it('FR204_live_explicit: with Light chosen (even after load), an OS switch changes nothing', () => {
    os = fakeOsScheme('light')
    runInitScript()
    localStorage.setItem(THEME_STORAGE_KEY, 'light')
    os.switchTo('dark')
    expect(html()).toHaveClass('light')
    expect(html()).not.toHaveClass('dark')
  })

  it('FR204_no_storage: without localStorage access it still resolves System from the OS', () => {
    os = fakeOsScheme('dark')
    const original = Object.getOwnPropertyDescriptor(window, 'localStorage')
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      get() {
        throw new Error('SecurityError')
      },
    })
    try {
      runInitScript()
    } finally {
      if (original) {
        Object.defineProperty(window, 'localStorage', original)
      }
    }
    expect(html()).toHaveClass('dark')
  })
})

function Probe() {
  const { preference, resolvedTheme, setPreference } = useTheme()
  return (
    <div>
      <p data-testid="theme">
        {preference}/{resolvedTheme}
      </p>
      <button
        type="button"
        onClick={() => {
          setPreference('dark')
        }}
      >
        Dark
      </button>
      <button
        type="button"
        onClick={() => {
          setPreference('system')
        }}
      >
        System
      </button>
    </div>
  )
}

describe('FR-204: the useTheme hook', () => {
  let os: ReturnType<typeof fakeOsScheme> | undefined
  afterEach(() => {
    os?.restore()
    os = undefined
    resetHtml()
  })

  it('FR204_hook_default: System by default, resolved from the OS, and it follows an OS switch live', () => {
    os = fakeOsScheme('light')
    render(<Probe />)
    expect(screen.getByTestId('theme')).toHaveTextContent('system/light')

    act(() => {
      os?.switchTo('dark')
    })
    expect(screen.getByTestId('theme')).toHaveTextContent('system/dark')
    expect(html()).toHaveClass('dark')
  })

  it('FR204_hook_set: setPreference stores the choice in aide.theme (light | dark | system) and applies it', () => {
    os = fakeOsScheme('light')
    render(<Probe />)
    act(() => {
      screen.getByRole('button', { name: 'Dark' }).click()
    })
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    expect(screen.getByTestId('theme')).toHaveTextContent('dark/dark')
    expect(html()).toHaveClass('dark')

    act(() => {
      screen.getByRole('button', { name: 'System' }).click()
    })
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('system')
    expect(screen.getByTestId('theme')).toHaveTextContent('system/light')
    expect(html()).toHaveClass('light')
  })

  it('FR204_hook_unsubscribes: the OS listener is removed when the last consumer unmounts', () => {
    os = fakeOsScheme('light')
    const { unmount } = render(<Probe />)
    expect(os.listenerCount()).toBe(1)
    unmount()
    expect(os.listenerCount()).toBe(0)
  })
})
