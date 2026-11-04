import { createContext, use, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

/**
 * FR-206: the header's primary-action slot. The shell provides the slot element; a page fills it with `HeaderAction`
 * (e.g. Projects → "New project"). Outside the shell (a page rendered on its own), the action renders in place.
 */
interface HeaderActionSlot {
  /** The header element to render into; `null` until it has mounted. */
  element: HTMLElement | null
}

const HeaderActionSlotContext = createContext<HeaderActionSlot | undefined>(undefined)

export function HeaderActionSlotProvider({ element, children }: { element: HTMLElement | null; children: ReactNode }) {
  return <HeaderActionSlotContext value={{ element }}>{children}</HeaderActionSlotContext>
}

/** Renders `children` in the header's primary-action slot (FR-206). */
export function HeaderAction({ children }: { children: ReactNode }) {
  const slot = use(HeaderActionSlotContext)
  if (!slot) {
    return children
  }
  return slot.element ? createPortal(children, slot.element) : null
}
