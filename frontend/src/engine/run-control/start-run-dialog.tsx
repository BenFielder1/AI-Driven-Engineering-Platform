import { useState, type RefObject } from 'react'

import { ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { usePipelineDefinition } from '../api/hooks'
import { useStartPipelineRun, type ApprovalMode } from '../api/run-control-hooks'
import type { PipelineRun, PipelineSummary } from '../api/types'
import { InputEditor } from '../schema-form/input-editor'
import { asSchema } from '../schema-form/json-schema'
import { approvalModeLabels, runControlErrors } from './server-errors'
import { useInputEditors } from './use-input-editors'

const allModes: readonly ApprovalMode[] = ['strict', 'advisory', 'autonomous']
/** The approval mode select's value for "the definition's or project's default" (the request sends `null`). */
const DEFAULT_MODE = ''

export interface StartRunDialogProps {
  projectId: string
  /** The engine-controlled pipelines (FR-330); at least one. */
  pipelines: readonly PipelineSummary[]
  onClose: () => void
  /** The API created the run (the host lands on it). */
  onStarted: (run: PipelineRun) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}

/**
 * FR-362 Start run: a pipeline picker, an approval mode from the definition's list, and an editor per declared input
 * artifact rendered from its schema (an input may be left for later: the run then waits on input). Submitting starts
 * the run (FR-331); errors use the shared copy (`startPipelineRun`) with INPUT_INVALID's failing paths.
 */
export function StartRunDialog({ projectId, pipelines, onClose, onStarted, fallbackFocusRef }: StartRunDialogProps) {
  const [pipelineId, setPipelineId] = useState(pipelines[0]?.id ?? '')
  const [mode, setMode] = useState<ApprovalMode | typeof DEFAULT_MODE>(DEFAULT_MODE)
  const [summary, setSummary] = useState<SummaryError[]>([])
  const selected = pipelines.find((p) => p.id === pipelineId)
  const definition = usePipelineDefinition(selected?.id, selected?.hash)
  const start = useStartPipelineRun(projectId)
  const editors = useInputEditors()

  const inputs = definition.data?.artifacts.filter((a) => a.input === true) ?? []
  const allowed = definition.data?.approvalModes?.length ? definition.data.approvalModes : allModes
  const defaultMode = definition.data?.defaultApprovalMode ?? null

  const onSubmit = async () => {
    setSummary([])
    if (!definition.data) {
      return
    }
    const values = await editors.collect(inputs.map((a) => a.key))
    if (!values) {
      setSummary([{ message: 'Some inputs need attention. Their problems are shown below each one.' }])
      return
    }
    start.mutate(
      { pipelineId, approvalMode: mode === DEFAULT_MODE ? null : mode, inputs: values },
      {
        onSuccess: onStarted,
        onError: (error) => {
          setSummary(runControlErrors(error, 'startPipelineRun'))
        },
      },
    )
  }

  return (
    <ModalDialog
      open
      title="Start a run"
      description="The run starts at the pipeline's first stage. Inputs you leave blank can be submitted later."
      busy={start.isPending}
      fallbackFocusRef={fallbackFocusRef}
      onClose={onClose}
      className="sm:max-w-2xl"
    >
      <form
        noValidate
        onSubmit={(e) => {
          e.preventDefault()
          void onSubmit()
        }}
        className="flex flex-col gap-4"
      >
        <ErrorSummary title="We couldn't start the run" errors={summary} />
        <SelectField
          label="Pipeline"
          value={pipelineId}
          onChange={(e) => {
            setPipelineId(e.target.value)
            setMode(DEFAULT_MODE)
            setSummary([])
          }}
        >
          {pipelines.map((p) => (
            <option key={p.id} value={p.id}>
              {p.title}
            </option>
          ))}
        </SelectField>
        {definition.isPending ? (
          <LoadingState label="Loading the pipeline…" />
        ) : definition.isError ? (
          <ErrorState error={definition.error} onRetry={() => void definition.refetch()} title="The pipeline couldn't load" />
        ) : (
          <>
            <SelectField
              label="Approval mode"
              value={mode}
              onChange={(e) => {
                setMode(e.target.value as ApprovalMode | typeof DEFAULT_MODE)
              }}
            >
              <option value={DEFAULT_MODE}>
                {defaultMode ? `Default (${approvalModeLabels[defaultMode]})` : 'The project’s approval mode'}
              </option>
              {allowed.map((m) => (
                <option key={m} value={m}>
                  {approvalModeLabels[m]}
                </option>
              ))}
            </SelectField>
            {inputs.length === 0 ? (
              <p className="text-sm text-muted-foreground">This pipeline takes no inputs.</p>
            ) : (
              inputs.map((artifact) => (
                <InputEditor
                  key={`${pipelineId}:${artifact.key}`}
                  ref={editors.refFor(artifact.key)}
                  idPrefix={`start-run-${artifact.key}`}
                  title={artifact.title}
                  schema={asSchema(artifact.schema)}
                  required={false}
                />
              ))
            )}
          </>
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={start.isPending}>
            Cancel
          </Button>
          <Button type="submit" disabled={!definition.data} pending={start.isPending} pendingLabel="Starting…">
            Start run
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
