import { useEffect, useRef } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { cn } from '../../lib/utils'
import { TagList } from './tag-list'
import { useStackOptions, type StackCatalog, type StackChoiceState, type StackDetection, type StackOptionState } from './stack-api'
import {
  applyOptions,
  catalogOption,
  NONE,
  optionName,
  splitOptions,
  stackLayerIds,
  toStackRequest,
  type StackChoiceId,
  type StackSelection,
} from './stack-model'

export type StackFieldErrors = Partial<Record<keyof StackSelection, string>>

const cardClass =
  'flex items-start gap-3 rounded-lg border border-border/60 bg-background/40 p-3 transition-colors has-checked:border-primary/60 has-checked:bg-primary/10'

/** The element id of a choice's first radio (the error-summary link target). */
export const stackChoiceFieldId = (idPrefix: string, choice: StackChoiceId) => `${idPrefix}-${choice}`
export const stackPathFieldId = (idPrefix: string, layer: 'frontend' | 'backend') => `${idPrefix}-${layer}-path`

/** The layers (sub-steps) of the catalog, in wizard order. */
export function stackLayers(catalog: StackCatalog | undefined): { id: string; name: string; description: string }[] {
  return catalog?.layers.length
    ? catalog.layers
    : stackLayerIds.map((id) => ({ id, name: id, description: '' }))
}

interface Detected {
  option: string
  evidence: readonly string[]
}

