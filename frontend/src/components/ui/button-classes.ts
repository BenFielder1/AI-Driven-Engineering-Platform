import { cn } from '../../lib/utils'

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost'

const variants: Record<ButtonVariant, string> = {
  primary: 'bg-primary text-primary-foreground shadow-xs hover:bg-primary/90',
  secondary: 'border border-field-border bg-background text-foreground hover:bg-accent',
  danger: 'bg-destructive text-destructive-foreground hover:bg-destructive/90',
  ghost: 'text-foreground hover:bg-accent',
}

/**
 * Classes for a button (or a link styled as one), FR-205: `rounded-lg`, at least 40 px tall (targets ≥ 24 × 24), the
 * global 2 px offset focus ring in the focus colour, and a 150 ms colour transition (off under reduced motion).
 */
export const buttonClasses = (variant: ButtonVariant = 'primary', extra = '') =>
  cn(
    'inline-flex min-h-10 items-center justify-center gap-2 rounded-lg px-4 py-2 text-sm font-medium',
    'transition-colors disabled:cursor-not-allowed disabled:opacity-60 [&_svg]:shrink-0',
    variants[variant],
    extra,
  )
