import { useEffect, type KeyboardEvent, type RefObject } from 'react'

const FOCUSABLE =
  'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

/**
 * Modal focus management (NFR-A11Y): on open, focus moves to `initialFocus` (or the first focusable element), Tab and
 * Shift+Tab are trapped inside `panelRef`, Escape calls `onEscape`, and focus returns to the opener on close
 * (or to `fallbackFocusRef` when the opener is gone).
 * Returns the panel's keydown handler.
 */
export function useModalFocus({
  open,
  panelRef,
  initialFocusRef,
  fallbackFocusRef,
  onEscape,
}: {
  open: boolean
  panelRef: RefObject<HTMLElement | null>
  initialFocusRef?: RefObject<HTMLElement | null> | undefined
  /** Where focus goes on close if the opener has left the DOM (e.g. its list item was refetched away). */
  fallbackFocusRef?: RefObject<HTMLElement | null> | undefined
  onEscape: () => void
}) {
  useEffect(() => {
    if (!open) {
      return undefined
    }
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const target = initialFocusRef?.current ?? panelRef.current?.querySelector<HTMLElement>(FOCUSABLE)
    target?.focus()
    return () => {
      if (opener?.isConnected) {
        opener.focus()
      } else {
        // Read at close on purpose: the fallback element may have been re-rendered since open.
        // eslint-disable-next-line react-hooks/exhaustive-deps
        fallbackFocusRef?.current?.focus()
      }
    }
  }, [open, initialFocusRef, fallbackFocusRef, panelRef])

  return function onKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.key === 'Escape') {
      event.stopPropagation()
      onEscape()
      return
    }
    if (event.key !== 'Tab' || !panelRef.current) {
      return
    }
    const focusable = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE))
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (!first || !last) {
      return
    }
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }
}
