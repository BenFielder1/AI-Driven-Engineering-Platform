import { useId } from 'react'
import type { UseFormRegisterReturn } from 'react-hook-form'

import { ErrorState, LoadingState } from '../../components/ui/states'
import { TagList } from './preset-chip'
import { PresetDetails } from './preset-details'
import { deployTargetLabel, usePresets, type PresetSummary } from './presets-api'

/** The "No preset (general)" option's value (the field is '' for none). */
export const NO_PRESET = ''

const cardClass =
  'flex items-start gap-3 rounded-lg border border-border/60 bg-background/40 p-3 transition-colors has-checked:border-primary/60 has-checked:bg-primary/10'

function PresetCard({
  preset,
  inputId,
  inputProps,
}: {
  preset: PresetSummary
  inputId: string
  inputProps: UseFormRegisterReturn
}) {
  const descId = `${inputId}-desc`
  return (
    <div className={cardClass}>
      <input type="radio" id={inputId} value={preset.id} aria-describedby={descId} className="mt-1 size-4 accent-primary" {...inputProps} />
      <div className="flex min-w-0 flex-col gap-1.5">
        <label htmlFor={inputId} className="font-medium">
          {preset.name} <span className="font-mono text-xs text-muted-foreground tabular-nums">{preset.version}</span>
        </label>
        <p id={descId} className="text-sm text-muted-foreground">
          {preset.description}
        </p>
        <TagList label={`${preset.name} stack`} items={preset.stack} />
        {preset.deployTargets.length > 0 && (
          <p className="text-xs text-muted-foreground">
            Deploys to: {preset.deployTargets.map(deployTargetLabel).join(', ')}
          </p>
        )}
      </div>
    </div>
  )
}

/**
 * The wizard's Stack step (FR-74, SPEC §7.2): a card per preset (name, version, description, stack chips, deployment
 * targets) plus "No preset (general)", the default. The picked preset's specialist agents and extra spec sections show
 * below the cards.
 */
export function PresetPicker({
  id,
  selected,
  inputProps,
  error,
  hideNone = false,
}: {
  /** id of the first radio (the error-summary link target). */
  id: string
  selected: string
  inputProps: UseFormRegisterReturn
  error?: string | undefined
  /** FR-114: the wizard's Stack step has its own "No stack (general)", so its legacy presets list omits "No preset". */
  hideNone?: boolean
}) {
  const base = useId()
  const presets = usePresets()
  const errorId = error ? `${base}-error` : undefined
  return (
    <div className="flex flex-col gap-4">
      <fieldset className="flex flex-col gap-2" aria-describedby={errorId}>
        <legend className="mb-1 text-sm font-medium">Tech stack preset</legend>
        {!hideNone && (
          <div className={cardClass}>
            <input
              type="radio"
              id={id}
              value={NO_PRESET}
              aria-describedby={`${id}-desc`}
              className="mt-1 size-4 accent-primary"
              {...inputProps}
            />
            <div>
              <label htmlFor={id} className="font-medium">
                No preset (general)
              </label>
              <p id={`${id}-desc`} className="text-sm text-muted-foreground">
                Any stack: a generalist agent writes every plan step, and the specification has the standard sections.
              </p>
            </div>
          </div>
        )}
        {presets.isPending && <LoadingState label="Loading presets…" />}
        {presets.isError && (
          <ErrorState error={presets.error} title="We couldn't load the presets" onRetry={() => void presets.refetch()} />
        )}
        {presets.data?.map((p, index) => (
          <PresetCard
            key={p.id}
            preset={p}
            inputId={hideNone && index === 0 ? id : `${base}-${p.id}`}
            inputProps={inputProps}
          />
        ))}
        {error && (
          <p id={errorId} className="text-sm text-destructive">
            {error}
          </p>
        )}
      </fieldset>
      {selected !== NO_PRESET && (
        <section aria-label="What the preset brings" className="rounded-lg border border-border/60 bg-background/40 p-4">
          <PresetDetails presetId={selected} />
        </section>
      )}
    </div>
  )
}
