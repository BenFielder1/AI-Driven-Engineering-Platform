import { zodResolver } from '@hookform/resolvers/zod'
import { useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import { StatusBadge } from '../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { applyFieldErrors, errorMessage, isApiError, messageFor } from '../../lib/errors'
import { RelativeTime } from '../projects/relative-time'
import { deploymentTargetLabels } from './deployment-format'
import {
  deploymentTargets,
  useDeleteDeploymentEnv,
  useDeploymentEnv,
  usePutDeploymentEnv,
  type DeploymentEnvVar,
  type DeploymentTarget,
  type RequiredEnvVar,
} from './deployments-api'
import { checkEnvKey, ENV_KEY_MAX, ENV_VALUE_MAX } from './vercel-project-rules'

const envSchema = z
  .object({
    key: z.string(),
    value: z.string(),
    preview: z.boolean(),
    production: z.boolean(),
    sensitive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    const keyError = checkEnvKey(v.key)
    if (keyError) {
      ctx.addIssue({ code: 'custom', path: ['key'], message: keyError })
    }
    if (!v.value) {
      ctx.addIssue({ code: 'custom', path: ['value'], message: 'Enter the value.' })
    } else if (v.value.length > ENV_VALUE_MAX) {
      ctx.addIssue({ code: 'custom', path: ['value'], message: 'Values are at most 64 KB.' })
    }
    if (!v.preview && !v.production) {
      ctx.addIssue({ code: 'custom', path: ['preview'], message: 'Pick at least one target: Preview or Production.' })
    }
  })

type EnvValues = z.infer<typeof envSchema>

const envIds = {
  key: 'env-key',
  value: 'env-value',
  preview: 'env-target-preview',
  production: 'env-target-production',
  sensitive: 'env-sensitive',
} as const

export interface EnvDraft {
  key: string
  targets: readonly DeploymentTarget[]
  sensitive: boolean
  /** Editing an existing variable: the key can't change. */
  existing: boolean
}

const emptyDraft: EnvDraft = { key: '', targets: ['preview', 'production'], sensitive: false, existing: false }

const toValues = (d: EnvDraft): EnvValues => ({
  key: d.key,
  value: '',
  preview: d.targets.includes('preview'),
  production: d.targets.includes('production'),
  sensitive: d.sensitive,
})

/**
 * FR-88: add or replace an env var. The value is a password field; after every attempt it is cleared from the form
 * and the mutation (whose variables hold it) is reset, so the value lives only as long as the request.
 */
function EnvVarForm({
  projectId,
  draft,
  onDone,
  onCancel,
}: {
  projectId: string
  draft: EnvDraft
  onDone: (outcome: { key: string; synced: boolean }) => void
  onCancel: () => void
}) {
  const put = usePutDeploymentEnv(projectId)
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    reset,
    formState: { errors, isSubmitted },
  } = useForm<EnvValues>({ resolver: zodResolver(envSchema), defaultValues: toValues(draft) })

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const key = v.key.trim()
    const targets = deploymentTargets.filter((t) => v[t])
    put.mutate(
      { key, value: v.value, targets, sensitive: v.sensitive },
      {
        onSuccess: () => {
          setValue('value', '')
          put.reset()
          reset(toValues(emptyDraft))
          onDone({ key, synced: true })
        },
        onError: (error) => {
          setValue('value', '')
          put.reset()
          // 502: saved in AIDE (value encrypted), not yet pushed to Vercel; the list shows the sync error.
          if (isApiError(error, 'UPSTREAM_ERROR')) {
            reset(toValues(emptyDraft))
            onDone({ key, synced: false })
            return
          }
          const unmatched = applyFieldErrors(error, setError, ['key', 'value'])
          setServerErrors(
            unmatched.length > 0 ? unmatched : isApiError(error, 'VALIDATION') ? [] : [errorMessage(error, 'deploymentEnvSave')],
          )
        },
      },
    )
  })

  const title = draft.existing ? `Replace ${draft.key}` : 'Add a variable'
  return (
    <form
      noValidate
      aria-label={title}
      onSubmit={(e) => void onSubmit(e)}
      className="flex flex-col gap-4 rounded-lg border border-border/60 bg-background/40 p-4"
    >
      <h4 className="font-semibold">{title}</h4>
      <ErrorSummary
        title="We couldn't save the variable"
        errors={[...serverErrors.map((message) => ({ message })), ...(isSubmitted ? fieldErrorSummary(errors, envIds) : [])]}
      />
      <TextField
        id={envIds.key}
        label="Name"
        autoComplete="off"
        spellCheck={false}
        maxLength={ENV_KEY_MAX}
        readOnly={draft.existing}
        className="font-mono"
        error={errors.key?.message}
        {...register('key')}
      />
      <TextField
        id={envIds.value}
        label="Value"
        type="password"
        autoComplete="new-password"
        spellCheck={false}
        hint={
          draft.existing
            ? 'Enter the new value. The saved value is never shown; it is encrypted in AIDE and pushed to Vercel.'
            : 'Encrypted in AIDE and pushed to Vercel on save. It is never shown again.'
        }
        error={errors.value?.message}
        {...register('value')}
      />
      <fieldset className="flex flex-col gap-2" aria-describedby={errors.preview ? 'env-targets-error' : undefined}>
        <legend className="mb-1 text-sm font-medium">Targets</legend>
        {deploymentTargets.map((t) => (
          <div key={t} className="flex items-center gap-2">
            <input id={envIds[t]} type="checkbox" className="size-4" {...register(t)} />
            <label htmlFor={envIds[t]} className="text-sm">
              {deploymentTargetLabels[t]}
            </label>
          </div>
        ))}
        {errors.preview?.message && (
          <p id="env-targets-error" className="text-sm text-destructive">
            {errors.preview.message}
          </p>
        )}
      </fieldset>
      <div className="flex items-start gap-2">
        <input id={envIds.sensitive} type="checkbox" className="mt-0.5 size-4" aria-describedby="env-sensitive-hint" {...register('sensitive')} />
        <div>
          <label htmlFor={envIds.sensitive} className="text-sm">
            Sensitive
          </label>
          <p id="env-sensitive-hint" className="text-sm text-muted-foreground">
            Vercel stores it so that nobody can read it back, not even in the Vercel dashboard.
          </p>
        </div>
      </div>
      <div className="flex flex-wrap justify-end gap-2">
        <Button
          variant="ghost"
          disabled={put.isPending}
          onClick={() => {
            setValue('value', '')
            put.reset()
            onCancel()
          }}
        >
          Cancel
        </Button>
        <Button type="submit" pending={put.isPending} pendingLabel="Saving…">
          Save variable
        </Button>
      </div>
    </form>
  )
}

