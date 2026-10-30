import { zodResolver } from '@hookform/resolvers/zod'
import { useMemo, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import type { Schemas } from '../../api/client'
import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { RadioGroup } from '../../components/ui/radio-group'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { errorMessage, isApiError, platformCopy } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import { useCurrentUser } from '../auth/auth-api'
import { connectionLabel, credentialStatusLabels } from '../connections/connection-format'
import { CredentialStatusBadge } from '../connections/credential-status-badge'
import { isPlatformProvider, usePlatformConnections, usePlatformResources } from '../connections/platform-connections-api'
import type { Project } from '../projects/projects-api'
import {
  capabilityLines,
  componentLabels,
  deploymentProviderLabels,
  isSetupActive,
  readinessItemLabels,
  readinessStateLabels,
  readinessStateTones,
  resourceNouns,
  resourcePickerTypes,
  safeUrl,
  setupStepLabels,
  targetBindingSummary,
  targetStatusLabels,
  targetStatusTones,
} from './deployment-format'
import { DeploymentLink } from './deployment-parts'
import { DeploymentProviderLogo } from './deployment-provider-logo'
import {
  useDeleteDeploymentTarget,
  usePutDeploymentTarget,
  useVerifyDeploymentTarget,
  type TargetCard,
} from './deployments-api'
import { PlatformGitAlert } from './platform-git-alert'

const providerName = (t: Pick<TargetCard, 'provider'>) => deploymentProviderLabels[t.provider]

/** An external fix link (https only), opening in a new tab. */
function FixLink({ href, children }: { href: string | null; children: string }) {
  const safe = safeUrl(href)
  if (!safe) {
    return null
  }
  return (
    <a href={safe} target="_blank" rel="noreferrer noopener" className="font-medium text-primary underline underline-offset-2">
      {children}
      <span className="sr-only"> (opens in a new tab)</span>
    </a>
  )
}

/** FR-144: whose connection the target uses ("connected by …"), or GitHub Pages' App installation. */
function BindingLine({ target: t }: { target: TargetCard }) {
  if (t.binding) {
    return (
      <span className="flex flex-wrap items-center gap-2">
        <span data-testid="target-binding">
          {providerName(t)} · {targetBindingSummary(t.binding)}
        </span>
        <CredentialStatusBadge status={t.binding.status} />
      </span>
    )
  }
  return (
    <span data-testid="target-binding">
      {t.needsConnection
        ? 'No connection bound yet.'
        : "No extra connection: connected by the project's GitHub App installation (or its access token)."}
    </span>
  )
}

function TargetFacts({ target: t }: { target: TargetCard }) {
  const showReason = t.statusReason && !(t.setup?.state === 'failed' && t.setup.message === t.statusReason)
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
      <dt className="text-muted-foreground">Connection</dt>
      <dd className="min-w-0">
        <BindingLine target={t} />
      </dd>
      <dt className="text-muted-foreground">{resourceNouns[t.provider].replace(/^./, (c) => c.toUpperCase())}</dt>
      <dd className="min-w-0 break-all" data-testid="target-resource">
        {t.name || t.resourceId ? (
          <>
            <span className="font-medium">{t.name ?? t.resourceId}</span>
            {t.resourceId && t.resourceId !== t.name && (
              <>
                {' '}
                <code className="font-mono text-xs text-muted-foreground">{t.resourceId}</code>
              </>
            )}
          </>
        ) : (
          'Not created or linked yet'
        )}
      </dd>
      <dt className="text-muted-foreground">Production URL</dt>
      <dd className="min-w-0">
        <DeploymentLink url={t.productionUrl} />
      </dd>
      <dt className="text-muted-foreground">Preview URL</dt>
      <dd className="min-w-0">
        {t.capabilities.previews ? <DeploymentLink url={t.previewUrl} /> : <span>No previews on {providerName(t)}</span>}
      </dd>
      {showReason && (
        <>
          <dt className="text-muted-foreground">Why</dt>
          <dd data-testid="target-status-reason">{t.statusReason}</dd>
        </>
      )}
    </dl>
  )
}

