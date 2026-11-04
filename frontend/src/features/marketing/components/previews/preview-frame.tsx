import type { ReactNode } from 'react'

import { panelClass } from '../../../../components/shared/panel-class'
import { cn } from '../../../../lib/utils'

/**
 * FR-220: the frame around a product preview. The drawing is `aria-hidden` and `inert` (nothing in it is focusable or
 * announced); the visible caption is its text alternative. Previews hold no links or buttons, only look-alikes.
 */
export function PreviewFrame({
  caption,
  children,
  className,
  chrome = false,
}: {
  caption: string
  children: ReactNode
  className?: string
  /** A window title bar, for the large hero preview. */
  chrome?: boolean
}) {
  return (
    <figure className={cn('m-0', className)} data-testid="product-preview">
      <div aria-hidden="true" inert className={cn(panelClass, 'overflow-hidden text-left shadow-lg select-none')}>
        {chrome && (
          <div className="flex items-center gap-1.5 border-b border-border bg-muted px-3 py-2">
            <span className="size-2.5 rounded-full bg-border" />
            <span className="size-2.5 rounded-full bg-border" />
            <span className="size-2.5 rounded-full bg-border" />
          </div>
        )}
        <div className="p-4">{children}</div>
      </div>
      <figcaption className="mt-3 text-center text-sm text-muted-foreground">{caption}</figcaption>
    </figure>
  )
}

/** A button look-alike for previews (not focusable, not a control). */
export function FakeButton({ children, primary = false }: { children: ReactNode; primary?: boolean }) {
  return (
    <span
      className={cn(
        'inline-flex h-8 items-center rounded-lg px-3 text-xs font-medium',
        primary ? 'bg-primary text-primary-foreground' : 'border border-field-border bg-background text-foreground',
      )}
    >
      {children}
    </span>
  )
}
