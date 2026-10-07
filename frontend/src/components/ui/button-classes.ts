export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost'

const variants: Record<ButtonVariant, string> = {
  primary: 'bg-primary text-on-primary hover:bg-primary-hover',
  secondary: 'border border-field-border bg-surface-raised text-fg hover:bg-surface',
  danger: 'bg-danger text-on-danger hover:opacity-90',
  ghost: 'text-fg hover:bg-surface',
}

export const buttonClasses = (variant: ButtonVariant = 'primary', extra = '') =>
  `inline-flex min-h-10 items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-medium ` +
  `transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${variants[variant]} ${extra}`.trim()
