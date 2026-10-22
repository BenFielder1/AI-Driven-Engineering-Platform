import { ChevronDown } from 'lucide-react'
import { useId, type ReactNode, type Ref, type SelectHTMLAttributes } from 'react'

import { cn } from '../../lib/utils'
import { fieldClass } from './field-classes'

/** A restyled native `<select>` (SPEC §7.1): keeps the platform picker and keyboard behaviour, with a chevron. */
export function NativeSelect({
  className,
  wrapperClassName,
  invalid = false,
  ref,
  ...props
}: SelectHTMLAttributes<HTMLSelectElement> & { invalid?: boolean; wrapperClassName?: string; ref?: Ref<HTMLSelectElement> }) {
  return (
    <div className={cn('relative min-w-0', wrapperClassName)}>
      <select ref={ref} className={fieldClass(invalid, cn('appearance-none pr-9', className))} {...props} />
      <ChevronDown aria-hidden className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground" />
    </div>
  )
}

export interface SelectFieldProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id'> {
  label: string
  hint?: string
  error?: string | undefined
  id?: string
  ref?: Ref<HTMLSelectElement>
  children: ReactNode
}

/** A labelled `<select>` with hint and error text wired up like TextField. */
export function SelectField({ label, hint, error, id, className = '', ref, children, ...selectProps }: SelectFieldProps) {
  const generated = useId()
  const selectId = id ?? generated
  const hintId = hint ? `${selectId}-hint` : undefined
  const errorId = error ? `${selectId}-error` : undefined
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined

  return (
    <div className={cn('flex flex-col gap-1', className)}>
      <label htmlFor={selectId} className="text-sm font-medium">
        {label}
      </label>
      {hint && (
        <p id={hintId} className="text-sm text-muted-foreground">
          {hint}
        </p>
      )}
      <NativeSelect
        ref={ref}
        id={selectId}
        invalid={Boolean(error)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        {...selectProps}
      >
        {children}
      </NativeSelect>
      {error && (
        <p id={errorId} className="text-sm text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}
