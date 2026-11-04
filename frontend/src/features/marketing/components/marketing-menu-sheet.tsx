import type { ReactNode, RefObject } from 'react'

import { Sheet, SheetContent, SheetTitle } from '../../../components/ui/sheet'

/**
 * FR-210: the mobile menu sheet (Radix dialog: focus trapped, Escape closes). Loaded on first use by the marketing
 * header, so the dialog code isn't part of `/`'s first-load JS (SPEC §8). Its trigger lives in the header, so focus is
 * returned to it explicitly on close.
 */
export default function MarketingMenuSheet({
  open,
  onOpenChange,
  triggerRef,
  children,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  triggerRef: RefObject<HTMLButtonElement | null>
  children: ReactNode
}) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent
        side="right"
        aria-describedby={undefined}
        className="p-4 pt-14"
        onCloseAutoFocus={(event) => {
          event.preventDefault()
          triggerRef.current?.focus()
        }}
      >
        <SheetTitle className="sr-only">Menu</SheetTitle>
        {children}
      </SheetContent>
    </Sheet>
  )
}
