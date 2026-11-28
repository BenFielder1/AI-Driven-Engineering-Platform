import { useState, type RefObject } from 'react'

import { ErrorSummary, type SummaryError } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { SelectField } from '../../components/ui/select-field'
import { useSubmitPipelineInput } from '../api/run-control-hooks'
import type { ArtifactDefinition, PipelineRun, RunArtifact } from '../api/types'
import { InputEditor } from '../schema-form/input-editor'
import { asSchema } from '../schema-form/json-schema'
import { runControlErrors } from './server-errors'
import { useInputEditors } from './use-input-editors'

export interface SubmitInputDialogProps {
  run: PipelineRun
  /** The definition's input artifacts (`input: true`), at least one. */
  inputs: readonly ArtifactDefinition[]
  /** The run's artifacts, to start on an input it has no version of yet. */
  artifacts: readonly RunArtifact[] | undefined
  onClose: () => void
  /** The API stored the version; `title` is the artifact's. */
  onSubmitted: (title: string) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}

/**
 * FR-362 Submit input: a run waiting on input takes a version of one of its input artifacts, in the same editor as
 * Start run (FR-335). Errors use the shared copy (`submitPipelineInput`).
 */
export function SubmitInputDialog({ run, inputs, artifacts, onClose, onSubmitted, fallbackFocusRef }: SubmitInputDialogProps) {
  const missing = inputs.find((a) => !artifacts?.some((r) => r.key === a.key && r.versions.length > 0))
  const [key, setKey] = useState((missing ?? inputs[0])?.key ?? '')
  const [summary, setSummary] = useState<SummaryError[]>([])
  const submit = useSubmitPipelineInput(run.id, run.projectId)
  const editors = useInputEditors()
  const artifact = inputs.find((a) => a.key === key)

  const onSubmit = async () => {
    setSummary([])
    if (!artifact) {
      return
    }
    const values = await editors.collect([artifact.key])
    if (!values || !(artifact.key in values)) {
      setSummary([{ message: 'The input needs attention. Its problems are shown below it.' }])
      return
    }
    submit.mutate(
      { artifactKey: artifact.key, content: values[artifact.key] },
      {
        onSuccess: () => {
          onSubmitted(artifact.title)
        },
        onError: (error) => {
          setSummary(runControlErrors(error, 'submitPipelineInput'))
        },
      },
    )
  }

  return (
    <ModalDialog
      open
      title="Submit input"
      description={`${run.pipelineTitle} is waiting on input. The run continues once it has what it needs.`}
      busy={submit.isPending}
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
        <ErrorSummary title="We couldn't submit the input" errors={summary} />
        {inputs.length > 1 && (
          <SelectField
            label="Input"
            value={key}
            onChange={(e) => {
              setKey(e.target.value)
              setSummary([])
            }}
          >
            {inputs.map((a) => (
              <option key={a.key} value={a.key}>
                {a.title}
              </option>
            ))}
          </SelectField>
        )}
        {artifact && (
          <InputEditor
            key={artifact.key}
            ref={editors.refFor(artifact.key)}
            idPrefix={`submit-input-${artifact.key}`}
            title={artifact.title}
            schema={asSchema(artifact.schema)}
            required
          />
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={submit.isPending}>
            Cancel
          </Button>
          <Button type="submit" disabled={!artifact} pending={submit.isPending} pendingLabel="Submitting…">
            Submit input
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
