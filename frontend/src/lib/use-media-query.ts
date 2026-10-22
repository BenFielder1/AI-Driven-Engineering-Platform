import { useSyncExternalStore } from 'react'

/** Whether a CSS media query matches, kept in sync with the viewport (false where matchMedia is missing). */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      if (typeof globalThis.matchMedia !== 'function') {
        return () => undefined
      }
      const list = globalThis.matchMedia(query)
      list.addEventListener('change', onChange)
      return () => {
        list.removeEventListener('change', onChange)
      }
    },
    () => typeof globalThis.matchMedia === 'function' && globalThis.matchMedia(query).matches,
    () => false,
  )
}

/** Tailwind's `md` breakpoint (48rem): tables from here up, stacked cards below. */
export const MD_QUERY = '(min-width: 48rem)'