function OptionCard({
  catalog,
  choice,
  option,
  name,
  inputId,
  checked,
  disabled,
  detected,
  onSelect,
}: {
  catalog: StackCatalog
  choice: string
  option: StackOptionState
  name: string
  inputId: string
  checked: boolean
  disabled: boolean
  detected: Detected | undefined
  onSelect: () => void
}) {
  const card = catalogOption(catalog, choice, option.id)
  const label = card?.name ?? option.id
  const descId = `${inputId}-desc`
  const reasonId = `${inputId}-reason`
  const evidenceId = `${inputId}-evidence`
  const reasons = option.reasons.map((r) => r.text)
  const describedBy = [
    card?.description ? descId : null,
    reasons.length > 0 ? reasonId : null,
    detected ? evidenceId : null,
  ]
    .filter(Boolean)
    .join(' ')
  return (
    <div className={cn(cardClass, disabled && 'opacity-80')} data-option={option.id} data-state={option.state}>
      <input
        type="radio"
        id={inputId}
        name={name}
        value={option.id}
        checked={checked}
        // Forbidden options stay focusable (SPEC §7 UI rules) but never become selected.
        aria-disabled={disabled || undefined}
        aria-describedby={describedBy || undefined}
        className="mt-1 size-4 accent-primary"
        onClick={(e) => {
          if (disabled) {
            e.preventDefault()
          }
        }}
        onChange={() => {
          if (!disabled) {
            onSelect()
          }
        }}
      />
      <div className="flex min-w-0 flex-col gap-1.5">
        <div className="flex flex-wrap items-center gap-2">
          <label htmlFor={inputId} className="font-medium">
            {label}
          </label>
          {card?.packVersion && <span className="font-mono text-xs text-muted-foreground tabular-nums">{card.packVersion}</span>}
          {option.recommended && !disabled && <Badge tone="info">Recommended</Badge>}
          {detected && <Badge tone="success">Detected</Badge>}
        </div>
        {card?.description && (
          <p id={descId} className="text-sm text-muted-foreground">
            {card.description}
          </p>
        )}
        {card && card.chips.length > 0 && <TagList label={`${label} features`} items={card.chips} />}
        {reasons.length > 0 && (
          <p id={reasonId} className={cn('text-sm', disabled ? 'text-muted-foreground' : 'text-foreground')}>
            {disabled ? (
              <>
                <span className="sr-only">Not available: </span>
                {reasons.join(' · ')}
              </>
            ) : (
              <>
                <span aria-hidden>⚠ </span>
                <span className="sr-only">Caveat: </span>
                {reasons.join(' · ')}
              </>
            )}
          </p>
        )}
        {detected && (
          <div id={evidenceId} className="text-xs text-muted-foreground">
            <span>Detected from: </span>
            <ul aria-label={`Why ${label} was detected`} className="inline">
              {detected.evidence.map((e, i) => (
                <li key={e} className="inline">
                  {i > 0 ? '; ' : ''}
                  {e}
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </div>
  )
}

/** A choice of the current layer: its option cards (a radio group) and a collapsed list of the forbidden ones. */
function ChoiceGroup({
  catalog,
  state,
  selected,
  fieldId,
  detected,
  conflict,
  error,
  onSelect,
}: {
  catalog: StackCatalog
  state: StackChoiceState
  selected: string
  fieldId: string
  detected: Detected | undefined
  conflict: StackDetection['conflicts'][number] | undefined
  error: string | undefined
  onSelect: (option: string) => void
}) {
  const choice = catalog.choices.find((c) => c.id === state.choice)
  const legend = choice?.name ?? state.choice
  const { available, unavailable } = splitOptions(state)
  const errorId = error ? `${fieldId}-error` : undefined
  const name = optionName.bind(null, catalog, state.choice)

  // Deployment choices with nothing to decide (e.g. no backend, so no backend host) show as one line.
  if (state.layer === 'deploy' && available.every((o) => o.id === NONE) && unavailable.length > 0) {
    const fixed = available[0] ?? null
    const reason = unavailable.flatMap((o) => o.reasons.map((r) => r.text))[0]
    return (
      <p className="text-sm" data-choice={state.choice}>
        <span className="font-medium">{legend}:</span> {fixed ? name(fixed.id) : 'Not applicable'}
        {reason ? <span className="text-muted-foreground"> ({reason})</span> : null}
      </p>
    )
  }
  if (state.choice === 'originModel' && available.length <= 1) {
    const only = available[0]
    const explanation = only
      ? (catalogOption(catalog, state.choice, only.id)?.description ?? '')
      : (unavailable.flatMap((o) => o.reasons.map((r) => r.text))[0] ?? '')
    return (
      <div className="text-sm" data-choice={state.choice}>
        <p>
          <span className="font-medium">{legend}:</span> {only ? name(only.id) : 'None'}
        </p>
        {explanation && <p className="text-muted-foreground">{explanation}</p>}
      </div>
    )
  }

  return (
    <fieldset className="flex flex-col gap-2" aria-describedby={errorId} data-choice={state.choice}>
      <legend className="mb-1 font-medium">{legend}</legend>
      {conflict && (
        <Alert tone="warning" title={`Couldn't decide: ${conflict.candidates.map((c) => name(c.option)).join(' or ')}`}>
          <ul className="mt-1 list-disc pl-5">
            {conflict.candidates.map((c) => (
              <li key={c.option}>
                {name(c.option)}
                {c.path ? ` at ${c.path}` : ''}: {c.evidence.join('; ')}
              </li>
            ))}
          </ul>
          <p className="mt-1">Choose one below.</p>
        </Alert>
      )}
      {available.map((o, index) => (
        <OptionCard
          key={o.id}
          catalog={catalog}
          choice={state.choice}
          option={o}
          name={fieldId}
          inputId={index === 0 ? fieldId : `${fieldId}-${o.id}`}
          checked={selected === o.id}
          disabled={false}
          detected={detected?.option === o.id ? detected : undefined}
          onSelect={() => {
            onSelect(o.id)
          }}
        />
      ))}
      {unavailable.length > 0 && (
        <details className="rounded-lg border border-dashed border-border/60 p-2">
          <summary className="min-h-6 cursor-pointer text-sm font-medium">
            Not available with your choices ({unavailable.length})
          </summary>
          <div className="mt-2 flex flex-col gap-2">
            {unavailable.map((o) => (
              <OptionCard
                key={o.id}
                catalog={catalog}
                choice={state.choice}
                option={o}
                name={`${fieldId}-unavailable`}
                inputId={`${fieldId}-${o.id}`}
                checked={false}
                disabled
                detected={detected?.option === o.id ? detected : undefined}
                onSelect={() => undefined}
              />
            ))}
          </div>
        </details>
      )}
      {error && (
        <p id={errorId} className="text-sm text-destructive">
          {error}
        </p>
      )}
    </fieldset>
  )
}

/**
 * The Stack step's five sub-steps (FR-114, FR-115): the option cards of the current layer, rendered from `POST
 * /api/stack/options` with the choices so far. A later choice the server reports as invalidated is cleared, with a
 * toast naming it and why. The VCS layer is read-only when the repository fixes it (R5).
 * Detection (FR-118) marks the detected options and shows the undecided choices.
 */
export function StackComposer({
  catalog,
  value,
  onChange,
  subStep,
  onSubStepChange,
  idPrefix,
  vcsFixed,
  vcsFixedReason,
  detection,
  errors = {},
}: {
  catalog: StackCatalog
  value: StackSelection
  onChange: (next: StackSelection) => void
  subStep: number
  onSubStepChange: (next: number) => void
  idPrefix: string
  /** The VCS option set by the repository (read-only), or null when the user picks it. */
  vcsFixed: string | null
  vcsFixedReason?: string
  detection?: StackDetection | null | undefined
  errors?: StackFieldErrors
}) {
  const layers = stackLayers(catalog)
  const layer = layers[Math.min(subStep, layers.length - 1)]
  const options = useStackOptions(toStackRequest(value, { withPaths: false }))
  const processed = useRef<unknown>(null)
  const onDeploy = layer?.id === 'deploy'

  // R5: the repository's host is the VCS layer.
  useEffect(() => {
    if (vcsFixed && value.vcs !== vcsFixed) {
      onChange({ ...value, vcs: vcsFixed })
    }
  }, [vcsFixed, value, onChange])

  // FR-114: clear what the server now reports as invalidated, and preselect the Deployment defaults.
  useEffect(() => {
    if (!options.data || options.isPlaceholderData) {
      return
    }
    const marker = `${String(options.dataUpdatedAt)}:${String(onDeploy)}:${JSON.stringify(value)}`
    if (processed.current === marker) {
      return
    }
    processed.current = marker
    const { selection, cleared } = applyOptions(value, options.data, catalog, { fillDefaults: onDeploy })
    // The toast region is a polite live region, so screen readers hear what was cleared too.
    for (const c of cleared) {
      toast.warning(c.message)
    }
    if (selection !== value) {
      onChange(selection)
    }
  }, [options.data, options.isPlaceholderData, options.dataUpdatedAt, value, catalog, onChange, onDeploy])

  const detectedOf = (choice: string): Detected | undefined => {
    const s = detection?.suggestions.find((d) => d.choice === choice)
    return s ? { option: s.option, evidence: s.evidence } : undefined
  }
  const conflictOf = (choice: string) => detection?.conflicts.find((c) => c.choice === choice)

  const select = (choice: StackChoiceId, option: string) => {
    onChange({ ...value, [choice]: option })
  }

  const choiceStates = options.data?.choices.filter((c) => c.layer === layer?.id) ?? []

  return (
    <div className="flex flex-col gap-4">
      <ol aria-label="Stack layers" className="flex flex-wrap gap-2 text-sm">
        {layers.map((l, index) => (
          <li key={l.id} aria-current={index === subStep ? 'step' : undefined}>
            {index < subStep ? (
              <button
                type="button"
                className="min-h-9 rounded-full border border-border/60 bg-card/60 px-3 underline-offset-4 hover:underline"
                onClick={() => {
                  onSubStepChange(index)
                }}
              >
                {index + 1}. {l.name}
              </button>
            ) : (
              <span
                className={cn(
                  'flex min-h-9 items-center rounded-full border px-3',
                  index === subStep
                    ? 'border-primary/60 bg-primary/10 font-medium'
                    : 'border-dashed border-border/60 text-muted-foreground',
                )}
              >
                {index + 1}. {l.name}
              </span>
            )}
          </li>
        ))}
      </ol>

      {layer && (
        <section aria-labelledby={`${idPrefix}-layer-title`} className="flex flex-col gap-4">
          <div>
            <h3 id={`${idPrefix}-layer-title`} className="text-lg">
              {layer.name}
            </h3>
            {layer.description && <p className="text-sm text-muted-foreground">{layer.description}</p>}
          </div>

          {options.isPending && <LoadingState label="Loading the options…" />}
          {options.isError && (
            <ErrorState error={options.error} title="The stack options couldn't load" onRetry={() => void options.refetch()} />
          )}

          {layer.id === 'vcs' && vcsFixed ? (
            <div className={cardClass} data-choice="vcs">
              <div>
                <p className="font-medium">{optionName(catalog, 'vcs', vcsFixed)}</p>
                <p className="text-sm text-muted-foreground">
                  {vcsFixedReason ?? 'Set from the repository you chose. It can only be the repository’s host.'}
                </p>
              </div>
            </div>
          ) : (
            choiceStates.map((state) => {
              const choice = state.choice as StackChoiceId
              return (
                <ChoiceGroup
                  key={state.choice}
                  catalog={catalog}
                  state={state}
                  selected={value[choice]}
                  fieldId={stackChoiceFieldId(idPrefix, choice)}
                  detected={detectedOf(state.choice)}
                  conflict={conflictOf(state.choice)}
                  error={errors[choice]}
                  onSelect={(option) => {
                    select(choice, option)
                  }}
                />
              )
            })
          )}

          {layer.id === 'frontend' && value.frontend && value.frontend !== NONE && (
            <TextField
              id={stackPathFieldId(idPrefix, 'frontend')}
              label="Frontend folder"
              hint="Where the frontend lives in the repository, relative to its root (for example frontend or apps/web). Use . for the root when there's no backend."
              autoComplete="off"
              spellCheck={false}
              maxLength={200}
              value={value.frontendPath}
              error={errors.frontendPath}
              onChange={(e) => {
                onChange({ ...value, frontendPath: e.target.value })
              }}
            />
          )}
          {layer.id === 'backend' && value.backend && value.backend !== NONE && (
            <TextField
              id={stackPathFieldId(idPrefix, 'backend')}
              label="Backend folder"
              hint="Where the backend lives in the repository, relative to its root (for example backend or api). Use . for the root when there's no frontend."
              autoComplete="off"
              spellCheck={false}
              maxLength={200}
              value={value.backendPath}
              error={errors.backendPath}
              onChange={(e) => {
                onChange({ ...value, backendPath: e.target.value })
              }}
            />
          )}
        </section>
      )}
    </div>
  )
}
