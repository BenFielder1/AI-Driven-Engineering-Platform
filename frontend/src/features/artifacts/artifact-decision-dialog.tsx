import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type RefObject } from 'react'
import { useForm } from 'react-hook-form'
import type { z } from 'zod'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage, errorReasons } from '../../lib/errors'
import { artifactTypeLabels } from '../projects/labels'
import { useProjectSources, type ProjectSources } from '../trackers/trackers-api'
import { isAgentAuthored } from './artifact-author'
import { decisionSchema } from './artifact-schemas'
import { useApproveArtifact, useRejectArtifact, type Artifact, type ArtifactType } from './artifacts-api'

type Values = z.infer<typeof decisionSchema>

/** What approving does to downstream artifacts (SPEC §4.4 invalidation cascade). */
const approveEffect: Record<ArtifactType, string> = {
  project_request: 'Any existing specification, plans and execution steps are invalidated.',
  technical_specification: 'Any existing implementation and optimisation plans and all execution steps are invalidated.',
  implementation_plan: 'Any optimisation plan and the steps of older plan versions are invalidated.',
  optimization_plan: 'Nothing else is invalidated.',
  codebase_summary: 'Nothing else is invalidated.',
  source_items: 'Nothing else is invalidated.',
}

export type ArtifactDecision = 'approve' | 'reject'

/** FR-173: this draft was built from an older `source_items` snapshot than the current one. */
const isBehindSources = (sources: ProjectSources | undefined, artifactId: string) =>
  sources?.draft?.artifactId === artifactId && sources.draft.behindSnapshot

/**
 * FR-17: approve or reject a draft (Owners), with an optional comment. Reject is a destructive confirmation
 * (alertdialog, SPEC §7). An agent-produced draft is regenerated with the rejection comment; a human edit isn't.
 * FR-173: approving a project request built from an older snapshot says so and asks to "Approve anyway".
 */
export function ArtifactDecisionDialog({
  projectId,
  artifact,
  action,
  onClose,
  onDecided,
  fallbackFocusRef,
}: {
  projectId: string
  artifact: Artifact
  action: ArtifactDecision
  onClose: () => void
  onDecided: (action: ArtifactDecision, artifact: Artifact) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}) {
  const approve = useApproveArtifact(projectId)
  const reject = useRejectArtifact(projectId)
  const mutation = action === 'approve' ? approve : reject
  const [serverError, setServerError] = useState<string | null>(null)
  const label = artifactTypeLabels[artifact.type].toLowerCase()
  const version = `v${String(artifact.version)}`
  const agent = isAgentAuthored(artifact.author)
  const checkSources = action === 'approve' && artifact.type === 'project_request'
  const sources = useProjectSources(projectId, artifact.releaseVersionId, checkSources)
  const behind = checkSources && isBehindSources(sources.data, artifact.id)
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<Values>({ resolver: zodResolver(decisionSchema), defaultValues: { comment: '' } })

  const onSubmit = handleSubmit(async ({ comment }) => {
    setServerError(null)
    if (checkSources && sources.data === undefined) {
      // Not loaded yet: don't approve past a warning that hasn't had the chance to show. If it's behind, it shows now.
      const latest = await sources.refetch()
      if (isBehindSources(latest.data, artifact.id)) {
        return
      }
    }
    mutation.mutate(
      { artifactId: artifact.id, comment: comment.trim() ? comment.trim() : null },
      {
        onSuccess: (decided) => {
          onDecided(action, decided)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['comment'])
          setServerError(
            unmatched.length > 0
              ? unmatched.join(' ')
              : [errorMessage(error, 'decideArtifact'), ...errorReasons(error)].join(' '),
          )
        },
      },
    )
  })

  const description =
    action === 'approve'
      ? `${version} is locked and becomes the current ${label}. ${approveEffect[artifact.type]}`
      : agent
        ? `${version} is marked rejected and the agent that produced it runs again with your feedback.`
        : `${version} is marked rejected and your comment is recorded. No agent runs for a human edit.`

  const commentId = `artifact-${artifact.id}-comment`
  return (
    <ModalDialog
      open
      role={action === 'reject' ? 'alertdialog' : 'dialog'}
      title={`${action === 'approve' ? 'Approve' : 'Reject'} ${label} ${version}?`}
      description={description}
      busy={mutation.isPending}
      fallbackFocusRef={fallbackFocusRef}
      onClose={onClose}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-4">
        <ErrorSummary
          title={`We couldn't ${action} this version`}
          errors={[
            ...(serverError ? [{ message: serverError }] : []),
            ...(isSubmitted ? fieldErrorSummary(errors, { comment: commentId }) : []),
          ]}
        />
        {behind && (
          <Alert tone="warning" title="Sources changed since this draft">
            <p>
              This draft was built from an older snapshot of the source items, so it doesn&apos;t reflect the latest import. You
              can run another round first, or approve it anyway.
            </p>
          </Alert>
        )}
        <TextAreaField
          id={commentId}
          label={action === 'approve' ? 'Comment (optional)' : 'Feedback (optional)'}
          hint={action === 'reject' && agent ? 'Tell the agent what to change.' : undefined}
          rows={4}
          error={errors.comment?.message}
          {...register('comment')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            variant={action === 'reject' ? 'danger' : 'primary'}
            pending={mutation.isPending}
            pendingLabel={action === 'approve' ? 'Approving…' : 'Rejecting…'}
          >
            {action === 'approve' ? (behind ? 'Approve anyway' : `Approve ${version}`) : `Reject ${version}`}
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
