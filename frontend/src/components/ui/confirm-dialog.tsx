import { useRef, type ReactNode, type RefObject } from 'react'

import { Button } from './button'
import { ModalDialog } from './modal-dialog'

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
 * Modal confirmation (SPEC §7: destructive actions need confirmation) on the shadcn Dialog as an alertdialog. Focus
 * moves to Cancel on open, is trapped inside, Escape cancels (not while pending), and focus returns to the opener.
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
  const cancelRef = useRef<HTMLButtonElement>(null)
  return (
    <ModalDialog
      open={open}
      role="alertdialog"
      title={title}
      description={children}
      initialFocusRef={initialFocusRef ?? cancelRef}
      busy={pending}
      onClose={onCancel}
      className="sm:max-w-md"
    >
      <div className="flex flex-wrap justify-end gap-2">
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
    </ModalDialog>
  )
}
