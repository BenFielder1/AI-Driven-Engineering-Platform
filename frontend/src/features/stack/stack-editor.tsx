import { useCallback, useId, useState } from 'react'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import type { StackCatalog } from './stack-api'
import { StackComposer, stackChoiceFieldId, stackLayers, stackPathFieldId, type StackFieldErrors } from './stack-composer'
import { requiredChoices, type StackChoiceId, type StackSelection } from './stack-model'
import { StackSummary } from './stack-summary'

/** "Choose a frontend (or None)." for a required choice that is still empty. */
export function missingChoiceMessage(catalog: StackCatalog | undefined, choice: StackChoiceId): string {
  const name = catalog?.choices.find((c) => c.id === choice)?.name ?? choice
  return choice === 'vcs' ? `Choose the ${name.toLowerCase()} host.` : `Choose the ${name.toLowerCase()}, or None.`
}

/** The required choices of a layer that aren't made yet. */
export function missingChoices(catalog: StackCatalog | undefined, value: StackSelection, layer: string | undefined): StackFieldErrors {
  const errors: StackFieldErrors = {}
  for (const choice of requiredChoices) {
    const choiceLayer = catalog?.choices.find((c) => c.id === choice)?.layer ?? choice
    if ((layer === undefined || choiceLayer === layer) && value[choice] === '') {
      errors[choice] = missingChoiceMessage(catalog, choice)
    }
  }
  return errors
}

/** The error-summary entries of stack field errors (each linked to its field). */
export function stackErrorSummary(idPrefix: string, errors: StackFieldErrors) {
  return Object.entries(errors)
    .filter((e): e is [keyof StackSelection, string] => Boolean(e[1]))
    .map(([field, message]) => ({
      message,
      fieldId:
        field === 'frontendPath'
          ? stackPathFieldId(idPrefix, 'frontend')
          : field === 'backendPath'
            ? stackPathFieldId(idPrefix, 'backend')
            : stackChoiceFieldId(idPrefix, field),
    }))
}

/**
 * "Change stack" (FR-117): the same five sub-steps, pre-filled, with Previous / Next and the summary card, then the
 * caller's submit (Settings → Stack saves; the start-version dialog keeps the choice for its request).
 */
export function StackEditor({
  catalog,
  initial,
  vcsFixed,
  submitLabel,
  pending = false,
  serverErrors = {},
  onSubmit,
  onCancel,
}: {
  catalog: StackCatalog
  initial: StackSelection
  vcsFixed: string | null
  submitLabel: string
  pending?: boolean
  serverErrors?: StackFieldErrors
  onSubmit: (value: StackSelection) => void
  onCancel: () => void
}) {
  const idPrefix = useId()
  const [value, setValue] = useState(initial)
  const [subStep, setSubStep] = useState(0)
  const [errors, setErrors] = useState<StackFieldErrors>({})
  const [liveErrors, setLiveErrors] = useState<StackFieldErrors>({})
  const layers = stackLayers(catalog)
  const last = subStep >= layers.length - 1
  const shown = { ...liveErrors, ...serverErrors, ...errors }
  const onFieldErrors = useCallback((e: StackFieldErrors) => {
    setLiveErrors(e)
  }, [])

  const next = () => {
    const missing = missingChoices(catalog, value, last ? undefined : layers[subStep]?.id)
    setErrors(missing)
    if (Object.keys(missing).length > 0) {
      return
    }
    if (last) {
      onSubmit(value)
    } else {
      setSubStep(subStep + 1)
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <ErrorSummary title="Check the stack" errors={stackErrorSummary(idPrefix, errors)} />
      <StackComposer
        catalog={catalog}
        value={value}
        onChange={(v) => {
          setErrors({})
          setValue(v)
        }}
        subStep={subStep}
        onSubStepChange={setSubStep}
        idPrefix={idPrefix}
        vcsFixed={vcsFixed}
        errors={shown}
      />
      <StackSummary catalog={catalog} value={value} onFieldErrors={onFieldErrors} />
      <div className="flex flex-wrap justify-between gap-2">
        <div className="flex gap-2">
          <Button variant="secondary" onClick={onCancel} disabled={pending}>
            Cancel
          </Button>
          {subStep > 0 && (
            <Button
              variant="secondary"
              onClick={() => {
                setSubStep(subStep - 1)
              }}
            >
              Previous layer
            </Button>
          )}
        </div>
        <Button onClick={next} pending={pending} pendingLabel="Saving…">
          {last ? submitLabel : 'Next layer'}
        </Button>
      </div>
    </div>
  )
}
