import { useRef, useState } from 'react'
import { toast } from 'sonner'

import { Button } from '../../components/ui/button'
import { usePipelines, useRunArtifacts } from '../api/hooks'
import type { ArtifactDefinition, PipelineDefinition, PipelineRun } from '../api/types'
import { canStartRuns, type PipelineAccess } from './access'
import { StartRunDialog } from './start-run-dialog'
import { SubmitInputDialog } from './submit-input-dialog'

/**
 * FR-362: the Start run button, for Contributors and up when the registry has an engine-controlled pipeline (`host`
 * pipelines, the SDLC, start from their own pages). A Viewer sees no button. A started run is landed on.
 */
export function StartRunAction(props: { projectId: string; access: PipelineAccess; onStarted: (runId: string) => void }) {
  return canStartRuns(props.access) ? <StartRunButton projectId={props.projectId} onStarted={props.onStarted} /> : null
}

function StartRunButton({ projectId, onStarted }: { projectId: string; onStarted: (runId: string) => void }) {
  const pipelines = usePipelines()
  const [open, setOpen] = useState(false)
  const opener = useRef<HTMLButtonElement>(null)
  const engine = (pipelines.data ?? []).filter((p) => p.runControl === 'engine')
  if (engine.length === 0) {
    return null
  }
  return (
    <>
      <Button
        ref={opener}
        onClick={() => {
          setOpen(true)
        }}
      >
        Start run
      </Button>
      {open && (
        <StartRunDialog
          projectId={projectId}
          pipelines={engine}
          fallbackFocusRef={opener}
          onClose={() => {
            setOpen(false)
          }}
          onStarted={(run) => {
            setOpen(false)
            toast.success(`${run.pipelineTitle} started.`)
            onStarted(run.id)
          }}
        />
      )}
    </>
  )
}

/** FR-362: Submit input on a run waiting on input (Contributor and up), with the same editor as Start run. */
export function SubmitInputAction({
  run,
  definition,
  access,
}: {
  run: PipelineRun
  definition: PipelineDefinition | undefined
  access: PipelineAccess
}) {
  const inputs = definition?.artifacts.filter((a) => a.input === true) ?? []
  if (run.status !== 'waiting_on_input' || !canStartRuns(access) || inputs.length === 0) {
    return null
  }
  return <SubmitInputButton run={run} inputs={inputs} />
}

function SubmitInputButton({ run, inputs }: { run: PipelineRun; inputs: readonly ArtifactDefinition[] }) {
  const [open, setOpen] = useState(false)
  const opener = useRef<HTMLButtonElement>(null)
  const artifacts = useRunArtifacts(run.id)
  return (
    <>
      <Button
        ref={opener}
        onClick={() => {
          setOpen(true)
        }}
      >
        Submit input
      </Button>
      {open && (
        <SubmitInputDialog
          run={run}
          inputs={inputs}
          artifacts={artifacts.data}
          fallbackFocusRef={opener}
          onClose={() => {
            setOpen(false)
          }}
          onSubmitted={(title) => {
            setOpen(false)
            toast.success(`${title} submitted. The run continues.`)
          }}
        />
      )}
    </>
  )
}
