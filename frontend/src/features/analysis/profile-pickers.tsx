import { SelectField } from '../../components/ui/select-field'
import type { StackCatalog, StackChoiceState, StackOptions } from '../stack/stack-api'
import { NONE, optionName } from '../stack/stack-model'
import { selectionOfProfile, type HostChoice, type StackProfileDocument } from './stack-profile-format'

const UNDECIDED = ''

/** The option ids of a choice: from `/api/stack/options` when loaded (D-V4-19), else the catalog's. */
function optionsOf(catalog: StackCatalog, choice: string, state: StackChoiceState | undefined) {
  if (state) {
    const available = state.options.filter((o) => o.state === 'allowed' || o.state === 'caveat')
    const unavailable = state.options.filter((o) => o.state === 'forbidden' || o.state === 'invalidated')
    return { available, unavailable }
  }
  const all = catalog.choices.find((c) => c.id === choice)?.options ?? []
  return { available: all.map((o) => ({ id: o.id, state: 'allowed' as const, reasons: [], recommended: false })), unavailable: [] }
}

/** Why the chosen option isn't allowed with the other choices (the server's reason, e.g. R3), as visible text. */
export function unavailableReason(catalog: StackCatalog, state: StackChoiceState | undefined, value: string): string | undefined {
  const option = state?.options.find((o) => o.id === value)
  if (!option || (option.state !== 'forbidden' && option.state !== 'invalidated')) {
    return undefined
  }
  const reasons = option.reasons.map((r) => `${r.text.replace(/\.$/, '')} (${r.rule})`)
  return `${optionName(catalog, state?.choice ?? '', value)} isn't available with your other choices: ${reasons.join('; ') || 'a compatibility rule forbids it'}.`
}

/**
 * FR-181, FR-183: the option picker of a layer, filtered by `/api/stack/options` (the profile keeps no compatibility logic,
 * D-V4-19). Options the other choices rule out are listed apart and can't be picked; a chosen one that became unavailable
 * stays shown with the server's reason. `allowUndecided` keeps "Not decided yet" for a blank the user hasn't answered.
 */
export function LayerOptionSelect({
  catalog,
  choice,
  state,
  id,
  label,
  hint,
  value,
  error,
  disabled = false,
  allowUndecided = false,
  onChange,
}: {
  catalog: StackCatalog
  choice: string
  state: StackChoiceState | undefined
  id: string
  label: string
  hint?: string
  value: string
  error?: string | undefined
  disabled?: boolean
  allowUndecided?: boolean
  onChange: (option: string) => void
}) {
  const { available, unavailable } = optionsOf(catalog, choice, state)
  const name = (option: string) => optionName(catalog, choice, option)
  const realOptions = available.filter((o) => o.id !== NONE)
  const hasNone = available.some((o) => o.id === NONE) || unavailable.some((o) => o.id === NONE)
  const stale = value !== UNDECIDED && value !== NONE && !realOptions.some((o) => o.id === value) ? value : null
  return (
    <SelectField
      id={id}
      label={label}
      hint={hint}
      value={value}
      disabled={disabled}
      error={error ?? unavailableReason(catalog, state, value)}
      onChange={(e) => {
        onChange(e.target.value)
      }}
    >
      {(allowUndecided || value === UNDECIDED) && <option value={UNDECIDED}>Not decided yet</option>}
      {realOptions.map((o) => (
        <option key={o.id} value={o.id}>
          {name(o.id)}
          {o.state === 'caveat' ? ' (with a caveat)' : ''}
        </option>
      ))}
      {stale && <option value={stale}>{name(stale)} (not available)</option>}
      {hasNone && <option value={NONE}>None</option>}
      {unavailable.filter((o) => o.id !== NONE && o.id !== stale).length > 0 && (
        <optgroup label="Not available with your choices">
          {unavailable
            .filter((o) => o.id !== NONE && o.id !== stale)
            .map((o) => (
              <option key={o.id} value={o.id} disabled>
                {name(o.id)}: {o.reasons[0]?.text ?? 'not allowed'}
              </option>
            ))}
        </optgroup>
      )}
    </SelectField>
  )
}

