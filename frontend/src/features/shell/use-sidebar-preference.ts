import { useCallback, useState, useSyncExternalStore } from 'react'

/**
 * SPEC §7.1: the sidebar is icons-only (64 px) from lg to xl and expanded (256 px) from xl, unless the user pins it
 * collapsed or expanded. The pin is kept in localStorage; storage failures (private mode, quota) fall back to `auto`.
 */
export type SidebarMode = 'auto' | 'collapsed' | 'expanded'

export const SIDEBAR_STORAGE_KEY = 'aide.sidebar'
const XL_QUERY = '(min-width: 80rem)'

export function readSidebarMode(): SidebarMode {
  try {
    const stored = globalThis.localStorage.getItem(SIDEBAR_STORAGE_KEY)
    return stored === 'collapsed' || stored === 'expanded' ? stored : 'auto'
  } catch {
    return 'auto'
  }
}

function writeSidebarMode(mode: SidebarMode): void {
  try {
    if (mode === 'auto') {
      globalThis.localStorage.removeItem(SIDEBAR_STORAGE_KEY)
    } else {
      globalThis.localStorage.setItem(SIDEBAR_STORAGE_KEY, mode)
    }
  } catch {
    // Storage unavailable: the choice lasts for this page only.
  }
}

function subscribeXl(onChange: () => void): () => void {
  if (typeof globalThis.matchMedia !== 'function') {
    return () => undefined
  }
  const query = globalThis.matchMedia(XL_QUERY)
  query.addEventListener('change', onChange)
  return () => {
    query.removeEventListener('change', onChange)
  }
}

const isXl = () => typeof globalThis.matchMedia === 'function' && globalThis.matchMedia(XL_QUERY).matches

/** The pinned mode, whether the sidebar currently shows expanded (at lg and up), and a toggle that pins the opposite. */
export function useSidebarPreference() {
  const [mode, setMode] = useState<SidebarMode>(readSidebarMode)
  const xl = useSyncExternalStore(subscribeXl, isXl, () => false)
  const expanded = mode === 'expanded' || (mode === 'auto' && xl)

  const toggle = useCallback(() => {
    const next: SidebarMode = expanded ? 'collapsed' : 'expanded'
    writeSidebarMode(next)
    setMode(next)
  }, [expanded])

  return { mode, expanded, toggle }
}
