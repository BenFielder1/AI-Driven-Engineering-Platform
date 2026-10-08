import { useId } from 'react'
import type { UseFormRegisterReturn } from 'react-hook-form'

export interface RadioOption {
  value: string
  label: string
  description?: string
}

interface RadioGroupProps {
  legend: string
  options: readonly RadioOption[]
  /** `register('field')` from React Hook Form, spread onto every radio. */
  inputProps: UseFormRegisterReturn
  /** id of the first radio (the error-summary link target). */
  id?: string
  error?: string | undefined
  disabled?: boolean
}

/** A fieldset of labelled radios; each option's description is tied to its radio with aria-describedby. */
export function RadioGroup({ legend, options, inputProps, id, error, disabled }: RadioGroupProps) {
  const base = useId()
  const errorId = error ? `${base}-error` : undefined
  return (
    <fieldset className="flex flex-col gap-2" aria-describedby={errorId} disabled={disabled}>
      <legend className="mb-1 text-sm font-medium">{legend}</legend>
      {options.map((option, index) => {
        const inputId = index === 0 && id ? id : `${base}-${option.value}`
        const descId = option.description ? `${inputId}-desc` : undefined
        return (
          <div key={option.value} className="flex items-start gap-2 rounded-md border border-border p-3">
            <input
              type="radio"
              id={inputId}
              value={option.value}
              aria-describedby={descId}
              className="mt-1 size-4"
              {...inputProps}
            />
            <div>
              <label htmlFor={inputId} className="font-medium">
                {option.label}
              </label>
              {option.description && (
                <p id={descId} className="text-sm text-muted">
                  {option.description}
                </p>
              )}
            </div>
          </div>
        )
      })}
      {error && (
        <p id={errorId} className="text-sm text-danger">
          {error}
        </p>
      )}
    </fieldset>
  )
}
