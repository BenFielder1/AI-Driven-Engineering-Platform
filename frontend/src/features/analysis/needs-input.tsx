import { useCallback, useState } from 'react'
import { toast } from 'sonner'

import { ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError } from '../../lib/errors'
import { useProjectAccess } from '../projects/project-context'
import type { StackCatalog } from '../stack/stack-api'
import { catalogOption, NONE, optionName } from '../stack/stack-model'
import { StageSection } from '../stages/stage-section'
import { useSaveStackProfile, type Analysis } from './analysis-api'
import { layerFieldId, profileFieldErrors } from './profile-editor'
import { LayerOptionSelect } from './profile-pickers'
import {
  layerLabel,
  layerStatusLabels,
  selectionOfProfile,
  withLayerChoice,
  withLayerPath,
  type EditableLayer,
  type StackProfileBlank,
  type StackProfileDocument,
} from './stack-profile-format'
import { useProfileOptions } from './use-profile-options'

const ID_PREFIX = 'needs-input'

const isEditable = (layer: string): layer is EditableLayer => layer === 'frontend' || layer === 'backend' || layer === 'data'

/** Why the layer needs input, in words (FR-181). */
function blankReason(catalog: StackCatalog, blank: StackProfileBlank): string {
  switch (blank.status) {
    case 'unknown':
      return 'The Stack Analyst couldn’t determine it.'
    case 'conflict':
      return `The evidence disagrees: ${blank.candidates.map((c) => `${c.option === NONE ? 'None' : optionName(catalog, blank.layer, c.option)} (${c.source === 'detector' ? 'the detector' : 'the agent'})`).join(' or ')}.`
    case 'unsupported':
      return `${blank.technology ?? 'This technology'} isn’t in the stack catalog. Pick the closest option, or continue with No stack (general).`
    default:
      return layerStatusLabels[blank.status]
  }
}

/**
 * FR-181 "Needs your input": every layer the agent couldn't decide (unknown, conflict, unsupported), first on the page as
 * a labelled list. Writers answer with an option picker filtered by `/api/stack/options`, a path field, and the agent's
 * question as the prompt; saving creates a new draft (FR-183). An unsupported layer also offers "Continue with No stack
 * (general)" to Owners (`onGeneralist`).
 */
