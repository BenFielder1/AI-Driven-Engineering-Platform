import { cn } from '../../lib/utils'

/**
 * Form control surface (FR-205): `rounded-lg`, the 3:1 `field-border`, the focus ring and the destructive border when
 * invalid. Text stays 16 px on small screens (no iOS zoom on focus).
 */
export function fieldClass(invalid: boolean, extra = '') {
  return cn(
    'min-h-10 w-full min-w-0 rounded-lg border bg-background px-3 py-2 text-base text-foreground shadow-xs sm:text-sm',
    'transition-colors placeholder:text-muted-foreground',
    // FR-205: the global focus ring (2 px, offset, in the focus colour) plus the border in the same colour.
    'focus-visible:border-ring',
    'disabled:cursor-not-allowed disabled:opacity-60',
    invalid ? 'border-destructive ring-destructive/20' : 'border-field-border',
    extra,
  )
}

/** Labels above controls. */
export const labelClass = 'text-sm font-medium'
