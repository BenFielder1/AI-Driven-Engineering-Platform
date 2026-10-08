import { useId, useRef, type ReactNode, type RefObject } from 'react'

import { Button } from './button'
import { useModalFocus } from './use-modal-focus'

interface DrawerProps {
  open: boolean
  title: string
  /** Focus target on close when the opener no longer exists (e.g. its table row was refetched away). */
  fallbackFocusRef?: RefObject<HTMLElement | null>
  onClose: () => void
  children: ReactNode
}

/**
 * A labelled modal panel docked to the right edge, for record details (agent runs). Focus moves to the Close button on
 * open, is trapped inside, Escape and Close dismiss it, and focus returns to the opener (NFR-A11Y). Full width on
 * narrow screens.
 */
export function Drawer({ open, title, fallbackFocusRef, onClose, children }: DrawerProps) {
  const titleId = useId()
  const panelRef = useRef<HTMLDivElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const onKeyDown = useModalFocus({ open, panelRef, initialFocusRef: closeRef, fallbackFocusRef, onEscape: onClose })

  if (!open) {
    return null
  }

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/50">
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={onKeyDown}
        className="flex h-full w-full max-w-2xl flex-col border-l border-border bg-surface-raised text-fg shadow-xl"
      >
        <div className="flex items-start justify-between gap-3 border-b border-border p-4">
          <h2 id={titleId} className="break-words text-lg font-semibold">
            {title}
          </h2>
          <Button ref={closeRef} variant="secondary" onClick={onClose}>
            Close
          </Button>
        </div>
        <div className="flex-1 overflow-y-auto p-4">{children}</div>
      </div>
    </div>
  )
}
