import type { ReactNode } from 'react'

import { panelClass } from '../../components/shared/panel-class'
import { cn } from '../../lib/utils'

/** A titled panel on a stage page (h3 under the stage's h2). `id` doubles as the deep-link anchor. */
export function StageSection({
  id,
  title,
  actions,
  children,
  className,
}: {
  id: string
  title: ReactNode
  actions?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section id={id} aria-labelledby={`${id}-title`} className={cn(panelClass, 'flex scroll-mt-24 flex-col gap-3 p-4', className)}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={`${id}-title`} className="text-lg">
          {title}
        </h3>
        {actions}
      </div>
      {children}
    </section>
  )
}
