import { cn } from '../../lib/utils'

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost'

const variants: Record<ButtonVariant, string> = {
  // SPEC §7.1: primary CTAs carry the primary glow.
  primary: 'glow-primary bg-primary text-primary-foreground hover:bg-primary/90',
  secondary: 'border border-field-border bg-card/70 text-foreground hover:bg-accent',
  danger: 'bg-destructive text-destructive-foreground hover:bg-destructive/90',
  ghost: 'text-foreground hover:bg-accent',
}

/** Classes for a button (or a link styled as one). Controls are `rounded-lg` (SPEC §7.1). */
export const buttonClasses = (variant: ButtonVariant = 'primary', extra = '') =>
  cn(
    'inline-flex min-h-10 items-center justify-center gap-2 rounded-lg px-4 py-2 text-sm font-medium',
    'transition-colors disabled:cursor-not-allowed disabled:opacity-60 [&_svg]:shrink-0',
    variants[variant],
    extra,
  )
