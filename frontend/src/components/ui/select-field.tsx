import { useId, type ReactNode, type Ref, type SelectHTMLAttributes } from 'react'

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
    <div className={`flex flex-col gap-1 ${className}`}>
      <label htmlFor={selectId} className="text-sm font-medium">
        {label}
      </label>
      {hint && (
        <p id={hintId} className="text-sm text-muted">
          {hint}
        </p>
      )}
      <select
        ref={ref}
        id={selectId}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={
          'min-h-10 rounded-md border bg-canvas px-3 py-2 text-base text-fg ' +
          (error ? 'border-danger' : 'border-field-border')
        }
        {...selectProps}
      >
        {children}
      </select>
      {error && (
        <p id={errorId} className="text-sm text-danger">
          {error}
        </p>
      )}
    </div>
  )
}
