import { zodResolver } from '@hookform/resolvers/zod'
import { useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import type { Schemas } from '../../api/client'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError, messageFor } from '../../lib/errors'
import { RelativeTime } from '../projects/relative-time'
import { componentLabels, deploymentTargetLabels, envSourceLabels, envSourceTones } from './deployment-format'
import {
  deploymentComponents,
  deploymentTargets,
  useDeleteDeploymentEnv,
  usePutDeploymentEnv,
  type DeploymentComponent,
  type DeploymentEnvVar,
  type DeploymentTarget,
} from './deployments-api'
import { checkEnvKey, ENV_KEY_MAX, ENV_VALUE_MAX } from './vercel-project-rules'

type Inventory = Schemas['DeploymentEnvInventoryDto']

const envSchema = z
  .object({
    key: z.string(),
    value: z.string(),
    frontend: z.boolean(),
    backend: z.boolean(),
    preview: z.boolean(),
    production: z.boolean(),
    sensitive: z.boolean(),
    public: z.boolean(),
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
    if (!v.frontend && !v.backend) {
      ctx.addIssue({ code: 'custom', path: ['frontend'], message: 'Pick at least one component: Frontend or Backend.' })
    }
    if (!v.preview && !v.production) {
      ctx.addIssue({ code: 'custom', path: ['preview'], message: 'Pick at least one environment: Preview or Production.' })
    }
  })

type EnvValues = z.infer<typeof envSchema>

const envIds = {
  key: 'env-key',
  value: 'env-value',
  frontend: 'env-applies-frontend',
  backend: 'env-applies-backend',
  preview: 'env-target-preview',
  production: 'env-target-production',
  sensitive: 'env-sensitive',
  public: 'env-public',
} as const

/** Server VALIDATION keys → form fields. */
const serverFields: Record<string, keyof EnvValues> = {
  key: 'key',
  value: 'value',
  public: 'public',
  appliesto: 'frontend',
  targets: 'preview',
}

export interface EnvDraft {
  key: string
  appliesTo: readonly DeploymentComponent[]
  environments: readonly DeploymentTarget[]
  sensitive: boolean
  public: boolean
  /** The key can't change: replacing a user value, or adding a required one. */
  keyLocked: boolean
  title: string
}

const emptyDraft: EnvDraft = {
  key: '',
  appliesTo: ['frontend'],
  environments: ['preview', 'production'],
  sensitive: false,
  public: false,
  keyLocked: false,
  title: 'Add a variable',
}

const toValues = (d: EnvDraft): EnvValues => ({
  key: d.key,
  value: '',
  frontend: d.appliesTo.includes('frontend'),
  backend: d.appliesTo.includes('backend'),
  preview: d.environments.includes('preview'),
  production: d.environments.includes('production'),
  sensitive: d.sensitive,
  public: d.public,
})

function Checkbox({ id, label, hint, error, inputProps }: { id: string; label: string; hint?: string; error?: string | undefined; inputProps: object }) {
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(' ') || undefined
  return (
    <div className="flex items-start gap-2">
      <input id={id} type="checkbox" className="mt-0.5 size-4" aria-describedby={describedBy} aria-invalid={error ? true : undefined} {...inputProps} />
      <div>
        <label htmlFor={id} className="text-sm">
          {label}
        </label>
        {hint && (
          <p id={`${id}-hint`} className="text-sm text-muted-foreground">
            {hint}
          </p>
        )}
        {error && (
          <p id={`${id}-error`} className="text-sm text-destructive">
            {error}
          </p>
        )}
      </div>
    </div>
  )
}

/**
 * FR-145: add or replace a user variable. The value is a password field; after every attempt it is cleared from the
 * form and the mutation (whose variables hold it) is reset, so the value lives only as long as the request. Server
 * VALIDATION (a derived key; "secret values can't be public") is shown inline on its field.
 */
