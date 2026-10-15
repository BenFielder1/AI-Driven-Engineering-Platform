import { useId, type InputHTMLAttributes, type Ref } from 'react'

import { fieldClass } from './field-classes'

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string
  hint?: string
  error?: string | undefined
  /** Stable id (also the error-summary link target); generated when omitted. */
  id?: string
  ref?: Ref<HTMLInputElement>
}

/** A labelled input with hint and error text wired up through aria-describedby / aria-invalid. */
export function TextField({ label, hint, error, id, className = '', ref, ...inputProps }: TextFieldProps) {
  const generated = useId()
  const inputId = id ?? generated
  const hintId = hint ? `${inputId}-hint` : undefined
  const errorId = error ? `${inputId}-error` : undefined
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined

  return (
    <div className={`flex flex-col gap-1 ${className}`}>
      <label htmlFor={inputId} className="text-sm font-medium">
        {label}
      </label>
      {hint && (
        <p id={hintId} className="text-sm text-muted-foreground">
          {hint}
        </p>
      )}
      <input
        ref={ref}
        id={inputId}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={fieldClass(Boolean(error))}
        {...inputProps}
      />
      {error && (
        <p id={errorId} className="text-sm text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}