/**
 * FR-184, SPEC §7 UI rules: the hosts the other choices rule out, each with the server's reason as visible text (not only
 * inside the closed picker), e.g. "Railway: Railway only builds from GitHub (R9)".
 */
function UnavailableHosts({ catalog, choice, label, state }: { catalog: StackCatalog; choice: string; label: string; state: StackChoiceState | undefined }) {
  const unavailable = (state?.options ?? []).filter((o) => o.id !== NONE && (o.state === 'forbidden' || o.state === 'invalidated') && o.id !== state?.selected)
  if (unavailable.length === 0) {
    return null
  }
  return (
    <ul aria-label={`${label}: not available`} className="text-xs text-muted-foreground">
      {unavailable.map((o) => (
        <li key={o.id}>
          <span className="font-medium">{optionName(catalog, choice, o.id)}</span> isn&apos;t available:{' '}
          {o.reasons.map((r) => `${r.text.replace(/\.$/, '')} (${r.rule})`).join('; ') || 'a compatibility rule forbids it'}
        </li>
      ))}
    </ul>
  )
}

/** The host choices with their wording in the Hosts section (FR-184). */
export const hostPickerChoices = ['frontendHost', 'backendHost', 'databaseHost', 'local', 'originModel'] as const
export type HostPickerChoice = (typeof hostPickerChoices)[number]

export const hostPickerFieldId = (idPrefix: string, choice: HostPickerChoice) => `${idPrefix}-${choice}`

/**
 * FR-184: the frontend, backend and database hosts, the local dev loop and the origin model, each filtered by
 * `/api/stack/options` for the profile's layers. Hosts from the detector's evidence are marked "Detected". `disabled`
 * comes with its visible reason from the caller (the fieldset around these).
 */
export function HostPickers({
  catalog,
  options,
  doc,
  idPrefix,
  disabled,
  errors = {},
  onChange,
}: {
  catalog: StackCatalog
  options: StackOptions | undefined
  doc: StackProfileDocument
  idPrefix: string
  disabled: boolean
  errors?: Partial<Record<HostPickerChoice, string>>
  onChange: (choice: HostPickerChoice, option: string) => void
}) {
  const sel = selectionOfProfile(doc)
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      {hostPickerChoices.map((choice) => {
        const state = options?.choices.find((c) => c.choice === choice)
        const label = catalog.choices.find((c) => c.id === choice)?.name ?? choice
        const detected = doc.hosts.detected.includes(choice) && doc.hosts[choice as HostChoice] !== null
        const value = sel[choice]
        const defaulted = (choice === 'local' || choice === 'originModel') && value === ''
        return (
          <div key={choice} data-choice={choice} className="flex flex-col gap-1">
            {choice === 'local' || choice === 'originModel' ? (
              <SelectField
                id={hostPickerFieldId(idPrefix, choice)}
                label={label}
                value={value}
                disabled={disabled}
                hint={defaulted ? `Default: ${state?.default ? optionName(catalog, choice, state.default) : 'chosen by Tevrin'}` : undefined}
                error={errors[choice] ?? unavailableReason(catalog, state, value)}
                onChange={(e) => {
                  onChange(choice, e.target.value)
                }}
              >
                <option value="">Default</option>
                {optionsOf(catalog, choice, state).available.map((o) => (
                  <option key={o.id} value={o.id}>
                    {optionName(catalog, choice, o.id)}
                  </option>
                ))}
              </SelectField>
            ) : (
              <LayerOptionSelect
                catalog={catalog}
                choice={choice}
                state={state}
                id={hostPickerFieldId(idPrefix, choice)}
                label={label}
                hint={detected ? 'Detected: found in the repository.' : undefined}
                value={value}
                error={errors[choice]}
                disabled={disabled}
                onChange={(option) => {
                  onChange(choice, option)
                }}
              />
            )}
            {choice !== 'local' && choice !== 'originModel' && <UnavailableHosts catalog={catalog} choice={choice} label={label} state={state} />}
          </div>
        )
      })}
    </div>
  )
}
