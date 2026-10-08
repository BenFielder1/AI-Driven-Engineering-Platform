import { useId, useRef, type ReactNode, type RefObject } from 'react'

import { useModalFocus } from './use-modal-focus'

interface DialogProps {
  open: boolean
  title: string
  /** Short explanation under the title (becomes the accessible description). */
  description?: ReactNode
  /** `alertdialog` for destructive confirmations (SPEC §7: reject needs confirmation). */
  role?: 'dialog' | 'alertdialog'
  /** Element to focus on open; defaults to the first focusable element. */
  initialFocusRef?: RefObject<HTMLElement | null>
  /** Focus target on close when the opener no longer exists. */
  fallbackFocusRef?: RefObject<HTMLElement | null>
  /** Escape is ignored while true (a request is in flight). */
  busy?: boolean
  onClose: () => void
  children: ReactNode
}

/**
 * A labelled modal for small forms (gate decisions): focus moves inside on open, is trapped, Escape closes,
 * and focus returns to the opener on close. The content provides its own buttons.
 */
export function Dialog({
  open,
  title,
  description,
  role = 'dialog',
  initialFocusRef,
  fallbackFocusRef,
  busy = false,
  onClose,
  children,
}: DialogProps) {
  const titleId = useId()
  const descriptionId = useId()
  const panelRef = useRef<HTMLDivElement>(null)
  const onKeyDown = useModalFocus({
    open,
    panelRef,
    initialFocusRef,
    fallbackFocusRef,
    onEscape: () => {
      if (!busy) {
        onClose()
      }
    },
  })

  if (!open) {
    return null
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center overflow-y-auto bg-black/50 p-4">
      <div
        ref={panelRef}
        role={role}
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        onKeyDown={onKeyDown}
        className="w-full max-w-lg rounded-lg border border-border bg-surface-raised p-5 text-fg shadow-xl"
      >
        <h2 id={titleId} className="text-lg font-semibold">
          {title}
        </h2>
        {description && (
          <div id={descriptionId} className="mt-1 text-sm text-muted">
            {description}
          </div>
        )}
        <div className="mt-4">{children}</div>
      </div>
    </div>
  )
}