function EnvVarForm({
  projectId,
  publicPrefix,
  draft,
  onDone,
  onCancel,
}: {
  projectId: string
  publicPrefix: string | null
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
    formState: { errors, isSubmitted },
  } = useForm<EnvValues>({ resolver: zodResolver(envSchema), defaultValues: toValues(draft) })

  const onSubmit = handleSubmit((v) => {
    setServerErrors([])
    const key = v.key.trim()
    put.mutate(
      {
        key,
        value: v.value,
        targets: deploymentTargets.filter((t) => v[t]),
        appliesTo: deploymentComponents.filter((c) => v[c]),
        sensitive: v.sensitive,
        public: v.public || (publicPrefix !== null && key.startsWith(publicPrefix)),
      },
      {
        onSuccess: () => {
          setValue('value', '')
          put.reset()
          onDone({ key, synced: true })
        },
        onError: (error) => {
          setValue('value', '')
          put.reset()
          // 502: saved in AIDE (value encrypted), not yet pushed to a platform; the row shows the sync error.
          if (isApiError(error, 'UPSTREAM_ERROR')) {
            onDone({ key, synced: false })
            return
          }
          const unmatched: string[] = []
          if (isApiError(error, 'VALIDATION')) {
            for (const [field, messages] of Object.entries(error.fieldErrors)) {
              const target = serverFields[field.toLowerCase()]
              if (target) {
                setError(target, { type: 'server', message: messages.join(' ') })
              } else {
                unmatched.push(messages.join(' '))
              }
            }
            setServerErrors(unmatched)
            return
          }
          setServerErrors([errorMessage(error, 'deploymentEnvSave')])
        },
      },
    )
  })

  return (
    <form
      noValidate
      aria-label={draft.title}
      onSubmit={(e) => void onSubmit(e)}
      className="flex flex-col gap-4 rounded-lg border border-border/60 bg-background/40 p-4"
    >
      <h4 className="font-semibold">{draft.title}</h4>
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
        readOnly={draft.keyLocked}
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
        hint="Encrypted in AIDE and pushed to the deployment platforms on save. It is never shown again."
        error={errors.value?.message}
        {...register('value')}
      />
      <div className="grid gap-4 sm:grid-cols-2">
        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-sm font-medium">Components</legend>
          {deploymentComponents.map((c) => (
            <Checkbox
              key={c}
              id={envIds[c]}
              label={componentLabels[c]}
              error={c === 'frontend' ? errors.frontend?.message : undefined}
              inputProps={register(c)}
            />
          ))}
        </fieldset>
        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-sm font-medium">Environments</legend>
          {deploymentTargets.map((t) => (
            <Checkbox
              key={t}
              id={envIds[t]}
              label={deploymentTargetLabels[t]}
              error={t === 'preview' ? errors.preview?.message : undefined}
              inputProps={register(t)}
            />
          ))}
        </fieldset>
      </div>
      <Checkbox
        id={envIds.sensitive}
        label="Sensitive"
        hint="Stored so that nobody can read it back, where the platform supports it."
        inputProps={register('sensitive')}
      />
      <Checkbox
        id={envIds.public}
        label="Public"
        hint={
          publicPrefix
            ? `Reaches the browser. Keys starting with ${publicPrefix} are always public. Secret values can't be public.`
            : "Reaches the browser. Secret values can't be public."
        }
        error={errors.public?.message}
        inputProps={register('public')}
      />
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

function SyncState({ v }: { v: DeploymentEnvVar }) {
  if (v.source === 'required') {
    return <span>Missing: deployments are likely to fail without it.</span>
  }
  if (v.source === 'derived' && !v.hasValue) {
    return <span className="text-muted-foreground">No value yet: AIDE fills it once its source exists.</span>
  }
  if (v.syncError) {
    return (
      <span className="text-destructive" data-testid="env-sync-error">
        Not synced: {v.syncError} AIDE retries on the next save or deployment.
      </span>
    )
  }
  if (v.syncedAt) {
    return (
      <span className="text-muted-foreground">
        Synced <RelativeTime iso={v.syncedAt} />
      </span>
    )
  }
  return <span className="text-muted-foreground">Not synced yet</span>
}

/**
 * Settings → Deployment → Environment inventory (FR-145): one card per variable (stacks at 375 px) with its key,
 * source badge (user / derived / required-missing), components, environments, public and sensitive markers and sync
 * state. Values are never shown. Owners add, replace and delete user variables and add values for required ones;
 * derived variables are read-only with their origin.
 */
export function EnvInventorySection({ projectId, inventory, canManage }: { projectId: string; inventory: Inventory; canManage: boolean }) {
  const remove = useDeleteDeploymentEnv(projectId)
  const [draft, setDraft] = useState<EnvDraft | null>(null)
  const [toDelete, setToDelete] = useState<DeploymentEnvVar | null>(null)
  const [notice, setNotice] = useState<{ tone: 'warning' | 'error'; title: string; message: string } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const prefix = inventory.publicPrefix

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
        toast.success(`${target.key} deleted.`)
        headingRef.current?.focus()
      },
      onError: (error) => {
        setToDelete(null)
        setNotice({ tone: 'error', title: `We couldn't delete ${target.key}`, message: errorMessage(error, 'deploymentEnvDelete') })
      },
    })
  }

  const missing = inventory.variables.filter((v) => v.source === 'required').length

  return (
    <section aria-labelledby="env-inventory-title" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id="env-inventory-title" ref={headingRef} tabIndex={-1} className="text-lg outline-none">
          Environment inventory
        </h3>
        {canManage && draft === null && (
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
        Values are encrypted in AIDE, synced to each target on save and before each deployment, and never shown again.
        {prefix ? ` Public variables start with ${prefix}.` : ''}
      </p>
      {missing > 0 && (
        <Alert tone="warning" title={`${String(missing)} required variable${missing === 1 ? ' is' : 's are'} missing`}>
          <p>The stack needs {missing === 1 ? 'it' : 'them'}. Deployments without {missing === 1 ? 'it' : 'them'} are likely to fail.</p>
        </Alert>
      )}
      {notice && (
        <Alert tone={notice.tone} role={notice.tone === 'error' ? 'alert' : 'status'} title={notice.title}>
          <p>{notice.message}</p>
        </Alert>
      )}
      {draft && (
        <EnvVarForm
          key={`${draft.key}:${draft.title}`}
          projectId={projectId}
          publicPrefix={prefix}
          draft={draft}
          onCancel={() => {
            setDraft(null)
          }}
          onDone={({ key, synced }) => {
            setDraft(null)
            if (synced) {
              toast.success(`${key} saved and synced.`)
            } else {
              setNotice({ tone: 'warning', title: `${key} saved, not yet synced`, message: messageFor('UPSTREAM_ERROR', 'deploymentEnvSave') })
            }
          }}
        />
      )}
      {inventory.variables.length === 0 ? (
        <p className="text-sm text-muted-foreground">No environment variables yet.</p>
      ) : (
        <ul aria-label="Environment variables" className="divide-y divide-border/60 rounded-lg border border-border/60">
          {inventory.variables.map((v) => (
            <li key={v.key} aria-label={`Variable ${v.key}`} className="flex min-w-0 flex-col gap-2 p-3 md:flex-row md:items-start md:justify-between">
              <div className="flex min-w-0 flex-col gap-1 text-sm">
                <span className="flex flex-wrap items-center gap-2">
                  <code className="font-mono font-semibold break-all">{v.key}</code>
                  <StatusBadge tone={envSourceTones[v.source]}>
                    <span data-testid="env-source">{envSourceLabels[v.source]}</span>
                  </StatusBadge>
                  {v.public && <StatusBadge tone="info">Public</StatusBadge>}
                  {v.sensitive && <StatusBadge tone="neutral">Sensitive</StatusBadge>}
                </span>
                <dl className="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-0.5">
                  <dt className="text-muted-foreground">Components</dt>
                  <dd data-testid="env-components">{v.appliesTo.map((c) => componentLabels[c]).join(', ')}</dd>
                  <dt className="text-muted-foreground">Environments</dt>
                  <dd data-testid="env-environments">{v.environments.map((t) => deploymentTargetLabels[t]).join(', ')}</dd>
                </dl>
                {v.source === 'derived' && v.derivedFrom && <span data-testid="env-derived-from">From {v.derivedFrom} · read-only</span>}
                {v.description && <span className="text-muted-foreground">{v.description}</span>}
                {v.source === 'user' && v.updatedAt && (
                  <span>
                    {v.hasValue ? 'Value set' : 'No value'} · updated <RelativeTime iso={v.updatedAt} />
                  </span>
                )}
                <SyncState v={v} />
              </div>
              {canManage && v.source === 'user' && (
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="secondary"
                    aria-label={`Replace the value of ${v.key}`}
                    onClick={() => {
                      open({ ...v, keyLocked: true, title: `Replace ${v.key}` })
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
              )}
              {canManage && v.source === 'required' && (
                <Button
                  variant="secondary"
                  aria-label={`Add a value for ${v.key}`}
                  onClick={() => {
                    open({ ...v, keyLocked: true, title: `Add ${v.key}` })
                  }}
                >
                  Add value
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
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
        <p>It is deleted in AIDE and on the deployment platforms. Deployments after that build without it.</p>
      </ConfirmDialog>
    </section>
  )
}