function SyncStatus({ v }: { v: DeploymentEnvVar }) {
  if (v.syncError) {
    return (
      <span className="text-destructive" data-testid="env-sync-error">
        Not synced to Vercel: {v.syncError} AIDE will retry on the next save or deployment.
      </span>
    )
  }
  if (v.syncedAt) {
    return (
      <span className="text-muted-foreground">
        Synced to Vercel <RelativeTime iso={v.syncedAt} />
      </span>
    )
  }
  return <span className="text-muted-foreground">Not synced to Vercel yet</span>
}

/**
 * Settings → Deployment → Environment variables (FR-88, Owners): key, targets, sensitive and whether a value is set
 * (never the value), the Vercel sync state, replace and delete. Missing required variables can be added in one click.
 */
export function EnvVarsSection({ projectId, required }: { projectId: string; required: readonly RequiredEnvVar[] }) {
  const env = useDeploymentEnv(projectId)
  const remove = useDeleteDeploymentEnv(projectId)
  const [draft, setDraft] = useState<EnvDraft | null>(null)
  const [toDelete, setToDelete] = useState<DeploymentEnvVar | null>(null)
  const [notice, setNotice] = useState<{ tone: 'warning' | 'error'; title: string; message: string } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const open = (d: EnvDraft) => {
    setNotice(null)
    setDraft(d)
  }

  const confirmDelete = () => {
    if (!toDelete) {
      return
    }
    const target = toDelete
    remove.mutate(target.key, {
      onSuccess: () => {
        setToDelete(null)
        toast.success(`${target.key} deleted in AIDE and Vercel.`)
        headingRef.current?.focus()
      },
      onError: (error) => {
        setToDelete(null)
        setNotice({ tone: 'error', title: `We couldn't delete ${target.key}`, message: errorMessage(error, 'deploymentEnvDelete') })
      },
    })
  }

  const missing = required.filter((r) => !r.present)

  let body
  if (env.isPending) {
    body = <LoadingState label="Loading environment variables…" />
  } else if (env.isError) {
    body = <ErrorState error={env.error} title="The environment variables couldn't load" onRetry={() => void env.refetch()} />
  } else if (env.data.length === 0) {
    body = <p className="text-sm text-muted-foreground">No environment variables yet.</p>
  } else {
    body = (
      <ul aria-label="Environment variables" className="divide-y divide-border/60 rounded-lg border border-border/60">
        {env.data.map((v) => (
          <li key={v.key} aria-label={`Variable ${v.key}`} className="flex flex-col gap-2 p-3 sm:flex-row sm:items-start sm:justify-between">
            <div className="flex min-w-0 flex-col gap-1 text-sm">
              <span className="flex flex-wrap items-center gap-2">
                <code className="font-mono font-semibold break-all">{v.key}</code>
                {v.sensitive && <StatusBadge tone="neutral">Sensitive</StatusBadge>}
                <span className="text-muted-foreground">{v.targets.map((t) => deploymentTargetLabels[t]).join(' · ')}</span>
              </span>
              <span>
                {v.hasValue ? 'Value set' : 'No value'} · updated <RelativeTime iso={v.updatedAt} />
              </span>
              <SyncStatus v={v} />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="secondary"
                aria-label={`Replace the value of ${v.key}`}
                onClick={() => {
                  open({ key: v.key, targets: v.targets, sensitive: v.sensitive, existing: true })
                }}
              >
                Replace value
              </Button>
              <Button
                variant="danger"
                aria-label={`Delete ${v.key}`}
                onClick={() => {
                  setToDelete(v)
                }}
              >
                Delete
              </Button>
            </div>
          </li>
        ))}
      </ul>
    )
  }

  return (
    <section aria-labelledby="env-vars-title" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id="env-vars-title" ref={headingRef} tabIndex={-1} className="text-lg outline-none">
          Environment variables
        </h3>
        {draft === null && (
          <Button
            variant="secondary"
            onClick={() => {
              open(emptyDraft)
            }}
          >
            Add variable
          </Button>
        )}
      </div>
      <p className="text-sm text-muted-foreground">
        Values are encrypted in AIDE, pushed to Vercel when you save, and never shown again.
      </p>
      {notice && (
        <Alert tone={notice.tone} role={notice.tone === 'error' ? 'alert' : 'status'} title={notice.title}>
          <p>{notice.message}</p>
        </Alert>
      )}
      {missing.length > 0 && draft === null && (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span>Add a missing required variable:</span>
          {missing.map((r) => (
            <Button
              key={r.key}
              variant="ghost"
              aria-label={`Add ${r.key}`}
              onClick={() => {
                open({ key: r.key, targets: r.targets, sensitive: r.sensitive, existing: false })
              }}
            >
              <code className="font-mono">{r.key}</code>
            </Button>
          ))}
        </div>
      )}
      {draft && (
        <EnvVarForm
          key={`${draft.key}:${String(draft.existing)}`}
          projectId={projectId}
          draft={draft}
          onCancel={() => {
            setDraft(null)
          }}
          onDone={({ key, synced }) => {
            setDraft(null)
            if (synced) {
              toast.success(`${key} saved and pushed to Vercel.`)
            } else {
              setNotice({ tone: 'warning', title: `${key} saved, not yet in Vercel`, message: messageFor('UPSTREAM_ERROR', 'deploymentEnvSave') })
            }
          }}
        />
      )}
      {body}
      <ConfirmDialog
        open={toDelete !== null}
        title={`Delete ${toDelete?.key ?? ''}?`}
        confirmLabel="Delete variable"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmDelete}
        onCancel={() => {
          setToDelete(null)
        }}
      >
        <p>It is deleted in AIDE and in Vercel. Deployments after that build without it.</p>
      </ConfirmDialog>
    </section>
  )
}
