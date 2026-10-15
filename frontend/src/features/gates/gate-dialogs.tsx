import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type RefObject } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import type { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { RadioGroup } from '../../components/ui/radio-group'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage } from '../../lib/errors'
import { escalationDecisionLabels, escalationOptionsFor, escalationReasonLabels, gateTypeLabels } from '../projects/labels'
import { approveEffect, rejectEffect } from './gate-effects'
import { commentSchema, escalationSchema } from './gate-schemas'
import { useApproveGate, useRejectGate, type Gate } from './gates-api'

type CommentValues = z.infer<typeof commentSchema>

interface DialogProps {
  projectId: string
  gate: Gate & { type: Exclude<Gate['type'], 'escalation'> }
  onClose: () => void
  onDecided: (message: string) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}

/**
 * FR-13: approve or reject a spec, plan or release gate, with an optional comment. Reject is a destructive
 * confirmation (alertdialog, SPEC §7).
 */
export function GateCommentDialog({ projectId, gate, action, onClose, onDecided, fallbackFocusRef }: DialogProps & { action: 'approve' | 'reject' }) {
  const approve = useApproveGate(projectId)
  const reject = useRejectGate(projectId)
  const mutation = action === 'approve' ? approve : reject
  const [serverError, setServerError] = useState<string | null>(null)
  const label = gateTypeLabels[gate.type]
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitted },
  } = useForm<CommentValues>({ resolver: zodResolver(commentSchema), defaultValues: { comment: '' } })

  const onSubmit = handleSubmit(({ comment }) => {
    setServerError(null)
    const body = { comment: comment.trim() ? comment.trim() : null }
    mutation.mutate(
      { gateId: gate.id, body },
      {
        onSuccess: () => {
          onDecided(`${label} ${action === 'approve' ? 'approved' : 'rejected'}.`)
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['comment'])
          setServerError(unmatched.length > 0 ? unmatched.join(' ') : errorMessage(error))
        },
      },
    )
  })

  const commentId = `gate-${gate.id}-comment`
  return (
    <ModalDialog
      open
      role={action === 'reject' ? 'alertdialog' : 'dialog'}
      title={`${action === 'approve' ? 'Approve' : 'Reject'} ${label.toLowerCase()}?`}
      description={action === 'approve' ? approveEffect[gate.type] : rejectEffect[gate.type]}
      busy={mutation.isPending}
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
          label={action === 'approve' ? 'Comment (optional)' : 'Feedback (optional)'}
          hint={action === 'reject' ? 'Tell the agent what to change.' : undefined}
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
            {action === 'approve' ? 'Approve' : 'Reject'}
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}

type EscalationValues = z.output<ReturnType<typeof escalationSchema>>
type EscalationFormValues = z.input<ReturnType<typeof escalationSchema>>

export function EscalationDialog({
  projectId,
  gate,
  currentCheckpoint,
  onClose,
  onDecided,
  fallbackFocusRef,
}: {
  projectId: string
  gate: Gate
  currentCheckpoint: number
  onClose: () => void
  onDecided: (message: string) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}) {
  const approve = useApproveGate(projectId)
  const [serverError, setServerError] = useState<string | null>(null)
  const ids = {
    decision: `gate-${gate.id}-decision`,
    targetCheckpoint: `gate-${gate.id}-target`,
    comment: `gate-${gate.id}-comment`,
  } as const
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<EscalationFormValues, unknown, EscalationValues>({
    resolver: zodResolver(escalationSchema(currentCheckpoint)),
    defaultValues: { decision: '', targetCheckpoint: '', comment: '' },
  })
  const decision = useWatch({ control, name: 'decision' })
  const target = useWatch({ control, name: 'targetCheckpoint' }).trim()
  // §7: rollback is destructive, so the dialog becomes an alertdialog with a danger submit naming the checkpoint.
  const isRollback = decision === 'rollback'
  const rollbackLabel = /^\d+$/.test(target) ? `Roll back to checkpoint ${target}` : 'Roll back'
  // FR-33: a blocked execution step can be retried (continue) or rolled back; accept does nothing there, so it's hidden.
  const stepBlocked = gate.escalationReason === 'step_blocked'
  const stepLabel = gate.stepNumber === null ? 'An execution step' : `Step ${String(gate.stepNumber)}`

  const onSubmit = handleSubmit((values) => {
    setServerError(null)
    const comment = values.comment.trim() ? values.comment.trim() : null
    const body = {
      decision: values.decision,
      comment,
      targetCheckpoint: values.decision === 'rollback' ? Number(values.targetCheckpoint.trim()) : null,
    }
    approve.mutate(
      { gateId: gate.id, body },
      {
        onSuccess: () => {
          onDecided(
            stepBlocked && values.decision === 'continue'
              ? `Escalation decided: ${stepLabel.toLowerCase()} will be retried.`
              : values.decision === 'rollback'
              ? `Escalation decided: roll back to checkpoint ${values.targetCheckpoint.trim()}.`
              : `Escalation decided: ${escalationDecisionLabels[values.decision].toLowerCase()}.`,
          )
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['targetCheckpoint', 'comment', 'decision'])
          setServerError(
            unmatched.length > 0
              ? unmatched.join(' ')
              : errorMessage(error, stepBlocked ? 'stepEscalationDecision' : undefined),
          )
        },
      },
    )
  })

  return (
    <ModalDialog
      open
      title="Decide the escalation"
      description={
        stepBlocked
          ? `${stepLabel} was rejected by the Execution Guardian on every attempt and is blocked. Continue retries it with fresh attempts; roll back returns the project to an earlier checkpoint.`
          : gate.escalationReason
            ? `Review needs a human decision before the project can continue. Reason: ${escalationReasonLabels[gate.escalationReason]}.`
            : 'Review needs a human decision before the project can continue.'
      }
      role={isRollback ? 'alertdialog' : 'dialog'}
      busy={approve.isPending}
      fallbackFocusRef={fallbackFocusRef}
      onClose={onClose}
    >
      <form noValidate onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-4">
        <ErrorSummary
          title="We couldn't record the decision"
          errors={[
            ...(serverError ? [{ message: serverError }] : []),
            ...(isSubmitted ? fieldErrorSummary(errors, ids) : []),
          ]}
        />
        <RadioGroup
          legend="Decision"
          id={ids.decision}
          options={escalationOptionsFor(gate.escalationReason)}
          inputProps={register('decision')}
          error={errors.decision?.message}
        />
        {decision === 'rollback' && (
          <TextField
            id={ids.targetCheckpoint}
            label="Roll back to checkpoint"
            hint={
              currentCheckpoint > 1
                ? `A checkpoint from 1 to ${String(currentCheckpoint - 1)} (the project is at checkpoint ${String(currentCheckpoint)}).`
                : 'The project is at checkpoint 1, so there is nothing earlier.'
            }
            inputMode="numeric"
            autoComplete="off"
            error={errors.targetCheckpoint?.message}
            {...register('targetCheckpoint')}
          />
        )}
        <TextAreaField
          id={ids.comment}
          label="Comment (optional)"
          rows={3}
          error={errors.comment?.message}
          {...register('comment')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={approve.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            variant={isRollback ? 'danger' : 'primary'}
            pending={approve.isPending}
            pendingLabel={isRollback ? 'Rolling back…' : 'Saving…'}
          >
            {isRollback ? rollbackLabel : 'Record decision'}
          </Button>
        </div>
      </form>
    </ModalDialog>
  )
}
