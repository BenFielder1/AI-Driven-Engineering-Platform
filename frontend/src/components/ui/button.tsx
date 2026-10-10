import type { ButtonHTMLAttributes, Ref } from 'react'

import { buttonClasses, type ButtonVariant } from './button-classes'

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  /** Shows the pending label and disables the button (aria-busy for assistive tech). */
  pending?: boolean
  pendingLabel?: string
  ref?: Ref<HTMLButtonElement>
}

export function Button({
  variant = 'primary',
  pending = false,
  pendingLabel,
  className = '',
  type = 'button',
  disabled,
  children,
  ref,
  ...rest
}: ButtonProps) {
  return (
    <button
      ref={ref}
      type={type}
      className={buttonClasses(variant, className)}
      disabled={disabled === true || pending}
      aria-busy={pending || undefined}
      {...rest}
    >
      {pending && pendingLabel ? pendingLabel : children}
    </button>
  )
}
