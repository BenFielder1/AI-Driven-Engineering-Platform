import { useSyncExternalStore } from 'react'

/** FR-207, SPEC §5: dismissing the Home checklist stores `"1"` here; "Show setup checklist" removes it. */
export const CHECKLIST_DISMISSED_KEY = 'tevrin.home.checklist-dismissed'
/** FR-326: the pre-0.8.0 key, read when CHECKLIST_DISMISSED_KEY is missing; writing removes it. */
export const LEGACY_CHECKLIST_DISMISSED_KEY = 'aide.home.checklist-dismissed'

// Used only when localStorage is unavailable: the choice then lasts for this page.
let memory = false
const listeners = new Set<() => void>()

function read(): boolean {
  try {
    const stored =
      globalThis.localStorage.getItem(CHECKLIST_DISMISSED_KEY) ??
      globalThis.localStorage.getItem(LEGACY_CHECKLIST_DISMISSED_KEY)
    return stored === '1'
  } catch {
    return memory
  }
}

export function setChecklistDismissed(dismissed: boolean): void {
  try {
    globalThis.localStorage.removeItem(LEGACY_CHECKLIST_DISMISSED_KEY)
    if (dismissed) {
      globalThis.localStorage.setItem(CHECKLIST_DISMISSED_KEY, '1')
    } else {
      globalThis.localStorage.removeItem(CHECKLIST_DISMISSED_KEY)
    }
  } catch {
    memory = dismissed
  }
  listeners.forEach((l) => {
    l()
  })
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

export function useChecklistDismissed(): boolean {
  return useSyncExternalStore(subscribe, read, () => false)
}
