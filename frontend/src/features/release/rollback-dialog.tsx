import { zodResolver } from '@hookform/resolvers/zod'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useForm } from 'react-hook-form'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextAreaField } from '../../components/ui/textarea-field'
import { applyFieldErrors, errorMessage, isApiError, messageFor } from '../../lib/errors'
import { phaseLabels, rollbackBlockLabels } from '../projects/labels'
import { checkpointLabel } from '../versions/version-label'
import { useCheckpoints, type Checkpoint } from '../versions/versions-api'
import { useRollback, type Rollback } from './release-api'
import { ROLLBACK_REASON_MAX, rollbackSchema, type RollbackFormValues, type RollbackValues } from './rollback-schemas'

interface RetryWindow {
  /** Epoch ms when the rollback is allowed again. */
  until: number
  /** Whole seconds left, ticking down to 0. */
  remaining: number
}

/** FR-45: the Retry-After countdown; `start(seconds)` begins it, `clear()` drops it. */
function useRetryCountdown() {
  const [retry, setRetry] = useState<RetryWindow | null>(null)
  const until = retry?.until ?? null
  const running = retry !== null && retry.remaining > 0
  useEffect(() => {
    if (until === null || !running) {
      return undefined
    }
    const timer = setInterval(() => {
      setRetry((r) => (r ? { ...r, remaining: Math.max(0, Math.ceil((r.until - Date.now()) / 1000)) } : r))
    }, 250)
    return () => {
      clearInterval(timer)
    }
  }, [until, running])
  return {
    retry,
    start: (seconds: number) => {
      setRetry({ until: Date.now() + seconds * 1000, remaining: seconds })
    },
    clear: () => {
      setRetry(null)
    },
  }
}

function checkpointOptionLabel(version: string | null, c: Checkpoint): string {
  const base = `${checkpointLabel(version, c.checkpoint)} · ${phaseLabels[c.phase]}`
  return !c.rollbackAllowed && c.rollbackBlockedReason ? `${base} (${rollbackBlockLabels[c.rollbackBlockedReason]})` : base
}

const ids = { targetCheckpoint: 'rollback-target', reason: 'rollback-reason' } as const

/**
 * Settings → Danger zone → Roll back (FR-44, FR-45, FR-63, §4.8, §4.10). Two steps: pick an allowed checkpoint of the
 * version in development (listed as "1.2.0 · checkpoint 4 · Review", blocked ones with the reason) and give a reason
 * (Zod: 1–500 characters), then confirm a summary of the effects. ROLLBACK_BLOCKED and RATE_LIMITED (with a countdown
 * from Retry-After) are shown on the confirmation step; INVALID_TARGET_VERSION returns to the picker.
 */
