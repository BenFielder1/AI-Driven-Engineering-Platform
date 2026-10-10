import { useId, useRef, type ReactNode, type RefObject } from 'react'

import { Button } from './button'
import { useModalFocus } from './use-modal-focus'

interface ConfirmDialogProps {
  open: boolean
  title: string
  children?: ReactNode
  confirmLabel: string
  pendingLabel?: string
  pending?: boolean
  destructive?: boolean
  /** Keeps the confirm button disabled (e.g. until the typed project name matches). */
  confirmDisabled?: boolean
  /** Element to focus on open instead of Cancel (e.g. a confirmation input). */
  initialFocusRef?: RefObject<HTMLElement | null>
  onConfirm: () => void
  onCancel: () => void
}

/**
 * Modal confirmation (SPEC §7: destructive actions need confirmation). Focus moves to Cancel on open, is trapped
 * inside, Escape cancels, and focus returns to the opener on close.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  pendingLabel,
  pending = false,
  destructive = false,
  confirmDisabled = false,
  initialFocusRef,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const titleId = useId()
  const bodyId = useId()
  const panelRef = useRef<HTMLDivElement>(null)
  const cancelRef = useRef<HTMLButtonElement>(null)

  const onKeyDown = useModalFocus({
    open,
    panelRef,
    initialFocusRef: initialFocusRef ?? cancelRef,
    onEscape: () => {
      if (!pending) {
        onCancel()
      }
    },
  })

  if (!open) {
    return null
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div
        ref={panelRef}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={children ? bodyId : undefined}
        onKeyDown={onKeyDown}
        className="w-full max-w-md rounded-lg border border-border bg-surface-raised p-5 text-fg shadow-xl"
      >
        <h2 id={titleId} className="text-lg font-semibold">
          {title}
        </h2>
        {children && (
          <div id={bodyId} className="mt-2 text-sm text-muted">
            {children}
          </div>
        )}
        <div className="mt-5 flex flex-wrap justify-end gap-2">
          <Button ref={cancelRef} variant="secondary" onClick={onCancel} disabled={pending}>
            Cancel
          </Button>
          <Button
            variant={destructive ? 'danger' : 'primary'}
            onClick={onConfirm}
            disabled={confirmDisabled}
            pending={pending}
            pendingLabel={pendingLabel}
          >
            {confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  )
}
