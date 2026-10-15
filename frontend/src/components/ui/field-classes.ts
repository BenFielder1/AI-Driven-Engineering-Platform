import { cn } from '../../lib/utils'

/**
 * Form control surface (SPEC §7.1): `rounded-lg`, the 3:1 `field-border`, a focus ring and the destructive border when
 * invalid. Text stays 16 px on small screens (no iOS zoom on focus).
 */
export function fieldClass(invalid: boolean, extra = '') {
  return cn(
    'min-h-10 w-full min-w-0 rounded-lg border bg-background/60 px-3 py-2 text-base text-foreground shadow-xs sm:text-sm',
    'transition-[color,box-shadow] outline-none placeholder:text-muted-foreground',
    'focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50',
    'disabled:cursor-not-allowed disabled:opacity-60',
    invalid ? 'border-destructive ring-destructive/20' : 'border-field-border',
    extra,
  )
}

/** Labels above controls. */
export const labelClass = 'text-sm font-medium'