export function NeedsInputSection({
  catalog,
  analysis,
  document,
  canEdit,
  canDecide,
  onGeneralist,
}: {
  catalog: StackCatalog
  analysis: Analysis
  document: StackProfileDocument
  canEdit: boolean
  canDecide: boolean
  onGeneralist: () => void
}) {
  const blanks = analysis.needsInput
  const profile = analysis.profile
  const [doc, setDoc] = useState(document)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [summary, setSummary] = useState<string[]>([])
  const { project } = useProjectAccess()
  const save = useSaveStackProfile(project.id)
  const onHostsCleared = useCallback((next: StackProfileDocument) => {
    setDoc(next)
  }, [])
  const options = useProfileOptions(catalog, canEdit ? doc : null, canEdit ? onHostsCleared : null)
  const sel = selectionOfProfile(doc)
  const answered = blanks.some((b) => isEditable(b.layer) && doc.layers[b.layer].status !== document.layers[b.layer].status)
  const unsupported = blanks.some((b) => b.status === 'unsupported')

  const onSubmit = () => {
    setSummary([])
    setFieldErrors({})
    if (!answered) {
      setSummary(['Answer at least one layer before saving.'])
      return
    }
    save.mutate(
      { narrative: profile?.narrative ?? '', document: doc, baseVersion: profile?.version ?? null },
      {
        onSuccess: (created) => {
          toast.success(`Saved as draft v${String(created.version)}.`)
        },
        onError: (error) => {
          const { byId, unmatched } = profileFieldErrors(error, ID_PREFIX)
          setFieldErrors(byId)
          setSummary(isApiError(error, 'VALIDATION') ? unmatched : [errorMessage(error, 'createArtifactDraft')])
        },
      },
    )
  }

  const summaryErrors: SummaryError[] = [
    ...summary.map((message) => ({ message })),
    ...Object.entries(fieldErrors).map(([fieldId, message]) => ({ fieldId, message })),
  ]

  const items = (
    <ol aria-label="Layers that need your input" className="flex flex-col gap-3">
      {blanks.map((blank) => {
        const layer = blank.layer
        const editable = canEdit && isEditable(layer)
        const questionId = `${ID_PREFIX}-${layer}-question`
        return (
          <li key={layer} data-testid={`needs-input-${layer}`} className="flex flex-col gap-2 rounded-lg border border-warning/50 bg-warning/5 p-3">
            <h4 className="font-medium">
              {layerLabel(layer)}: {layerStatusLabels[blank.status]}
            </h4>
            <p id={questionId} className="text-sm">
              {blank.question ? (
                <>
                  <span className="font-medium">The agent asks: </span>
                  {blank.question}
                </>
              ) : (
                blankReason(catalog, blank)
              )}
            </p>
            {blank.question && <p className="text-sm text-muted-foreground">{blankReason(catalog, blank)}</p>}
            {blank.candidates.length > 0 && (
              <ul aria-label={`${layerLabel(layer)} candidates`} className="list-disc pl-5 text-sm">
                {blank.candidates.map((c) => (
                  <li key={`${c.source}:${c.option}`}>
                    {c.option === NONE ? 'None' : optionName(catalog, layer, c.option)}
                    {c.path ? ` at ${c.path}` : ''} ({c.source === 'detector' ? 'detector' : 'agent'})
                    {c.evidence.length > 0 ? `: ${c.evidence.map((e) => `${e.file} (${e.text})`).join('; ')}` : ''}
                  </li>
                ))}
              </ul>
            )}
            {editable && isEditable(layer) && (
              <div className="grid gap-3 sm:grid-cols-2">
                <LayerOptionSelect
                  catalog={catalog}
                  choice={layer}
                  state={options.data?.choices.find((c) => c.choice === layer)}
                  id={layerFieldId(ID_PREFIX, layer)}
                  label={`${layerLabel(layer)} option`}
                  value={sel[layer]}
                  allowUndecided
                  error={fieldErrors[layerFieldId(ID_PREFIX, layer)]}
                  onChange={(option) => {
                    setFieldErrors({})
                    if (option === '') {
                      setDoc({ ...doc, layers: { ...doc.layers, [layer]: document.layers[layer] } })
                      return
                    }
                    const candidate = blank.candidates.find((c) => c.option === option)
                    setDoc(
                      withLayerChoice(doc, layer, option, {
                        path: candidate?.path ?? doc.layers[layer].path ?? (layer === 'frontend' ? 'frontend' : 'backend'),
                        mode: layer === 'data' ? (catalogOption(catalog, 'data', option)?.mode ?? null) : null,
                      }),
                    )
                  }}
                />
                {layer !== 'data' && sel[layer] !== '' && sel[layer] !== NONE && (
                  <TextField
                    id={layerFieldId(ID_PREFIX, layer, 'path')}
                    label={`${layerLabel(layer)} folder`}
                    hint="Relative to the repository root."
                    autoComplete="off"
                    spellCheck={false}
                    maxLength={200}
                    value={doc.layers[layer].path ?? ''}
                    error={fieldErrors[layerFieldId(ID_PREFIX, layer, 'path')]}
                    onChange={(e) => {
                      setDoc(withLayerPath(doc, layer, e.target.value))
                    }}
                  />
                )}
              </div>
            )}
          </li>
        )
      })}
    </ol>
  )

  return (
    <StageSection id="needs-input" title="Needs your input" className="border-warning/50">
      <p className="text-sm text-muted-foreground">
        The Stack Analyst never guesses. Answer these before the profile can be approved.
      </p>
      {canEdit ? (
        <form
          noValidate
          aria-label="Answer the layers that need input"
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            onSubmit()
          }}
        >
          <ErrorSummary title="We couldn't save your answers" errors={summaryErrors} />
          {items}
          <div className="flex flex-wrap gap-2">
            <Button type="submit" pending={save.isPending} pendingLabel="Saving…">
              Save answers as a new draft
            </Button>
          </div>
        </form>
      ) : (
        <>
          {items}
          <p className="text-sm text-muted-foreground">Only Contributors and Owners can answer these.</p>
        </>
      )}
      {unsupported && (
        <div className="flex flex-col gap-1 border-t border-border/60 pt-3">
          <p id="generalist-hint" className="text-sm">
            No close option? Continue with No stack (general): the generalist agent writes every plan step, and the profile
            is approved with no composition.
          </p>
          <div>
            <Button variant="secondary" disabled={!canDecide} aria-describedby="generalist-hint" onClick={onGeneralist}>
              Continue with No stack (general)
            </Button>
          </div>
          {!canDecide && <p className="text-sm text-muted-foreground">Only project Owners can choose this.</p>}
        </div>
      )}
    </StageSection>
  )
}