export function RollbackDialog({
  projectId,
  version,
  currentCheckpoint,
  onClose,
  onRolledBack,
}: {
  projectId: string
  /** The release version in development (the checkpoints belong to it). */
  version: string | null
  currentCheckpoint: number
  onClose: () => void
  onRolledBack: (rollback: Rollback) => void
}) {
  const checkpoints = useCheckpoints(projectId, undefined)
  const rollback = useRollback(projectId)
  const [confirmed, setConfirmed] = useState<RollbackValues | null>(null)
  const [serverError, setServerError] = useState<unknown>(null)
  const countdown = useRetryCountdown()
  const retryAt = countdown.retry
  const remaining = retryAt?.remaining ?? 0
  const confirmHeadingRef = useRef<HTMLHeadingElement>(null)

  const {
    register,
    handleSubmit,
    setError,
    setFocus,
    formState: { errors, isSubmitted },
  } = useForm<RollbackFormValues, unknown, RollbackValues>({
    resolver: zodResolver(rollbackSchema),
    defaultValues: { targetCheckpoint: '', reason: '' },
  })

  // Focus follows the step (NFR-A11Y): the confirmation heading, or the field to fix after going back. The picker gets
  // focus once the versions load (the dialog opens on a loading state with nothing to focus).
  const focusField = useRef<keyof RollbackFormValues | null>('targetCheckpoint')
  const loaded = checkpoints.isSuccess
  useEffect(() => {
    if (confirmed) {
      confirmHeadingRef.current?.focus()
    } else if (loaded && focusField.current) {
      setFocus(focusField.current)
      focusField.current = null
    }
  }, [confirmed, loaded, setFocus])

  const backToForm = (field: keyof RollbackFormValues = 'targetCheckpoint') => {
    focusField.current = field
    setConfirmed(null)
  }

  const onContinue = handleSubmit((values) => {
    setServerError(null)
    setConfirmed(values)
  })

  const onConfirm = () => {
    if (!confirmed || remaining > 0) {
      return
    }
    setServerError(null)
    countdown.clear()
    rollback.mutate(
      { targetCheckpoint: confirmed.targetCheckpoint, reason: confirmed.reason },
      {
        onSuccess: (record) => {
          onRolledBack(record)
        },
        onError: (error) => {
          if (isApiError(error, 'RATE_LIMITED') && error.retryAfterSeconds) {
            countdown.start(error.retryAfterSeconds)
          }
          if (isApiError(error, 'INVALID_TARGET_VERSION')) {
            void checkpoints.refetch()
            setError('targetCheckpoint', { type: 'server', message: messageFor('INVALID_TARGET_VERSION') })
            backToForm('targetCheckpoint')
            return
          }
          if (isApiError(error, 'VALIDATION')) {
            const unmatched = applyFieldErrors(error, setError, ['targetCheckpoint', 'reason'])
            if (unmatched.length > 0) {
              setServerError(error)
            }
            backToForm(Object.hasOwn(error.fieldErrors, 'reason') ? 'reason' : 'targetCheckpoint')
            return
          }
          setServerError(error)
        },
      },
    )
  }

  const rateLimited = isApiError(serverError, 'RATE_LIMITED')
  const errorCopy = serverError === null ? null : rateLimited ? messageFor('RATE_LIMITED', 'rollback') : errorMessage(serverError, 'rollback')
  const target = confirmed ? checkpoints.data?.find((c) => c.checkpoint === confirmed.targetCheckpoint) : undefined
  const allowed = checkpoints.data?.filter((c) => c.rollbackAllowed) ?? []
  const sorted = [...(checkpoints.data ?? [])].sort((a, b) => b.checkpoint - a.checkpoint)
  const at = (n: number) => checkpointLabel(version, n)

  let content: ReactNode
  if (checkpoints.isPending) {
    content = <LoadingState label="Loading checkpoints…" />
  } else if (checkpoints.isError) {
    content = (
      <ErrorState error={checkpoints.error} title="The checkpoints couldn't load" onRetry={() => void checkpoints.refetch()} />
    )
  } else if (confirmed) {
    const phase = target ? phaseLabels[target.phase] : null
    content = (
      <div className="flex flex-col gap-4">
        <h3 ref={confirmHeadingRef} tabIndex={-1} className="font-semibold outline-none">
          Roll back from {at(currentCheckpoint)} to {at(confirmed.targetCheckpoint)}?
        </h3>
        <ul aria-label="What the rollback does" className="list-disc pl-5 text-sm">
          <li>
            {phase ? `Restores the ${phase} phase` : 'Restores the phase'} recorded at checkpoint {confirmed.targetCheckpoint}; the
            project status becomes Rolled back.
          </li>
          <li>
            Artifacts created after checkpoint {confirmed.targetCheckpoint} are invalidated, and the steps of invalidated plans
            are reset.
          </li>
          <li>Pending gates and queued jobs are cancelled, and the review state is cleared.</li>
          <li>The Git branch isn&apos;t rewritten: later commits stay in its history.</li>
        </ul>
        <div className="text-sm">
          <p className="font-medium">Reason</p>
          <p className="whitespace-pre-wrap break-words text-muted-foreground">{confirmed.reason}</p>
        </div>
        <div role="alert" aria-live="assertive">
          {errorCopy && <Alert tone="error" title="We couldn't roll back the project">
            <p>{errorCopy}</p>
          </Alert>}
        </div>
        {retryAt && (
          <p className="text-sm font-medium" aria-live="off">
            {remaining > 0 ? `Try again in ${String(remaining)}s.` : 'You can try again now.'}
          </p>
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button
            variant="secondary"
            disabled={rollback.isPending}
            onClick={() => {
              setServerError(null)
              backToForm()
            }}
          >
            Back
          </Button>
          <Button
            variant="danger"
            pending={rollback.isPending}
            pendingLabel="Rolling back…"
            disabled={remaining > 0}
            onClick={onConfirm}
          >
            Roll back to checkpoint {confirmed.targetCheckpoint}
          </Button>
        </div>
      </div>
    )
  } else {
    content = (
      <form noValidate onSubmit={(e) => void onContinue(e)} className="flex flex-col gap-4">
        <ErrorSummary
          title="Check the rollback"
          errors={[
            ...(errorCopy ? [{ message: errorCopy }] : []),
            ...(isSubmitted ? fieldErrorSummary(errors, ids) : []),
          ]}
        />
        {allowed.length === 0 && (
          <Alert tone="info">There&apos;s no earlier checkpoint of this version to roll back to.</Alert>
        )}
        <SelectField
          id={ids.targetCheckpoint}
          label="Checkpoint to restore"
          hint="Checkpoints of the version in development. Those that can't be restored are listed with the reason."
          error={errors.targetCheckpoint?.message}
          {...register('targetCheckpoint')}
        >
          <option value="">Choose a checkpoint</option>
          {sorted.map((c) => (
            <option key={c.checkpoint} value={String(c.checkpoint)} disabled={!c.rollbackAllowed}>
              {checkpointOptionLabel(version, c)}
            </option>
          ))}
        </SelectField>
        <TextAreaField
          id={ids.reason}
          label="Reason"
          hint={`Recorded with the rollback. Up to ${String(ROLLBACK_REASON_MAX)} characters.`}
          rows={3}
          error={errors.reason?.message}
          {...register('reason')}
        />
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" disabled={allowed.length === 0}>
            Continue
          </Button>
        </div>
      </form>
    )
  }

  return (
    <ModalDialog
      open
      role="alertdialog"
      title="Roll back the project"
      description="Restore an earlier checkpoint of the version in development. You'll confirm what changes before anything happens."
      busy={rollback.isPending}
      onClose={onClose}
    >
      {content}
    </ModalDialog>
  )
}