/** FR-144: the `deployment_setup` job's steps while it runs, as text (announced politely). */
function SetupProgress({ target: t }: { target: TargetCard }) {
  if (!t.setup || !isSetupActive(t)) {
    return null
  }
  const current = t.setup.step ?? 'queued'
  const steps = ['queued', current === 'linking' ? 'linking' : 'creating', 'syncing_env']
  const index = Math.max(0, steps.indexOf(current))
  const label = (s: string) => (s in setupStepLabels ? setupStepLabels[s as keyof typeof setupStepLabels] : s)
  return (
    <div role="status" aria-live="polite" data-testid="setup-progress" className="rounded-lg border border-primary/40 bg-primary/10 p-3 text-sm">
      <p className="font-medium">Setting up: {label(current)}…</p>
      <ol aria-label="Setup steps" className="mt-2 flex flex-col gap-1">
        {steps.map((s, i) => (
          <li key={s}>
            {label(s)} <span className="text-muted-foreground">({i < index ? 'done' : i === index ? 'in progress' : 'next'})</span>
          </li>
        ))}
      </ol>
    </div>
  )
}

/** FR-144: a failed setup job: the shared copy for its code, the platform's message and the fix link. */
function SetupFailure({ target: t }: { target: TargetCard }) {
  const setup = t.setup
  if (setup?.state !== 'failed') {
    return null
  }
  const code = setup.errorCode
  const copy = platformCopy(code, t.provider, code === 'PLATFORM_PLAN_LIMIT' ? setup.message : null)
  const extra = setup.message && code !== 'PLATFORM_PLAN_LIMIT' ? setup.message : null
  if (code === 'PLATFORM_GIT_NOT_CONNECTED') {
    return (
      <PlatformGitAlert
        platform={t.provider}
        installUrl={setup.fixUrl}
        title={`The setup failed: ${providerName(t)} can't see the repository`}
        message={[copy, extra].filter(Boolean).join(' ')}
      />
    )
  }
  return (
    <Alert tone="error" role="alert" title="The setup failed">
      <p data-testid="setup-error">{copy ?? extra ?? 'The setup failed.'}</p>
      {copy && extra && <p className="mt-1">{extra}</p>}
      {setup.fixUrl && (
        <p className="mt-2">
          <FixLink href={setup.fixUrl}>
            {code === 'PLATFORM_PERMISSION_MISSING' ? 'Open the GitHub App settings' : `Fix it in ${providerName(t)}`}
          </FixLink>
        </p>
      )}
    </Alert>
  )
}

function BuildConfig({ target: t }: { target: TargetCard }) {
  const b = t.buildConfig
  const rows: [string, string | null][] = [
    ['Root directory', t.rootDirectory],
    ['Framework', b.framework],
    ['Build command', b.buildCommand],
    ['Output directory', b.outputDirectory],
    ['Start command', b.startCommand],
    ['Dockerfile', b.dockerfilePath],
    ['Health check path', b.healthCheckPath],
    ['Pre-deploy command', b.preDeployCommand],
  ]
  return (
    <details className="rounded-lg border border-border/60 bg-background/40">
      <summary className="min-h-10 cursor-pointer px-3 py-2 text-sm font-medium">Build configuration (derived from the stack)</summary>
      <dl className="grid gap-x-6 gap-y-1 border-t border-border/60 px-3 py-2 text-sm sm:grid-cols-[max-content_1fr]">
        {rows
          .filter((r): r is [string, string] => r[1] !== null && r[1] !== '')
          .map(([label, value]) => (
            <div key={label} className="contents">
              <dt className="text-muted-foreground">{label}</dt>
              <dd className="min-w-0">
                <code className="font-mono text-xs break-all">{value}</code>
              </dd>
            </div>
          ))}
      </dl>
    </details>
  )
}

function Capabilities({ target: t }: { target: TargetCard }) {
  return (
    <ul aria-label={`What ${providerName(t)} supports`} className="flex flex-col gap-0.5 text-sm text-muted-foreground">
      {capabilityLines(t).map((line) => (
        <li key={line}>{line}</li>
      ))}
    </ul>
  )
}

