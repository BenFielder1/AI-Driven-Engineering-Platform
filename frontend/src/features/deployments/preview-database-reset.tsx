import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { errorMessage } from '../../lib/errors'
import { useResetPreviewDatabase, type PreviewResetMethod } from './deployments-api'
import { previewResetCopy } from './rollout-format'

/**
 * FR-148 (Owner): "Reset preview database", confirmed first because it drops the preview data. After it is queued, a
 * status note says what happens next for the method (`manual` on Railway: the Owner empties it there); a refusal
 * (`INVALID_STATE` while a rollout or reset runs) is shown with its reason.
 */
export function ResetPreviewDatabase({
  projectId,
  variant = 'secondary',
}: {
  projectId: string
  variant?: 'primary' | 'secondary'
}) {
  const reset = useResetPreviewDatabase(projectId)
  const [confirming, setConfirming] = useState(false)
  const [method, setMethod] = useState<PreviewResetMethod | null>(null)

  return (
    <div className="flex flex-col gap-2">
      <div>
        <Button
          variant={variant}
          onClick={() => {
            setMethod(null)
            reset.reset()
            setConfirming(true)
          }}
        >
          Reset preview database
        </Button>
      </div>
      {method && (
        <p role="status" data-testid="preview-reset-result" className="rounded-lg border border-primary/40 bg-primary/10 p-3 text-sm">
          {previewResetCopy[method]}
        </p>
      )}
      {reset.isError && (
        <Alert tone="error" role="alert" title="The preview database wasn't reset">
          <p data-testid="preview-reset-error">{errorMessage(reset.error, 'databaseReset')}</p>
        </Alert>
      )}
      <ConfirmDialog
        open={confirming}
        title="Reset the preview database?"
        confirmLabel="Reset"
        pendingLabel="Resetting…"
        pending={reset.isPending}
        destructive
        onConfirm={() => {
          reset.mutate(undefined, {
            onSuccess: (result) => {
              setMethod(result.method)
              toast.success(previewResetCopy[result.method])
            },
            onSettled: () => {
              setConfirming(false)
            },
          })
        }}
        onCancel={() => {
          setConfirming(false)
        }}
      >
        <p>
          This drops every table and row in the preview database. Production isn&apos;t touched. The next preview deployment
          re-applies every migration from the version branch.
        </p>
      </ConfirmDialog>
    </div>
  )
}
