import { useSyncExternalStore } from 'react'

/**
 * FR-204 (amends SPEC v3 §7.1): Light, Dark and System; System is the default. The choice is stored in localStorage
 * `aide.theme` as `light` | `dark` | `system` (the key keeps its v5 name, so a saved theme carries over: FR-201).
 * src/lib/theme-init.ts applies the same rule before first paint and follows the OS while System is chosen: keep the
 * two in step.
 */
export type ThemePreference = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

export const THEME_STORAGE_KEY = 'aide.theme'
export const THEME_PREFERENCES: readonly ThemePreference[] = ['system', 'light', 'dark']
export const DARK_SCHEME_QUERY = '(prefers-color-scheme: dark)'

export function isThemePreference(value: unknown): value is ThemePreference {
  return value === 'system' || value === 'light' || value === 'dark'
}

// Only used when localStorage is unavailable (private mode, quota): the choice then lasts for this page.
let memoryPreference: ThemePreference | null = null

export function readThemePreference(): ThemePreference {
  try {
    const stored = globalThis.localStorage.getItem(THEME_STORAGE_KEY)
    return isThemePreference(stored) ? stored : 'system'
  } catch {
    return memoryPreference ?? 'system'
  }
}

function darkQuery(): MediaQueryList | null {
  return typeof globalThis.matchMedia === 'function' ? globalThis.matchMedia(DARK_SCHEME_QUERY) : null
}

/** Whether the OS asks for dark. No answer (or no matchMedia) means light, the CSS default. */
export function systemPrefersDark(): boolean {
  return darkQuery()?.matches ?? false
}

export function resolveTheme(preference: ThemePreference, systemDark: boolean = systemPrefersDark()): ResolvedTheme {
  return preference === 'dark' || (preference === 'system' && systemDark) ? 'dark' : 'light'
}

/** Applies the effective theme to <html>: `.dark` or `.light` selects the token set (index.css). */
export function applyTheme(preference: ThemePreference = readThemePreference()): ResolvedTheme {
  const resolved = resolveTheme(preference)
  const root = document.documentElement
  root.classList.toggle('dark', resolved === 'dark')
  root.classList.toggle('light', resolved === 'light')
  root.style.colorScheme = resolved
  return resolved
}

const listeners = new Set<() => void>()

function notify() {
  listeners.forEach((listener) => {
    listener()
  })
}

export function setThemePreference(preference: ThemePreference): void {
  try {
    globalThis.localStorage.setItem(THEME_STORAGE_KEY, preference)
  } catch {
    memoryPreference = preference
  }
  applyTheme(preference)
  notify()
}

/** The OS switched between light and dark: follow it while System is chosen (the init script does the same). */
function onSystemChange() {
  if (readThemePreference() === 'system') {
    applyTheme('system')
  }
  notify()
}

/** Another tab changed the theme. */
function onStorage(event: StorageEvent) {
  if (event.key === THEME_STORAGE_KEY || event.key === null) {
    applyTheme()
    notify()
  }
}

let query: MediaQueryList | null = null

function subscribe(listener: () => void): () => void {
  if (listeners.size === 0) {
    query = darkQuery()
    query?.addEventListener('change', onSystemChange)
    globalThis.addEventListener('storage', onStorage)
  }
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
    if (listeners.size === 0) {
      query?.removeEventListener('change', onSystemChange)
      query = null
      globalThis.removeEventListener('storage', onStorage)
    }
  }
}

/** Applies the stored theme now (the init script has normally done so already) and keeps <html> following the OS. */
export function initTheme(): () => void {
  applyTheme()
  return subscribe(() => undefined)
}

const getPreference = () => readThemePreference()
const getResolved = () => resolveTheme(readThemePreference())

/** The stored choice (`system` by default). The server render (and first client render) is `system`. */
export function useThemePreference(): ThemePreference {
  return useSyncExternalStore(subscribe, getPreference, () => 'system')
}

/** The theme in effect (`light` or `dark`), following the OS while System is chosen. */
export function useResolvedTheme(): ResolvedTheme {
  return useSyncExternalStore(subscribe, getResolved, () => 'light')
}

/**
 * FR-204 theme hook for selectors (ThemeSelector, the user menu, the marketing footer): the stored choice, the theme
 * in effect, and a setter that stores and applies a new choice.
 */
export function useTheme(): { preference: ThemePreference; resolvedTheme: ResolvedTheme; setPreference: (preference: ThemePreference) => void } {
  return { preference: useThemePreference(), resolvedTheme: useResolvedTheme(), setPreference: setThemePreference }
}
