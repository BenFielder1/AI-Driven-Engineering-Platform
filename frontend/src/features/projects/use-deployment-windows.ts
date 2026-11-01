import { useEffect } from 'react'
import { useWatch, type Control, type UseFormGetValues, type UseFormSetValue } from 'react-hook-form'

import { useStackOptions } from '../stack/stack-api'
import { toStackRequest, type StackSelection } from '../stack/stack-model'
import type { WizardValues } from './schemas'
import { separatePartsOf } from './wizard-stack'

/**
 * FR-188, FR-189: the Deployment step's windows and caveats, always from `POST /api/stack/options` for the hosts and the
 * separate parts (D-V4-19). The answer's windows go in the form, for validation and the summary. An origin model a split
 * invalidates (R10) is cleared with the server's reason, and asked for again when the parts change: they may allow it.
 */
export function useDeploymentWindows({
  active,
  selection,
  control,
  getValues,
  setValue,
}: {
  active: boolean
  selection: StackSelection
  control: Control<WizardValues>
  getValues: UseFormGetValues<WizardValues>
  setValue: UseFormSetValue<WizardValues>
}) {
  const separateParts = separatePartsOf(useWatch({ control, name: 'stackSeparateParts' }))
  const hostWindows = useWatch({ control, name: 'hostWindows' })
  const originNote = useWatch({ control, name: 'hostOriginNote' })
  const options = useStackOptions(toStackRequest(selection, { withPaths: false, separateParts }), active)
  const answer = active && !options.isPlaceholderData ? options.data : undefined

  useEffect(() => {
    if (!answer) {
      return
    }
    setValue('hostWindows', JSON.stringify(answer.windows))
    const origin = answer.choices.find((c) => c.choice === 'originModel')
    const selected = origin?.options.find((o) => o.id === origin.selected)
    if (selected?.state === 'invalidated' || selected?.state === 'forbidden') {
      setValue('hostClearedOrigin', selected.id)
      setValue('hostOriginNote', selected.reasons[0]?.text ?? '')
      setValue('stackOriginModel', '')
    }
  }, [answer, setValue])

  const toggle = (part: string, on: boolean) => {
    const parts = on ? [...separateParts, part] : separateParts.filter((p) => p !== part)
    setValue('stackSeparateParts', parts.join(','), { shouldDirty: true })
    const cleared = getValues('hostClearedOrigin')
    if (cleared) {
      setValue('stackOriginModel', cleared)
      setValue('hostClearedOrigin', '')
      setValue('hostOriginNote', '')
    }
  }

  return {
    separateParts,
    hostWindows,
    originNote: originNote || null,
    caveats: answer?.caveats ?? [],
    toggle,
    settling: active && (options.isFetching || options.isPlaceholderData),
    loading: options.isPending || options.isPlaceholderData,
    loadError: options.isError ? options.error : null,
    retry: () => void options.refetch(),
  }
}
