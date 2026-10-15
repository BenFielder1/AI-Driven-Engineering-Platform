import { useRef, type ReactNode, type RefObject } from 'react'

import { cn } from '../../lib/utils'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from './radix-dialog'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from './sheet'

/**
 * Focus handling shared by the modal wrappers (NFR-A11Y): radix moves focus inside, traps it and returns it to the
 * opener; these add an explicit initial target and a stable fallback when the opener has left the DOM (its row was
 * refetched away, its gate decided).
 */
function useFocusTargets(initialFocusRef?: RefObject<HTMLElement | null>, fallbackFocusRef?: RefObject<HTMLElement | null>) {
  const opener = useRef<Element | null>(null)
  return {
    onOpenAutoFocus: (event: Event) => {
      opener.current = document.activeElement
      if (initialFocusRef?.current) {
        event.preventDefault()
        initialFocusRef.current.focus()
      }
    },
    // Radix returns focus to a DialogTrigger; these modals are opened from ordinary buttons, so return it here.
    onCloseAutoFocus: (event: Event) => {
      event.preventDefault()
      // Something on the page already took focus on purpose (e.g. a heading after the item left the list): keep it.
      const active = document.activeElement
      if (active && active !== document.body && active.isConnected) {
        return
      }
      const target = opener.current
      if (target instanceof HTMLElement && target.isConnected && target !== document.body) {
        target.focus()
      } else {
        fallbackFocusRef?.current?.focus()
      }
    },
  }
}

export interface ModalDialogProps {
  open: boolean
  title: string
  /** Short explanation under the title (becomes the accessible description). */
  description?: ReactNode
  /** `alertdialog` for destructive confirmations (SPEC §7: reject and rollback need confirmation). */
  role?: 'dialog' | 'alertdialog'
  /** Element to focus on open; defaults to the first focusable element. */
  initialFocusRef?: RefObject<HTMLElement | null>
  /** Focus target on close when the opener no longer exists. */
  fallbackFocusRef?: RefObject<HTMLElement | null>
  /** Escape is ignored while true (a request is in flight). */
  busy?: boolean
  onClose: () => void
  className?: string
  children: ReactNode
}

/**
 * A labelled modal for small forms (gate and artifact decisions, rollback, confirmations) on the shadcn Dialog
 * (SPEC §7.1). The content provides its own buttons; an outside click never discards what was typed.
 */
export function ModalDialog({
  open,
  title,
  description,
  role = 'dialog',
  initialFocusRef,
  fallbackFocusRef,
  busy = false,
  onClose,
  className,
  children,
}: ModalDialogProps) {
  const focus = useFocusTargets(initialFocusRef, fallbackFocusRef)
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose()
        }
      }}
    >
      <DialogContent
        role={role}
        aria-modal="true"
        showCloseButton={false}
        {...(description ? {} : { 'aria-describedby': undefined })}
        onOpenAutoFocus={focus.onOpenAutoFocus}
        onCloseAutoFocus={focus.onCloseAutoFocus}
        onInteractOutside={(event) => {
          event.preventDefault()
        }}
        onEscapeKeyDown={(event) => {
          if (busy) {
            event.preventDefault()
          }
        }}
        className={cn('max-h-[calc(100dvh-2rem)] overflow-y-auto', className)}
      >
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          {description && (
            <DialogDescription asChild>
              <div>{description}</div>
            </DialogDescription>
          )}
        </DialogHeader>
        <div>{children}</div>
      </DialogContent>
    </Dialog>
  )
}

/**
 * Record details (agent runs) in a shadcn Sheet docked to the right (SPEC §7.1 "detail sheet"). Focus starts on Close,
 * is trapped inside, Escape and Close dismiss it, and focus returns to the opener. Full width on narrow screens.
 */
export function DetailSheet({
  open,
  title,
  description,
  fallbackFocusRef,
  onClose,
  children,
}: {
  open: boolean
  title: string
  description?: ReactNode
  fallbackFocusRef?: RefObject<HTMLElement | null>
  onClose: () => void
  children: ReactNode
}) {
  const focus = useFocusTargets(undefined, fallbackFocusRef)
  return (
    <Sheet
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          onClose()
        }
      }}
    >
      <SheetContent
        side="right"
        aria-modal="true"
        {...(description ? {} : { 'aria-describedby': undefined })}
        onOpenAutoFocus={(event) => {
          focus.onOpenAutoFocus(event)
          // Start on Close (the last focusable rendered by SheetContent), like the v1 drawer.
          const content = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
          const close = content?.querySelector<HTMLElement>('[data-slot="sheet-close-button"]')
          if (close) {
            event.preventDefault()
            close.focus()
          }
        }}
        onCloseAutoFocus={focus.onCloseAutoFocus}
        className="w-full gap-0 border-border/60 bg-background/95 backdrop-blur-xl sm:max-w-2xl"
      >
        <SheetHeader className="border-b border-border/60 pr-14">
          <SheetTitle className="text-lg break-words">{title}</SheetTitle>
          {description && <SheetDescription>{description}</SheetDescription>}
        </SheetHeader>
        <div className="min-h-0 flex-1 overflow-y-auto p-4">{children}</div>
      </SheetContent>
    </Sheet>
  )
}
