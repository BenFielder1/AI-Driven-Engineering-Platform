import { useId, type ReactNode } from 'react'

import { cn } from '../../../lib/utils'

/**
 * A marketing page section: a labelled `<section>` (by its `h2`) with an optional eyebrow and lead, in the page's
 * max width. `id` makes it an anchor target (e.g. `/product#artifacts`).
 */
export function Section({
  id,
  eyebrow,
  title,
  lead,
  children,
  className,
  testId,
}: {
  id?: string
  eyebrow?: string
  title: string
  lead?: ReactNode
  children?: ReactNode
  className?: string
  testId?: string
}) {
  const headingId = useId()
  return (
    <section
      id={id}
      aria-labelledby={headingId}
      data-testid={testId}
      className={cn('mx-auto max-w-6xl scroll-mt-20 px-4 py-14 sm:py-20', className)}
    >
      {eyebrow && <p className="eyebrow text-primary">{eyebrow}</p>}
      <h2 id={headingId} className="mt-2 max-w-3xl text-2xl font-semibold tracking-tight text-balance sm:text-3xl">
        {title}
      </h2>
      {lead && <p className="mt-3 max-w-2xl text-base text-muted-foreground sm:text-lg">{lead}</p>}
      {children && <div className="mt-8">{children}</div>}
    </section>
  )
}
