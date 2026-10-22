import { useEffect, useRef } from 'react'
import type { UseFormRegisterReturn } from 'react-hook-form'

import { Button } from '../../../components/ui/button'
import { TextField } from '../../../components/ui/text-field'

/** What happens to a stored secret on save. */
export type SecretAction = 'keep' | 'replace' | 'clear'

interface SecretFieldProps {
  id: string
  /** e.g. "Access token" */
  label: string
  /** The API's set / not-set flag (FR-9). The value itself is never available to the UI. */
  isSet: boolean
  action: SecretAction
  onActionChange: (action: SecretAction) => void
  inputProps: UseFormRegisterReturn
  hint?: string
  error?: string | undefined
  maxLength?: number
  /** Offer "Clear" when set (default true). Slack/Teams webhook URLs can be replaced but never cleared. */
  allowClear?: boolean
  /** A value is required when none is saved (no "(optional)" in the label). */
  required?: boolean
  /** Read-only display shown next to "Set", e.g. a masked URL such as `hooks.slack.com/…`. */
  setDetail?: string | undefined
  /** Lower-case form of the label used inside sentences and button names (default: `label.toLowerCase()`). */
  nounPhrase?: string
}

/**
 * A write-only secret (FR-9): shows "Set" / "Not set", never a value. When set, it can be replaced or cleared;
 * a new value is entered in a password field. Focus follows the control that replaces the one just used.
 */
export function SecretField({
  id,
  label,
  isSet,
  action,
  onActionChange,
  inputProps,
  hint,
  error,
  maxLength,
  allowClear = true,
  required = false,
  setDetail,
  nounPhrase = label.toLowerCase(),
}: SecretFieldProps) {
  const replaceRef = useRef<HTMLButtonElement>(null)
  const undoRef = useRef<HTMLButtonElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const pendingFocus = useRef<'replace' | 'undo' | 'input' | null>(null)

  useEffect(() => {
    const target = pendingFocus.current
    pendingFocus.current = null
    if (target === 'replace') {
      replaceRef.current?.focus()
    } else if (target === 'undo') {
      undoRef.current?.focus()
    } else if (target === 'input') {
      inputRef.current?.focus()
    }
  }, [action])

  const change = (next: SecretAction, focus: 'replace' | 'undo' | 'input') => {
    pendingFocus.current = focus
    onActionChange(next)
  }

  const statusId = `${id}-status`
  const showInput = !isSet || action === 'replace'

  return (
    <div className="flex flex-col gap-2">
      <p id={statusId} className="text-sm">
        <span className="font-medium">{label}:</span>{' '}
        {action === 'clear' ? (
          <span className="text-destructive">will be removed when you save</span>
        ) : isSet ? (
          <>
            Set
            {setDetail && (
              <>
                {' '}
                (<code className="break-all">{setDetail}</code>)
              </>
            )}
          </>
        ) : (
          'Not set'
        )}
      </p>
      {isSet && action === 'keep' && (
        <div className="flex flex-wrap gap-2">
          <Button
            ref={replaceRef}
            variant="secondary"
            aria-label={`Replace ${nounPhrase}`}
            onClick={() => {
              change('replace', 'input')
            }}
          >
            Replace
          </Button>
          {allowClear && (
            <Button
              variant="secondary"
              aria-label={`Clear ${nounPhrase}`}
              onClick={() => {
                change('clear', 'undo')
              }}
            >
              Clear
            </Button>
          )}
        </div>
      )}
      {action === 'clear' && (
        <div>
          <Button
            ref={undoRef}
            variant="secondary"
            onClick={() => {
              change('keep', 'replace')
            }}
          >
            Keep {nounPhrase}
          </Button>
        </div>
      )}
      {showInput && action !== 'clear' && (
        <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
          <TextField
            {...inputProps}
            ref={(el) => {
              inputProps.ref(el)
              inputRef.current = el
            }}
            id={id}
            label={isSet ? `New ${nounPhrase}` : required ? label : `${label} (optional)`}
            hint={hint}
            error={error}
            type="password"
            autoComplete="new-password"
            maxLength={maxLength}
            className="flex-1"
            aria-required={required && !isSet ? true : undefined}
          />
          {isSet && (
            <Button
              variant="ghost"
              aria-label={`Cancel replacing ${nounPhrase}`}
              onClick={() => {
                change('keep', 'replace')
              }}
            >
              Cancel replacing
            </Button>
          )}
        </div>
      )}
    </div>
  )
}
