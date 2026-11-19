import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type RefObject } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage } from '../../lib/errors'
import { useDecidePipelineGate } from '../api/gate-hooks'
import type { RunGate } from '../api/types'

/** DecidePipelineGateRequest.comment maxLength (the API's limit, as the project gate endpoints). */
export const GATE_DECISION_COMMENT_MAX = 4000

/** FR-307: approve and reject both take an optional comment (as the project gates, 0.1.0 FR-13). */
export const gateDecisionSchema = z.object({
  comment: z
    .string()
    .max(GATE_DECISION_COMMENT_MAX, { error: `Comments are at most ${String(GATE_DECISION_COMMENT_MAX)} characters.` }),
})

type Values = z.infer<typeof gateDecisionSchema>

/** What approving or rejecting a gate does, when the host knows (it owns the pipeline's rules). */
export interface GateDecisionCopy {
  /** The gate's name in the dialog title and toast; the definition's title when absent. */
  label?: string
  approveEffect?: string
  rejectEffect?: string
}

/** The engine's own descriptions, for gates the host has no copy for. */
export const defaultGateEffects = {
  approve: 'The run continues past this gate.',
  reject: 'The run doesn’t pass this gate. Your feedback is recorded with the decision and given to the next attempt.',
} as const

export interface GateDecisionProps {
  gate: RunGate
  action: 'approve' | 'reject'
  /** The gate's title from its definition and run (FR-305). */
  gateLabel: string
  copy?: GateDecisionCopy
  onClose: () => void
  /** Called after the API accepted the decision, with a sentence for a toast. */
  onDecided: (message: string) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}

/**
 * FR-307 GateDecision: approve or reject one pipeline gate through the engine endpoints, with the project gate dialogs'
 * copy. Reject is a destructive confirmation (alertdialog, SPEC §7). Errors use the shared copy (`pipelineGateDecision`).
 */
export function GateDecision({ gate, action, gateLabel, copy, onClose, onDecided, fallbackFocusRef }: GateDecisionProps) {
  const decide = useDecidePipelineGate()
  const [serverError, setServerError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<Values>({ resolver: zodResolver(gateDecisionSchema), defaultValues: { comment: '' } })
  const approve = action === 'approve'
  const label = copy?.label ?? gateLabel

  const onSubmit = handleSubmit(({ comment }) => {
    setServerError(null)
    const trimmed = comment.trim()
    decide.mutate(
      { runId: gate.runId, gateId: gate.id, action, comment: trimmed ? trimmed : null },
      {
        onSuccess: () => {
          onDecided(`${label} ${approve ? 'approved' : 'rejected'}.`)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['comment'])
          setServerError(unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error, 'pipelineGateDecision'))
        },
      },
    )
  })

  const commentId = `pipeline-gate-${gate.id}-comment`
  return (
    <ModalDialog
      open
      role={approve ? 'dialog' : 'alertdialog'}
      title={`${approve ? 'Approve' : 'Reject'} ${label.toLowerCase()}?`}
      description={
        approve ? (copy?.approveEffect ?? defaultGateEffects.approve) : (copy?.rejectEffect ?? defaultGateEffects.reject)
      }
      busy={decide.isPending}
      fallbackFocusRef={fallbackFocusRef}
      onClose={onClose}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-4">
        <ErrorSummary
          title={`We couldn't ${action} the gate`}
          errors={[
            ...(serverError ? [{ message: serverError }] : []),
            ...(isSubmitted ? fieldErrorSummary(errors, { comment: commentId }) : []),
          ]}
        />
        <TextAreaField
          id={commentId}
          label={approve ? 'Comment (optional)' : 'Feedback (optional)'}
          hint={approve ? undefined : 'Tell the agent what to change.'}
          rows={4}
          error={errors.comment?.message}
          {...register('comment')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={decide.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            variant={approve ? 'primary' : 'danger'}
            pending={decide.isPending}
            pendingLabel={approve ? 'Approving…' : 'Rejecting…'}
          >
            {approve ? 'Approve' : 'Reject'}
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
