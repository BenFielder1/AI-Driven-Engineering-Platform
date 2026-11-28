import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage, isApiError } from '../../lib/errors'
import {
  FEEDBACK_TEXT_MAX,
  useAgentFeedback,
  useCreateAgentFeedback,
  useDeleteAgentFeedback,
  type AgentFeedback,
} from '../api/feedback-hooks'
import type { PipelineDefinition } from '../api/types'
import { formatDateTime } from '../format'
import { canAddFeedback, canDeleteFeedback, type PipelineAccess } from '../run-control/access'
import { definitionAgentKeys } from './agent-keys'

const feedbackSchema = z.object({
  text: z
    .string()
    .trim()
    .min(1, { error: 'Enter the feedback.' })
    .max(FEEDBACK_TEXT_MAX, { error: `Feedback is at most ${String(FEEDBACK_TEXT_MAX)} characters.` }),
})

function AddFeedback({ projectId, pipelineId, agentKey }: { projectId: string; pipelineId: string; agentKey: string }) {
  const create = useCreateAgentFeedback(projectId, pipelineId, agentKey)
  const [serverError, setServerError] = useState<string | null>(null)
  const form = useForm<z.input<typeof feedbackSchema>, unknown, z.output<typeof feedbackSchema>>({
    resolver: zodResolver(feedbackSchema),
    defaultValues: { text: '' },
  })
  const onSubmit = form.handleSubmit(({ text }) => {
    setServerError(null)
    create.mutate(text, {
      onSuccess: () => {
        form.reset({ text: '' })
        toast.success('Feedback added. The agent is given it on its next run.')
      },
      onError: (error) => {
        const unmatched = applyFieldErrors(error, form.setError, ['text'])
        const onField = isApiError(error) && Object.keys(error.fieldErrors).length > unmatched.length
        setServerError(unmatched.length > 0 ? unmatched.join(' ') : onField ? null : errorMessage(error, 'agentFeedback'))
      },
    })
  })
  const id = `feedback-${agentKey}-text`
  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-2">
      <TextAreaField
        id={id}
        label="New feedback"
        hint="Tell this agent what to do differently. It's given to the agent on its next runs in this project."
        rows={3}
        error={form.formState.errors.text?.message}
        {...form.register('text')}
      />
      {serverError && (
        <p role="alert" className="text-sm text-destructive">
          {serverError}
        </p>
      )}
      <div>
        <Button type="submit" pending={create.isPending} pendingLabel="Adding…">
          Add feedback
        </Button>
      </div>
    </form>
  )
}

function FeedbackEntry({ entry, deletable, onDelete }: { entry: AgentFeedback; deletable: boolean; onDelete: () => void }) {
  return (
    <li className="flex flex-col gap-1 border-b border-border py-3 last:border-b-0">
      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <Badge>{entry.source === 'human' ? 'Person' : 'Stage'}</Badge>
        <span>{formatDateTime(entry.createdAt)}</span>
      </div>
      <p className="whitespace-pre-wrap text-sm">{entry.text}</p>
      {deletable && (
        <div>
          <Button variant="ghost" onClick={onDelete}>
            Delete
          </Button>
        </div>
      )}
    </li>
  )
}

function AgentFeedbackList({
  projectId,
  pipelineId,
  agentKey,
  access,
}: {
  projectId: string
  pipelineId: string
  agentKey: string
  access: PipelineAccess
}) {
  const feedback = useAgentFeedback(projectId, pipelineId, agentKey)
  const remove = useDeleteAgentFeedback(projectId)
  const [pending, setPending] = useState<AgentFeedback | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  let list
  if (feedback.isPending) {
    list = <LoadingState label="Loading feedback…" />
  } else if (feedback.isError) {
    list = <ErrorState error={feedback.error} onRetry={() => void feedback.refetch()} title="The feedback couldn't load" />
  } else if (feedback.data.length === 0) {
    list = <EmptyState title="No feedback yet">Feedback added for {agentKey} is listed here, newest first.</EmptyState>
  } else {
    list = (
      <ul aria-label={`Feedback for ${agentKey}`}>
        {feedback.data.map((entry) => (
          <FeedbackEntry
            key={entry.id}
            entry={entry}
            deletable={canDeleteFeedback(access, entry.createdBy)}
            onDelete={() => {
              setDeleteError(null)
              setPending(entry)
            }}
          />
        ))}
      </ul>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      {deleteError && (
        <p role="alert" className="text-sm text-destructive">
          {deleteError}
        </p>
      )}
      {list}
      {canAddFeedback(access) && <AddFeedback projectId={projectId} pipelineId={pipelineId} agentKey={agentKey} />}
      <ConfirmDialog
        open={pending !== null}
        title="Delete this feedback?"
        confirmLabel="Delete"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onCancel={() => {
          setPending(null)
        }}
        onConfirm={() => {
          if (!pending) {
            return
          }
          remove.mutate(pending.id, {
            onSuccess: () => {
              toast.success('Feedback deleted. It is no longer given to the agent.')
            },
            onError: (error) => {
              setDeleteError(errorMessage(error, 'agentFeedback'))
            },
            onSettled: () => {
              setPending(null)
            },
          })
        }}
      >
        <p>The agent stops being given it from its next run. This can’t be undone.</p>
      </ConfirmDialog>
    </div>
  )
}

/**
 * FR-365 Feedback: the pipeline's agents (from the definition, FR-305), each with its feedback entries. Contributors
 * and up add entries; Owners delete any, and authors their own.
 */
export function FeedbackSection({
  projectId,
  definition,
  access,
}: {
  projectId: string
  definition: PipelineDefinition
  access: PipelineAccess
}) {
  const agentKeys = definitionAgentKeys(definition)
  const [picked, setPicked] = useState<string | null>(null)
  const agentKey = picked !== null && agentKeys.includes(picked) ? picked : (agentKeys[0] ?? null)
  if (agentKey === null) {
    return <EmptyState title="No agents">This pipeline has no agent stages to give feedback to.</EmptyState>
  }
  return (
    <div className="flex flex-col gap-4">
      <SelectField
        label="Agent"
        className="max-w-sm"
        value={agentKey}
        onChange={(e) => {
          setPicked(e.target.value)
        }}
      >
        {agentKeys.map((key) => (
          <option key={key} value={key}>
            {key}
          </option>
        ))}
      </SelectField>
      <AgentFeedbackList
        key={agentKey}
        projectId={projectId}
        pipelineId={definition.pipelineId}
        agentKey={agentKey}
        access={access}
      />
    </div>
  )
}