/** FR-144: the readiness checklist (state in words, message, fix link) and Verify for Owners. */
function Readiness({ projectId, target: t, canManage }: { projectId: string; target: TargetCard; canManage: boolean }) {
  const verify = useVerifyDeploymentTarget(projectId, t.component)
  const headingId = `readiness-${t.component}`
  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h4 id={headingId} className="font-semibold">
          Readiness
        </h4>
        {canManage && (
          <Button
            variant="secondary"
            pending={verify.isPending}
            pendingLabel="Checking…"
            aria-label={`Verify the ${t.component} target`}
            onClick={() => {
              verify.mutate(undefined, {
                onSuccess: () => {
                  toast.success(`${componentLabels[t.component]} target checked.`)
                },
              })
            }}
          >
            Verify
          </Button>
        )}
      </div>
      {verify.isError && (
        <Alert tone="error" role="alert" title="We couldn't check the target">
          <p>{errorMessage(verify.error, 'deploymentVerify')}</p>
        </Alert>
      )}
      <ul aria-label={`Readiness checklist (${t.component})`} className="divide-y divide-border/60 rounded-lg border border-border/60">
        {t.readiness.map((item) => {
          const label = readinessItemLabels[item.id] ?? item.id
          return (
            <li key={item.id} aria-label={label} className="flex flex-col gap-1 p-3 text-sm sm:flex-row sm:items-start sm:gap-3">
              <StatusBadge tone={readinessStateTones[item.state]}>
                <span data-testid="readiness-state">{readinessStateLabels[item.state]}</span>
              </StatusBadge>
              <span className="min-w-0">
                <span className="font-medium">{label}:</span> {item.message}{' '}
                <FixLink href={item.fixUrl}>{`Fix: ${label}`}</FixLink>
              </span>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

interface SetupValues {
  connectionId: string
  mode: 'keep' | 'create' | 'link'
  name: string
  region: string
  plan: string
  resourceId: string
}

/** Server VALIDATION keys → form fields. */
const serverFields: Record<string, keyof SetupValues> = {
  connectionid: 'connectionId',
  'create.name': 'name',
  'create.region': 'region',
  'create.plan': 'plan',
  'link.resourceid': 'resourceId',
  create: 'mode',
  link: 'mode',
}

/**
 * The setup form's rules (FR-144, matching the API): every provider but GitHub Pages needs one of the caller's own
 * connections on every PUT; `keep` (the target already has a resource) rebinds it, `create` or `link` need their fields.
 */
function setupSchema(t: TargetCard) {
  const name = providerName(t)
  const noun = resourceNouns[t.provider]
  return z
    .object({
      connectionId: z.string(),
      mode: z.enum(['keep', 'create', 'link']),
      name: z.string(),
      region: z.string(),
      plan: z.string(),
      resourceId: z.string(),
    })
    .superRefine((v, ctx) => {
      // GitHub Pages: no connection and no resource to pick (setup turns Pages on for the repository).
      if (!t.needsConnection) {
        return
      }
      if (!v.connectionId) {
        ctx.addIssue({ code: 'custom', path: ['connectionId'], message: `Pick one of your ${name} connections.` })
      }
      if (v.mode === 'create') {
        const n = v.name.trim()
        if (!n) {
          ctx.addIssue({ code: 'custom', path: ['name'], message: `Enter a name for the new ${noun}.` })
        } else if (n.length > 100) {
          ctx.addIssue({ code: 'custom', path: ['name'], message: 'Names are at most 100 characters.' })
        }
      }
      if (v.mode === 'link' && !v.resourceId.trim()) {
        ctx.addIssue({ code: 'custom', path: ['resourceId'], message: `Pick the ${noun} to link.` })
      }
      if (v.mode === 'keep' && !t.resourceId) {
        ctx.addIssue({ code: 'custom', path: ['mode'], message: `Create or link a ${noun}.` })
      }
    })
}

/** A failed PUT: PLATFORM_PERMISSION_MISSING (GitHub App settings link), PLATFORM_GIT_NOT_CONNECTED, or the shared copy. */
function SetupError({ error, target: t }: { error: unknown; target: TargetCard }) {
  if (isApiError(error, 'PLATFORM_PERMISSION_MISSING')) {
    return (
      <Alert tone="error" role="alert" title={`${providerName(t)} can't be set up yet`}>
        <p>{errorMessage(error)}</p>
        {error.installUrl && (
          <p className="mt-2">
            <a href={error.installUrl} target="_blank" rel="noreferrer noopener" className={buttonClasses('secondary')}>
              Open the GitHub App settings
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          </p>
        )}
      </Alert>
    )
  }
  if (isApiError(error, 'PLATFORM_GIT_NOT_CONNECTED')) {
    return <PlatformGitAlert error={error} platform={error.platform ?? t.provider} message={errorMessage(error)} />
  }
  return (
    <Alert tone="error" role="alert" title="We couldn't set up the target">
      <p>{error instanceof Error && !isApiError(error) ? error.message : errorMessage(error, 'deploymentTarget')}</p>
    </Alert>
  )
}

/** FR-144 (Owner): bind one of the caller's connections and create or link the platform resource. */
function TargetSetupForm({ project, target: t, onDone }: { project: Project; target: TargetCard; onDone: () => void }) {
  const put = usePutDeploymentTarget(project.id, t.component)
  const me = useCurrentUser()
  const name = providerName(t)
  const noun = resourceNouns[t.provider]
  const pickerType = resourcePickerTypes[t.provider]
  const myBinding = t.binding !== null && t.binding.boundBy.id === me?.id
  const schema = useMemo(() => setupSchema(t), [t])
  const [failure, setFailure] = useState<unknown>(null)
  const ids = {
    connectionId: `${t.component}-connection`,
    mode: `${t.component}-mode`,
    name: `${t.component}-name`,
    region: `${t.component}-region`,
    plan: `${t.component}-plan`,
    resourceId: `${t.component}-resource`,
  } as const
  const suggested = `${project.slug || 'app'}-${t.component === 'frontend' ? 'web' : 'api'}`.slice(0, 100)
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<SetupValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      connectionId: myBinding ? (t.binding?.connectionId ?? '') : '',
      mode: t.resourceId ? 'keep' : 'create',
      name: t.name ?? suggested,
      region: '',
      plan: '',
      resourceId: '',
    },
  })
  const mode = useWatch({ control, name: 'mode' })
  const connectionId = useWatch({ control, name: 'connectionId' })
  const platform = isPlatformProvider(t.provider) ? t.provider : undefined
  const connections = usePlatformConnections({ enabled: t.canBind && platform !== undefined, ...(platform ? { provider: platform } : {}) })
  const own = connections.data ?? []
  const selected = own.find((c) => c.id === connectionId)
  const resources = usePlatformResources(mode === 'link' && connectionId ? connectionId : null, pickerType)

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    // GitHub Pages: the provider only. Others: always the chosen connection; `keep` is a rebind (no create or link).
    const body: Schemas['PutDeploymentTargetRequest'] = !t.needsConnection
      ? { provider: t.provider }
      : {
          provider: t.provider,
          connectionId: v.connectionId,
          ...(v.mode === 'create'
            ? {
                create: {
                  name: v.name.trim(),
                  ...(v.region.trim() ? { region: v.region.trim() } : {}),
                  ...(v.plan.trim() ? { plan: v.plan.trim() } : {}),
                },
              }
            : v.mode === 'link'
              ? { link: { resourceId: v.resourceId.trim() } }
              : {}),
        }
    put.mutate(body, {
      onSuccess: () => {
        toast.success(`Setting up the ${t.component} on ${name}.`)
        onDone()
      },
      onError: (error) => {
        if (isApiError(error, 'VALIDATION') && Object.keys(error.fieldErrors).length > 0) {
          const unmatched: string[] = []
          for (const [key, messages] of Object.entries(error.fieldErrors)) {
            const field = serverFields[key.toLowerCase()]
            if (field) {
              setError(field, { type: 'server', message: messages.join(' ') })
            } else {
              unmatched.push(messages.join(' '))
            }
          }
          setFailure(unmatched.length > 0 ? new Error(unmatched.join(' ')) : null)
          return
        }
        setFailure(error)
      },
    })
  })

  const modeOptions = [
    ...(t.resourceId ? [{ value: 'keep', label: `Keep ${t.name ?? t.resourceId}`, description: 'Only rebind it to the connection above; AIDE checks the setup again.' }] : []),
    { value: 'create', label: `Create a new ${name} ${noun}`, description: `Linked to the project's repository, built as shown below.` },
    { value: 'link', label: `Link an existing ${name} ${noun}`, description: `One your connection can reach.` },
  ]

  return (
    <form
      noValidate
      aria-label={`Set up the ${t.component} on ${name}`}
      onSubmit={(e) => void onSubmit(e)}
      className="flex flex-col gap-4 rounded-lg border border-border/60 bg-background/40 p-4"
    >
      <ErrorSummary title="Check the target's settings" errors={isSubmitted ? fieldErrorSummary(errors, ids) : []} />
      {failure !== null && <SetupError error={failure} target={t} />}
      {t.needsConnection && (
        <div className="flex flex-col gap-2">
          {connections.isPending && <LoadingState label={`Loading your ${name} connections…`} />}
          {connections.isError && (
            <ErrorState error={connections.error} title={`Your ${name} connections couldn't load`} onRetry={() => void connections.refetch()} />
          )}
          {connections.isSuccess && (
            <SelectField
              id={ids.connectionId}
              label={`${name} connection`}
              hint="Bind one of your own tokens. Every deployment of this target uses it, whoever triggers it."
              error={errors.connectionId?.message}
              {...register('connectionId')}
            >
              <option value="">Pick a connection</option>
              {own.map((c) => (
                <option key={c.id} value={c.id}>
                  {connectionLabel(c)}
                  {c.accountName ? ` · ${c.accountName}` : ''}
                  {c.status === 'valid' ? '' : ` (${credentialStatusLabels[c.status].toLowerCase()})`}
                </option>
              ))}
            </SelectField>
          )}
          {connections.isSuccess && own.length === 0 && (
            <p className="text-sm text-muted-foreground">You have no {name} connections yet.</p>
          )}
          {selected?.status === 'invalid' && (
            <p className="text-sm text-destructive">{name} rejected this token; setting up with it will fail.</p>
          )}
          <p>
            <Link to="/connections" className="text-sm font-medium text-primary underline underline-offset-2">
              Add a token in Connections
            </Link>
          </p>
        </div>
      )}
      {t.needsConnection ? (
        <>
          <RadioGroup
            id={ids.mode}
            legend={`Which ${name} ${noun} deploys the ${t.component}?`}
            inputProps={register('mode')}
            error={errors.mode?.message}
            options={modeOptions}
          />
          {mode === 'create' && (
            <div className="grid gap-4 sm:grid-cols-3">
              <TextField
                id={ids.name}
                label="Name"
                autoComplete="off"
                spellCheck={false}
                maxLength={100}
                className="sm:col-span-3"
                error={errors.name?.message}
                {...register('name')}
              />
              <TextField
                id={ids.region}
                label="Region (optional)"
                hint={`${name}'s default when empty.`}
                autoComplete="off"
                spellCheck={false}
                maxLength={64}
                error={errors.region?.message}
                {...register('region')}
              />
              <TextField
                id={ids.plan}
                label="Plan (optional)"
                hint={`${name}'s default when empty.`}
                autoComplete="off"
                spellCheck={false}
                maxLength={64}
                error={errors.plan?.message}
                {...register('plan')}
              />
            </div>
          )}
          {mode === 'link' &&
            (pickerType === null ? (
              <TextField
                id={ids.resourceId}
                label={`${name} ${noun} id`}
                autoComplete="off"
                spellCheck={false}
                maxLength={200}
                error={errors.resourceId?.message}
                {...register('resourceId')}
              />
            ) : !connectionId ? (
              <p className="text-sm text-muted-foreground">Pick a connection first: the list shows what it can reach.</p>
            ) : resources.isPending ? (
              <LoadingState label={`Loading your ${name} ${noun}s…`} />
            ) : resources.isError ? (
              <ErrorState error={resources.error} title={`Your ${name} ${noun}s couldn't load`} onRetry={() => void resources.refetch()} />
            ) : (
              <SelectField id={ids.resourceId} label={`Existing ${noun}`} error={errors.resourceId?.message} {...register('resourceId')}>
                <option value="">{resources.data.length === 0 ? `No ${noun}s found` : `Pick a ${noun}`}</option>
                {resources.data.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.name}
                    {r.detail ? ` · ${r.detail}` : ''}
                  </option>
                ))}
              </SelectField>
            ))}
        </>
      ) : (
        <p className="text-sm text-muted-foreground">
          {name} needs no extra connection: AIDE uses the project&apos;s GitHub App installation (or its access token) and turns
          on Pages for the repository.
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" pending={put.isPending} pendingLabel="Starting…">
          {t.resourceId ? 'Save target' : `Set up ${name}`}
        </Button>
        {t.resourceId && (
          <Button variant="ghost" onClick={onDone} disabled={put.isPending}>
            Cancel
          </Button>
        )}
      </div>
    </form>
  )
}

