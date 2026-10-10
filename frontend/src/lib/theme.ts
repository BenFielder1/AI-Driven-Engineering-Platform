import { useSyncExternalStore } from 'react'

/** SPEC §7: dark and light theme, system default; the user's override is kept in localStorage. */
export type ThemePreference = 'system' | 'light' | 'dark'

export const THEME_STORAGE_KEY = 'aide.theme'
const DARK_QUERY = '(prefers-color-scheme: dark)'

const listeners = new Set<() => void>()

function isPreference(value: unknown): value is ThemePreference {
  return value === 'system' || value === 'light' || value === 'dark'
}

export function readThemePreference(): ThemePreference {
  try {
    const stored = globalThis.localStorage.getItem(THEME_STORAGE_KEY)
    return isPreference(stored) ? stored : 'system'
  } catch {
    return 'system'
  }
}

function systemPrefersDark(): boolean {
  return typeof globalThis.matchMedia === 'function' && globalThis.matchMedia(DARK_QUERY).matches
}

/** Applies the effective theme to <html> (`.dark` drives Tailwind's dark variant and the colour tokens). */
export function applyTheme(preference: ThemePreference = readThemePreference()): void {
  const dark = preference === 'dark' || (preference === 'system' && systemPrefersDark())
  const root = document.documentElement
  root.classList.toggle('dark', dark)
  root.style.colorScheme = dark ? 'dark' : 'light'
}

export function setThemePreference(preference: ThemePreference): void {
  try {
    if (preference === 'system') {
      globalThis.localStorage.removeItem(THEME_STORAGE_KEY)
    } else {
      globalThis.localStorage.setItem(THEME_STORAGE_KEY, preference)
    }
  } catch {
    // Storage unavailable (private mode, quota): the choice lasts for this page only.
  }
  current = preference
  applyTheme(preference)
  listeners.forEach((listener) => {
    listener()
  })
}

let current: ThemePreference | null = null

/** Applies the stored theme now and follows system changes while the preference is `system`. */
export function initTheme(): void {
  current = readThemePreference()
  applyTheme(current)
  if (typeof globalThis.matchMedia === 'function') {
    globalThis.matchMedia(DARK_QUERY).addEventListener('change', () => {
      if ((current ?? 'system') === 'system') {
        applyTheme('system')
      }
    })
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

function getSnapshot(): ThemePreference {
  return current ?? readThemePreference()
}

export function useThemePreference(): ThemePreference {
  return useSyncExternalStore(subscribe, getSnapshot, () => 'system')
}
