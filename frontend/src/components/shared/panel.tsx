import type { ComponentProps } from 'react'

import { cn } from '../../lib/utils'
import { panelClass } from './panel-class'

/** A frosted card surface (SPEC §7.1). */
export function Panel({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="panel" className={cn(panelClass, className)} {...props} />
}