/**
 * Settings → Deployment target card (FR-143, FR-144): the provider, whose connection it uses, the resource, URLs,
 * status, setup progress or failure, the derived build configuration, capabilities and the readiness checklist.
 * Owners set it up (bind, create or link), verify it and unbind it.
 */
export function DeploymentTargetCard({ project, target: t, canManage }: { project: Project; target: TargetCard; canManage: boolean }) {
  const name = providerName(t)
  const component = componentLabels[t.component]
  const configured = t.resourceId !== null || t.binding !== null
  const active = isSetupActive(t)
  const [editing, setEditing] = useState(false)
  const [confirmUnbind, setConfirmUnbind] = useState(false)
  const unbind = useDeleteDeploymentTarget(project.id, t.component)
  const headingId = `target-${t.component}-title`
  const showForm = canManage && !active && (editing || (!configured && t.status !== 'configuring'))

  return (
    <section aria-labelledby={headingId} data-testid={`target-${t.component}`} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-4')}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={headingId} className="flex items-center gap-2 text-lg">
          <DeploymentProviderLogo provider={t.provider} className="size-5" />
          {component} on {name}
        </h3>
        <StatusBadge tone={targetStatusTones[t.status]} pulse={active}>
          <span data-testid="target-status">{targetStatusLabels[t.status]}</span>
        </StatusBadge>
      </div>
      {!t.inComposition && (
        <Alert tone="warning" title="No longer in the stack">
          <p>The stack no longer hosts the {t.component} on {name}. Unbind this target, then set up the new host.</p>
        </Alert>
      )}
      {!t.implementedForReal && !t.fakeMode && (
        <Alert tone="info" title={`${name} deployments aren't available yet`}>
          <p>AIDE can record this target, but it doesn&apos;t deploy to {name} for real yet.</p>
        </Alert>
      )}
      <TargetFacts target={t} />
      <SetupProgress target={t} />
      <SetupFailure target={t} />
      <BuildConfig target={t} />
      <Capabilities target={t} />
      <Readiness projectId={project.id} target={t} canManage={canManage} />
      {showForm && (
        <TargetSetupForm
          project={project}
          target={t}
          onDone={() => {
            setEditing(false)
          }}
        />
      )}
      {canManage && !active && !showForm && (
        <div className="flex flex-wrap gap-2">
          <Button
            variant="secondary"
            aria-label={`Change the ${t.component} target`}
            onClick={() => {
              setEditing(true)
            }}
          >
            Change setup
          </Button>
          {configured && (
            <Button
              variant="ghost"
              aria-label={`Unbind the ${t.component} target`}
              onClick={() => {
                setConfirmUnbind(true)
              }}
            >
              Unbind
            </Button>
          )}
        </div>
      )}
      {unbind.isError && (
        <Alert tone="error" role="alert" title="We couldn't unbind the target">
          <p>{errorMessage(unbind.error, 'deploymentUnbind')}</p>
        </Alert>
      )}
      <ConfirmDialog
        open={confirmUnbind}
        title={`Unbind the ${t.component} target?`}
        confirmLabel="Unbind"
        pendingLabel="Unbinding…"
        pending={unbind.isPending}
        destructive
        onConfirm={() => {
          unbind.mutate(undefined, {
            onSettled: () => {
              setConfirmUnbind(false)
            },
            onSuccess: () => {
              toast.success(`The ${t.component} target is unbound.`)
            },
          })
        }}
        onCancel={() => {
          setConfirmUnbind(false)
        }}
      >
        <p>
          AIDE stops deploying the {t.component} to {name}. Nothing is deleted on {name}, and the deployment history is kept.
        </p>
      </ConfirmDialog>
    </section>
  )
}
