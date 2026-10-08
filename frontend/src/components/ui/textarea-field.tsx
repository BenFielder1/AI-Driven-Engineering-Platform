import { useId, type Ref, type TextareaHTMLAttributes } from 'react'

export interface TextAreaFieldProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id'> {
  label: string
  hint?: string
  error?: string | undefined
  id?: string
  ref?: Ref<HTMLTextAreaElement>
}

/** A labelled `<textarea>` with hint and error text wired up like TextField. */
export function TextAreaField({ label, hint, error, id, className = '', ref, rows = 6, ...props }: TextAreaFieldProps) {
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
        <p id={hintId} className="text-sm text-muted">
          {hint}
        </p>
      )}
      <textarea
        ref={ref}
        id={inputId}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={
          'rounded-md border bg-canvas px-3 py-2 text-base text-fg ' + (error ? 'border-danger' : 'border-field-border')
        }
        {...props}
      />
      {error && (
        <p id={errorId} className="text-sm text-danger">
          {error}
        </p>
      )}
    </div>
  )
}
