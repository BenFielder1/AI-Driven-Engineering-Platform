import { useRef, useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import {
  useCreatePipelineWebhook,
  useDeletePipelineWebhook,
  usePipelineWebhooks,
  useRotatePipelineWebhook,
  type PipelineWebhook,
} from '../api/trigger-hooks'
import type { PipelineDefinition } from '../api/types'
import { formatDateTime } from '../format'
import { absoluteUrl, signingExample } from './trigger-format'

/** A secret on screen: held only in this component's state, gone on Done or a reload (FR-366). */
interface RevealedSecret {
  webhookId: string
  url: string
  secret: string
  inputTitle: string | null
  rotated: boolean
}

/** FR-366: the new secret, shown once, with a copy button and how to sign a call with it (§4.72). */
function SecretNotice({ revealed, onDone }: { revealed: RevealedSecret; onDone: () => void }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const inputRef = useRef<HTMLInputElement>(null)
  const url = absoluteUrl(revealed.url)

  async function copy() {
    try {
      await navigator.clipboard.writeText(revealed.secret)
      setCopyState('copied')
    } catch {
      inputRef.current?.select()
      setCopyState('failed')
    }
  }

  return (
    <Alert tone="success" title={revealed.rotated ? 'New webhook secret' : 'Webhook created'}>
      <p className="mt-1 font-medium">Copy the secret now. It isn’t shown again.</p>
      {revealed.rotated && <p className="text-sm">The previous secret keeps working for 24 hours.</p>}
      <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:items-end">
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <label htmlFor="webhook-secret-value" className="text-sm font-medium">
            Signing secret
          </label>
          <input
            ref={inputRef}
            id="webhook-secret-value"
            readOnly
            value={revealed.secret}
            onFocus={(e) => {
              e.currentTarget.select()
            }}
            className="min-h-10 w-full rounded-md border border-field-border bg-background px-3 py-2 font-mono text-sm text-foreground"
          />
        </div>
        <Button variant="secondary" onClick={() => void copy()}>
          Copy secret
        </Button>
        <Button variant="ghost" onClick={onDone}>
          Done
        </Button>
      </div>
      <p role="status" aria-live="polite" className="mt-2 text-sm">
        {copyState === 'copied' && 'Copied to the clipboard.'}
        {copyState === 'failed' && "Couldn't copy automatically. The secret is selected: copy it manually."}
      </p>
      <section aria-labelledby="webhook-signing-title" className="mt-3 flex flex-col gap-1">
        <h5 className="text-sm font-medium" id="webhook-signing-title">
          Signing a call
        </h5>
        <p className="text-sm">
          Sign <code className="font-mono">{'<unix seconds>.<raw body>'}</code> with HMAC-SHA256 and the secret, and send it as{' '}
          <code className="font-mono">{'X-Tevrin-Signature: t=<unix seconds>,v1=<hex>'}</code> with a new{' '}
          <code className="font-mono">X-Tevrin-Delivery</code> id per call. Calls more than 5 minutes old are refused.
        </p>
        <pre
          role="region"
          aria-label="Signing example (scrolls sideways)"
          tabIndex={0}
          className="overflow-x-auto rounded-md border border-border/60 bg-muted p-3 font-mono text-xs"
        >
          {signingExample(url, revealed.inputTitle)}
        </pre>
      </section>
    </Alert>
  )
}

function inputTitleOf(definition: PipelineDefinition | undefined): string | null {
  const key = definition?.triggers?.find((t) => t.kind === 'webhook')?.input
  return definition?.artifacts.find((a) => a.key === key)?.title ?? null
}

/**
 * FR-366 webhooks: the project's webhooks (never with a secret), create one for a pipeline that declares a webhook
 * trigger, rotate its secret and delete it (Owner). A new secret shows once; a reload shows the webhook without it.
 */
export function WebhooksPanel({
  projectId,
  pipelines,
  definitions,
  titles,
}: {
  projectId: string
  /** The engine pipelines whose definitions declare `trigger: { kind: webhook }`. */
  pipelines: readonly PipelineDefinition[]
  /** Every loaded engine definition by pipeline id (for the signing example's input). */
  definitions: ReadonlyMap<string, PipelineDefinition>
  titles: ReadonlyMap<string, string>
}) {
  const webhooks = usePipelineWebhooks(projectId)
  const create = useCreatePipelineWebhook(projectId)
  const rotate = useRotatePipelineWebhook(projectId)
  const remove = useDeletePipelineWebhook(projectId)
  const [pipelineId, setPipelineId] = useState(pipelines[0]?.pipelineId ?? '')
  const [revealed, setRevealed] = useState<RevealedSecret | null>(null)
  const [confirm, setConfirm] = useState<{ action: 'rotate' | 'delete'; webhook: PipelineWebhook } | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const onError = (error: unknown) => {
    setActionError(errorMessage(error, 'pipelineTriggers'))
  }
  const selected = pipelines.some((p) => p.pipelineId === pipelineId) ? pipelineId : (pipelines[0]?.pipelineId ?? '')

  let list
  if (webhooks.isPending) {
    list = <LoadingState label="Loading webhooks…" />
  } else if (webhooks.isError) {
    list = <ErrorState error={webhooks.error} onRetry={() => void webhooks.refetch()} title="The webhooks couldn't load" />
  } else if (webhooks.data.length === 0) {
    list = <EmptyState title="No webhooks">A webhook starts a run when another system calls it with a signed request.</EmptyState>
  } else {
    list = (
      <ul aria-label="Webhooks">
        {webhooks.data.map((w) => {
          const title = titles.get(w.pipelineId) ?? w.pipelineId
          return (
            <li key={w.id} className="flex flex-col gap-2 border-b border-border py-3 last:border-b-0 sm:flex-row sm:items-start sm:justify-between">
              <div className="flex min-w-0 flex-col gap-1">
                <span className="font-medium">{title}</span>
                <code className="break-all font-mono text-sm">{absoluteUrl(w.url)}</code>
                {w.previousSecretExpiresAt && (
                  <p className="text-xs text-muted-foreground">
                    The previous secret works until {formatDateTime(w.previousSecretExpiresAt)}.
                  </p>
                )}
              </div>
              <div className="flex flex-wrap gap-2">
                <Button
                  variant="secondary"
                  aria-label={`Rotate the secret of the ${title} webhook`}
                  onClick={() => {
                    setActionError(null)
                    setConfirm({ action: 'rotate', webhook: w })
                  }}
                >
                  Rotate secret
                </Button>
                <Button
                  variant="ghost"
                  aria-label={`Delete the ${title} webhook`}
                  onClick={() => {
                    setActionError(null)
                    setConfirm({ action: 'delete', webhook: w })
                  }}
                >
                  Delete
                </Button>
              </div>
            </li>
          )
        })}
      </ul>
    )
  }

  const pendingConfirm = rotate.isPending || remove.isPending
  return (
    <div className="flex flex-col gap-4">
      {revealed && (
        <SecretNotice
          revealed={revealed}
          onDone={() => {
            setRevealed(null)
          }}
        />
      )}
      {actionError && (
        <p role="alert" className="text-sm text-destructive">
          {actionError}
        </p>
      )}
      {list}
      {pipelines.length > 0 ? (
        <form
          noValidate
          className="flex flex-col gap-3 sm:flex-row sm:items-end"
          onSubmit={(e) => {
            e.preventDefault()
            setActionError(null)
            create.mutate(selected, {
              onSuccess: (created) => {
                setRevealed({
                  webhookId: created.id,
                  url: created.url,
                  secret: created.secret,
                  inputTitle: inputTitleOf(definitions.get(selected)),
                  rotated: false,
                })
                create.reset()
              },
              onError,
            })
          }}
        >
          <SelectField
            label="Pipeline"
            className="sm:max-w-sm"
            value={selected}
            onChange={(e) => {
              setPipelineId(e.target.value)
            }}
          >
            {pipelines.map((p) => (
              <option key={p.pipelineId} value={p.pipelineId}>
                {p.title}
              </option>
            ))}
          </SelectField>
          <Button type="submit" pending={create.isPending} pendingLabel="Creating…">
            Create webhook
          </Button>
        </form>
      ) : (
        <p className="text-sm text-muted-foreground">No pipeline here declares a webhook trigger.</p>
      )}
      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.action === 'rotate' ? 'Rotate this webhook’s secret?' : 'Delete this webhook?'}
        confirmLabel={confirm?.action === 'rotate' ? 'Rotate secret' : 'Delete'}
        pendingLabel={confirm?.action === 'rotate' ? 'Rotating…' : 'Deleting…'}
        pending={pendingConfirm}
        destructive={confirm?.action === 'delete'}
        onCancel={() => {
          setConfirm(null)
        }}
        onConfirm={() => {
          if (!confirm) {
            return
          }
          const { webhook } = confirm
          if (confirm.action === 'rotate') {
            rotate.mutate(webhook.id, {
              onSuccess: ({ secret }) => {
                setRevealed({
                  webhookId: webhook.id,
                  url: webhook.url,
                  secret,
                  inputTitle: inputTitleOf(definitions.get(webhook.pipelineId)),
                  rotated: true,
                })
                rotate.reset()
              },
              onError,
              onSettled: () => {
                setConfirm(null)
              },
            })
          } else {
            remove.mutate(webhook.id, {
              onSuccess: () => {
                if (revealed?.webhookId === webhook.id) {
                  setRevealed(null)
                }
                toast.success('Webhook deleted.')
              },
              onError,
              onSettled: () => {
                setConfirm(null)
              },
            })
          }
        }}
      >
        {confirm?.action === 'rotate' ? (
          <p>A new secret is shown once. The current one keeps working for 24 hours, so senders can switch over.</p>
        ) : (
          <p>Calls to its URL are refused from now on. Runs it already started carry on.</p>
        )}
      </ConfirmDialog>
    </div>
  )
}
