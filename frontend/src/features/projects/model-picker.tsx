import { useState, type Ref } from 'react'

import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import type { LlmModel } from '../connections/connections-api'
import { formatNumber } from './labels'
import { limits } from './schemas'

/** The select value of "Other model id…" (never sent: the typed id is the field's value). */
export const OTHER_MODEL = '__other_model__'

/** "Claude Opus 5.5 · max output 128,000 tokens (recommended)" (FR-159). */
export const modelOptionLabel = (m: LlmModel) =>
  `${m.name} · max output ${formatNumber(m.maxOutputTokens)} tokens${m.recommended ? ' (recommended)' : ''}`

/** The catalog model of `id`, or `undefined` for an id that isn't in the catalog. */
export const catalogModel = (catalog: readonly LlmModel[], id: string) => catalog.find((m) => m.id === id)

interface ModelPickerProps {
  /** id of the select (the error-summary link target); the free-text field is `${id}-other`. */
  id: string
  label: string
  hint?: string | undefined
  catalog: readonly LlmModel[]
  /** The model id ('' = none / use the default). */
  value: string
  onChange: (value: string) => void
  onBlur?: () => void
  error?: string | undefined
  /** The label of the '' option (per-agent overrides: "Use the default model"); omitted, there is none. */
  emptyLabel?: string
  ref?: Ref<HTMLSelectElement>
}

/**
 * FR-159 (wizard) and the settings LLM tab: a picker over the provider's model catalog, each with its max output
 * tokens, plus "Other model id…" which reveals a free-text field. An id that isn't in the catalog shows as Other.
 */
export function ModelPicker({ id, label, hint, catalog, value, onChange, onBlur, error, emptyLabel, ref }: ModelPickerProps) {
  const [otherChosen, setOtherChosen] = useState(false)
  const inCatalog = catalogModel(catalog, value) !== undefined
  const other = otherChosen || (value !== '' && !inCatalog)
  const selectValue = other ? OTHER_MODEL : value
  const otherId = `${id}-other`

  return (
    <div className="flex flex-col gap-2">
      <SelectField
        ref={ref}
        id={id}
        label={label}
        hint={hint}
        error={other ? undefined : error}
        value={selectValue}
        onBlur={onBlur}
        onChange={(e) => {
          const next = e.target.value
          if (next === OTHER_MODEL) {
            setOtherChosen(true)
            // Keep a typed id; a catalog id becomes the starting point the user edits.
            onChange(value)
          } else {
            setOtherChosen(false)
            onChange(next)
          }
        }}
      >
        {emptyLabel !== undefined ? (
          <option value="">{emptyLabel}</option>
        ) : (
          selectValue === '' && (
            <option value="" disabled>
              Choose a model
            </option>
          )
        )}
        {catalog.map((m) => (
          <option key={m.id} value={m.id}>
            {modelOptionLabel(m)}
          </option>
        ))}
        <option value={OTHER_MODEL}>Other model id…</option>
      </SelectField>
      {other && (
        <TextField
          id={otherId}
          label={`${label}: model id`}
          hint={emptyLabel !== undefined ? 'Leave it empty to use the default model.' : 'Any model id the provider accepts.'}
          maxLength={limits.modelMax}
          autoComplete="off"
          spellCheck={false}
          value={value}
          error={error}
          onBlur={onBlur}
          onChange={(e) => {
            onChange(e.target.value)
          }}
        />
      )}
    </div>
  )
}
