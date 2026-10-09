import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type RefObject } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import type { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { Dialog } from '../../components/ui/dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { RadioGroup } from '../../components/ui/radio-group'
import { TextAreaField } from '../../components/ui/textarea-field'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage } from '../../lib/errors'
import { escalationDecisionLabels, escalationOptionsFor, gateTypeLabels, phaseLabels } from '../projects/labels'
import { commentSchema, escalationSchema } from './gate-schemas'
import { useApproveGate, useRejectGate, type Gate } from './gates-api'

type CommentValues = z.infer<typeof commentSchema>

/** What approving each non-escalation gate does (SPEC §4.2). */
const approveEffect: Record<Exclude<Gate['type'], 'escalation'>, string> = {
  spec_approval: `The project moves to ${phaseLabels.planning}.`,
  plan_approval: `The project moves to ${phaseLabels.execution}.`,
  pre_release: `The project moves to ${phaseLabels.release} and the release starts.`,
}

/** What rejecting does (FR-13, §4.2: the producing agent re-runs with the comment as feedback). */
const rejectEffect: Record<Exclude<Gate['type'], 'escalation'>, string> = {
  spec_approval: 'The phase stays the same and the Spec generator runs again with your comment as feedback.',
  plan_approval: 'The phase stays the same and the Plan generator runs again with your comment as feedback.',
  pre_release: 'The phase stays the same and the release doesn’t start.',
}

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
    <Dialog
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
    </Dialog>
  )
}

type EscalationValues = z.output<ReturnType<typeof escalationSchema>>
type EscalationFormValues = z.input<ReturnType<typeof escalationSchema>>

export function EscalationDialog({
  projectId,
  gate,
  currentVersion,
  onClose,
  onDecided,
  fallbackFocusRef,
}: {
  projectId: string
  gate: Gate
  currentVersion: number
  onClose: () => void
  onDecided: (message: string) => void
  fallbackFocusRef?: RefObject<HTMLElement | null>
}) {
  const approve = useApproveGate(projectId)
  const [serverError, setServerError] = useState<string | null>(null)
  const ids = {
    decision: `gate-${gate.id}-decision`,
    targetVersion: `gate-${gate.id}-target`,
    comment: `gate-${gate.id}-comment`,
  } as const
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<EscalationFormValues, unknown, EscalationValues>({
    resolver: zodResolver(escalationSchema(currentVersion)),
    defaultValues: { decision: '', targetVersion: '', comment: '' },
  })
  const decision = useWatch({ control, name: 'decision' })
  const target = useWatch({ control, name: 'targetVersion' }).trim()
  // §7: rollback is destructive, so the dialog becomes an alertdialog with a danger submit naming the version.
  const isRollback = decision === 'rollback'
  const rollbackLabel = /^\d+$/.test(target) ? `Roll back to v${target}` : 'Roll back'
  // FR-33: a blocked execution step can be retried (continue) or rolled back; accept does nothing there, so it's hidden.
  const stepBlocked = gate.escalationReason === 'step_blocked'
  const stepLabel = gate.stepNumber === null ? 'An execution step' : `Step ${String(gate.stepNumber)}`

  const onSubmit = handleSubmit((values) => {
    setServerError(null)
    const comment = values.comment.trim() ? values.comment.trim() : null
    const body = {
      decision: values.decision,
      comment,
      targetVersion: values.decision === 'rollback' ? Number(values.targetVersion.trim()) : null,
    }
    approve.mutate(
      { gateId: gate.id, body },
      {
        onSuccess: () => {
          onDecided(
            stepBlocked && values.decision === 'continue'
              ? `Escalation decided: ${stepLabel.toLowerCase()} will be retried.`
              : values.decision === 'rollback'
              ? `Escalation decided: roll back to version ${values.targetVersion.trim()}.`
              : `Escalation decided: ${escalationDecisionLabels[values.decision].toLowerCase()}.`,
          )
        },
        onError: (error) => {
          const unmatched = applyFieldErrors(error, setError, ['targetVersion', 'comment', 'decision'])
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
    <Dialog
      open
      title="Decide the escalation"
      description={
        stepBlocked
          ? `${stepLabel} was rejected by the Execution Guardian on every attempt and is blocked. Continue retries it with fresh attempts; roll back returns the project to an earlier version.`
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
            id={ids.targetVersion}
            label="Roll back to version"
            hint={
              currentVersion > 1
                ? `A version from 1 to ${String(currentVersion - 1)} (the project is at version ${String(currentVersion)}).`
                : 'The project is at version 1, so there is nothing earlier.'
            }
            inputMode="numeric"
            autoComplete="off"
            error={errors.targetVersion?.message}
            {...register('targetVersion')}
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
    </Dialog>
  )
}
