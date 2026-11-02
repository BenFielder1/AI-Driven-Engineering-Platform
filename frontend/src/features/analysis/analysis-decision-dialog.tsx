import { useId, useState, type RefObject } from 'react'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { TextAreaField } from '../../components/ui/textarea-field'
import { errorMessage, isApiError } from '../../lib/errors'
import type { Artifact } from '../artifacts/artifacts-api'
import { useProjectAccess } from '../projects/project-context'
import { useDecideStackProfile } from './analysis-api'

export type ProfileDecision = 'approve' | 'reject' | 'generalist'

const COMMENT_MAX = 4000

const copy: Record<ProfileDecision, { title: (v: string) => string; description: string; submit: string; pending: string }> = {
  approve: {
    title: (v) => `Approve stack profile ${v}?`,
    description:
      'It is locked, and its layers and hosts become the version’s pinned stack. Any existing project request, specification, plans and execution steps are invalidated.',
    submit: 'Approve',
    pending: 'Approving…',
  },
  reject: {
    title: (v) => `Reject stack profile ${v}?`,
    description: 'It is marked rejected, and the Stack Analyst runs again with your feedback.',
    submit: 'Reject',
    pending: 'Rejecting…',
  },
  generalist: {
    title: () => 'Continue with No stack (general)?',
    description:
      'The choice is recorded on a new version of the profile, which is locked with no stack composition. The generalist agent writes every plan step, and Refinement can start.',
    submit: 'Continue with No stack',
    pending: 'Approving…',
  },
}

/**
 * FR-181, FR-183: approve (optional comment), reject (feedback required: the Stack Analyst re-runs with it) or "Continue
 * with No stack (general)" (approve with `generalist`). STACK_PROFILE_INCOMPLETE and STACK_INCOMPATIBLE show the
 * server's layers and rule reasons.
 */
export function AnalysisDecisionDialog({
  artifactId,
  version,
  action,
  fallbackFocusRef,
  onClose,
  onDecided,
}: {
  artifactId: string
  version: number
  action: ProfileDecision
  fallbackFocusRef?: RefObject<HTMLElement | null>
  onClose: () => void
  onDecided: (action: ProfileDecision, artifact: Artifact) => void
}) {
  const { project } = useProjectAccess()
  const decide = useDecideStackProfile(project.id)
  const [comment, setComment] = useState('')
  const [commentError, setCommentError] = useState<string | null>(null)
  const [serverError, setServerError] = useState<string | null>(null)
  const commentId = `${useId()}-comment`
  const c = copy[action]
  const label = `v${String(version)}`

  const submit = () => {
    setServerError(null)
    const text = comment.trim()
    if (action === 'reject' && !text) {
      setCommentError('Tell the Stack Analyst what to change.')
      return
    }
    if (text.length > COMMENT_MAX) {
      setCommentError(`The comment is at most ${COMMENT_MAX.toLocaleString()} characters.`)
      return
    }
    setCommentError(null)
    decide.mutate(
      { action: action === 'reject' ? 'reject' : 'approve', artifactId, comment: text || null, generalist: action === 'generalist' },
      {
        onSuccess: (artifact) => {
          onDecided(action, artifact)
        },
        onError: (error) => {
          setServerError(
            isApiError(error, 'VALIDATION') && Object.keys(error.fieldErrors).length > 0
              ? Object.values(error.fieldErrors).flat().join(' ')
              : errorMessage(error, 'decideStackProfile'),
          )
        },
      },
    )
  }

  return (
    <ModalDialog
      open
      role={action === 'reject' ? 'alertdialog' : 'dialog'}
      title={c.title(label)}
      description={c.description}
      busy={decide.isPending}
      fallbackFocusRef={fallbackFocusRef}
      onClose={onClose}
    >
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          submit()
        }}
      >
        <ErrorSummary
          title={`We couldn't ${action === 'reject' ? 'reject' : 'approve'} the profile`}
          errors={[
            ...(serverError ? [{ message: serverError }] : []),
            ...(commentError ? [{ fieldId: commentId, message: commentError }] : []),
          ]}
        />
        <TextAreaField
          id={commentId}
          label={action === 'reject' ? 'Feedback for the Stack Analyst' : 'Comment (optional)'}
          hint={action === 'reject' ? 'Required. Say what it got wrong, for example "the backend is in api/, not server/".' : undefined}
          required={action === 'reject'}
          rows={4}
          value={comment}
          error={commentError ?? undefined}
          onChange={(e) => {
            setComment(e.target.value)
          }}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={decide.isPending}>
            Cancel
          </Button>
          <Button type="submit" variant={action === 'reject' ? 'danger' : 'primary'} pending={decide.isPending} pendingLabel={c.pending}>
            {action === 'generalist' ? c.submit : `${c.submit} ${label}`}
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
