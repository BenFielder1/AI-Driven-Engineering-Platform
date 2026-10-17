import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import type { Schemas } from '../../api/client'
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
import { errorMessage, isApiError } from '../../lib/errors'
import { connectionLabel, credentialStatusLabels } from '../connections/connection-format'
import { CredentialStatusBadge } from '../connections/credential-status-badge'
import { useVercelConnections } from '../connections/vercel-connections-api'
import { useCurrentUser } from '../auth/auth-api'
import type { Project } from '../projects/projects-api'
import { deploymentBindingSummary, deploymentTargetLabels } from './deployment-format'
import { DeploymentLink } from './deployment-parts'
import { useDeploymentSettings, useUpdateDeploymentSettings, type DeploymentSettings, type RequiredEnvVar } from './deployments-api'
import { EnvVarsSection } from './env-vars-section'
import { VercelGitAlert } from './vercel-git-alert'
import { checkRootDirectory, checkVercelProjectName, suggestVercelProjectName } from './vercel-project-rules'

/** FR-88: the preset's required env vars, each flagged present or missing. */
function RequiredEnvList({ required }: { required: readonly RequiredEnvVar[] }) {
  if (required.length === 0) {
    return null
  }
  const missing = required.filter((r) => !r.present).length
  return (
    <section aria-labelledby="required-env-title" className="flex flex-col gap-2">
      <h3 id="required-env-title" className="text-lg">
        Required by the preset
      </h3>
      {missing > 0 && (
        <Alert tone="warning" title={`${String(missing)} required variable${missing === 1 ? ' is' : 's are'} missing`}>
          <p>Deployments build without them and are likely to fail. Add them under Environment variables.</p>
        </Alert>
      )}
      <ul aria-label="Required environment variables" className="divide-y divide-border/60 rounded-lg border border-border/60">
        {required.map((r) => (
          <li key={r.key} aria-label={`Required ${r.key}`} className="flex flex-col gap-1 p-3 text-sm">
            <span className="flex flex-wrap items-center gap-2">
              <code className="font-mono font-semibold break-all">{r.key}</code>
              <StatusBadge tone={r.present ? 'success' : 'warning'}>
                <span data-testid="required-env-status">{r.present ? 'Set' : 'Missing'}</span>
              </StatusBadge>
              {r.sensitive && <StatusBadge tone="neutral">Sensitive</StatusBadge>}
            </span>
            <span className="text-muted-foreground">
              {r.description} ({r.targets.map((t) => deploymentTargetLabels[t]).join(', ')})
            </span>
          </li>
        ))}
      </ul>
    </section>
  )
}

function CurrentSetup({ settings }: { settings: DeploymentSettings }) {
  const s = settings
  return (
    <dl className="grid gap-2 rounded-lg border border-border/60 p-4 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <dt className="text-muted-foreground">Provider</dt>
        <dd data-testid="deployment-binding" className="flex flex-wrap items-center gap-2">
          {deploymentBindingSummary(s)}
          {s.binding && <CredentialStatusBadge status={s.binding.status} />}
        </dd>
      </div>
      <div className="flex flex-wrap gap-2">
        <dt className="text-muted-foreground">Vercel project</dt>
        <dd>
          {s.vercelProject ? (
            <>
              <span className="font-medium">{s.vercelProject.name}</span>{' '}
              <code className="font-mono text-xs text-muted-foreground">{s.vercelProject.id}</code>
            </>
          ) : (
            'None linked yet'
          )}
        </dd>
      </div>
      {s.vercelProject?.productionUrl && (
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Production URL</dt>
          <dd className="min-w-0">
            <DeploymentLink url={s.vercelProject.productionUrl} />
          </dd>
        </div>
      )}
      <div className="flex flex-wrap gap-2">
        <dt className="text-muted-foreground">Builds</dt>
        <dd>
          <code className="font-mono text-xs">{s.rootDirectory}</code> · {s.framework}
        </dd>
      </div>
    </dl>
  )
}

const setupSchema = z
  .object({
    connectionId: z.string(),
    projectMode: z.enum(['keep', 'create', 'link']),
    projectName: z.string(),
    rootDirectory: z.string(),
    vercelProjectId: z.string(),
    /** Whether a connection must be picked (enabling, or nothing bound yet). */
    needsConnection: z.boolean(),
    /** Another Owner bound the connection: changing the project rebinds it to one of the caller's own (FR-88). */
    boundByOther: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.needsConnection && !v.connectionId) {
      ctx.addIssue({ code: 'custom', path: ['connectionId'], message: 'Pick one of your Vercel connections.' })
    } else if (v.boundByOther && v.projectMode !== 'keep' && !v.connectionId) {
      ctx.addIssue({ code: 'custom', path: ['connectionId'], message: rebindMessage })
    }
    if (v.projectMode === 'create') {
      const name = checkVercelProjectName(v.projectName)
      if (name) {
        ctx.addIssue({ code: 'custom', path: ['projectName'], message: name })
      }
      const root = checkRootDirectory(v.rootDirectory)
      if (root) {
        ctx.addIssue({ code: 'custom', path: ['rootDirectory'], message: root })
      }
    }
    if (v.projectMode === 'link' && !v.vercelProjectId.trim()) {
      ctx.addIssue({ code: 'custom', path: ['vercelProjectId'], message: 'Enter the Vercel project id (prj_…) or name.' })
    }
  })

type SetupValues = z.infer<typeof setupSchema>

const rebindMessage = 'Changing the Vercel project uses your own Vercel connection. Pick one of yours.'

const setupIds = {
  connectionId: 'deployment-connection',
  projectMode: 'deployment-project-mode',
  projectName: 'deployment-project-name',
  rootDirectory: 'deployment-root-directory',
  vercelProjectId: 'deployment-vercel-project',
  needsConnection: 'deployment-connection',
  boundByOther: 'deployment-connection',
} as const

/** Server VALIDATION keys → form fields. */
const serverFields: Record<string, keyof SetupValues> = {
  connectionid: 'connectionId',
  'create.name': 'projectName',
  'create.rootdirectory': 'rootDirectory',
  vercelprojectid: 'vercelProjectId',
  create: 'projectMode',
}

/** FR-88, Owners: enable Vercel, bind an own connection, create or link the Vercel project. */
function DeploymentSetupForm({ project, settings }: { project: Project; settings: DeploymentSettings }) {
  const update = useUpdateDeploymentSettings(project.id)
  const turnOff = useUpdateDeploymentSettings(project.id)
  const connections = useVercelConnections(settings.canBind)
  const me = useCurrentUser()
  const boundByOther = settings.binding !== null && settings.binding.boundBy.id !== me?.id
  const [failure, setFailure] = useState<unknown>(null)
  const [confirmOff, setConfirmOff] = useState(false)
  const enabled = settings.provider !== null
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<SetupValues>({
    resolver: zodResolver(setupSchema),
    defaultValues: {
      // Another Owner's connection isn't in the caller's list: '' keeps it (and changing the project needs one of theirs).
      connectionId: boundByOther ? '' : (settings.binding?.connectionId ?? ''),
      projectMode: settings.vercelProject ? 'keep' : 'create',
      projectName: suggestVercelProjectName(project.slug || project.name),
      rootDirectory: settings.rootDirectory === '.' ? '' : settings.rootDirectory,
      vercelProjectId: '',
      needsConnection: settings.canBind && settings.binding === null,
      boundByOther,
    },
  })
  const projectMode = useWatch({ control, name: 'projectMode' })
  const connectionId = useWatch({ control, name: 'connectionId' })

  const own = connections.data ?? []
  const boundIsMine = own.some((c) => c.id === settings.binding?.connectionId)
  const selected = own.find((c) => c.id === connectionId)

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    const body: Schemas['UpdateDeploymentSettingsRequest'] = {
      provider: settings.provider ?? 'vercel',
      ...(settings.canBind && v.connectionId && v.connectionId !== settings.binding?.connectionId ? { connectionId: v.connectionId } : {}),
      ...(v.projectMode === 'create'
        ? { create: { name: v.projectName.trim(), rootDirectory: v.rootDirectory.trim() || '.' } }
        : v.projectMode === 'link'
          ? { vercelProjectId: v.vercelProjectId.trim() }
          : {}),
    }
    update.mutate(body, {
      onSuccess: () => {
        toast.success(enabled ? 'Deployment settings saved.' : 'Vercel deployment is set up.')
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

  const doTurnOff = () => {
    turnOff.mutate(
      { provider: null },
      {
        onSuccess: () => {
          setConfirmOff(false)
          toast.success('Deployment turned off. The deployment history is kept.')
        },
        onError: (error) => {
          setConfirmOff(false)
          setFailure(error)
        },
      },
    )
  }

  return (
    <form noValidate aria-labelledby="deployment-setup-title" onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-4">
      <h3 id="deployment-setup-title" className="text-lg">
        {enabled ? 'Vercel setup' : 'Set up Vercel'}
      </h3>
      {!enabled && (
        <p className="text-sm text-muted-foreground">
          {settings.supported
            ? "This project's preset deploys to Vercel. Bind one of your Vercel connections and create or link the Vercel project."
            : "This project's preset doesn't deploy anywhere by default. You can still opt in to Vercel for a Next.js app."}
        </p>
      )}
      <ErrorSummary title="We couldn't save the deployment settings" errors={isSubmitted ? fieldErrorSummary(errors, setupIds) : []} />
      {failure === null && settings.gitConnected === false && <VercelGitAlert installUrl={settings.installUrl} />}
      {failure !== null &&
        (isApiError(failure, 'VERCEL_GIT_NOT_CONNECTED') ? (
          <VercelGitAlert error={failure} installUrl={settings.installUrl} />
        ) : (
          <Alert tone="error" role="alert" title="We couldn't save the deployment settings">
            <p>{failure instanceof Error && !isApiError(failure) ? failure.message : errorMessage(failure, 'deploymentSettings')}</p>
          </Alert>
        ))}

      <fieldset className="flex flex-col gap-3 rounded-lg border border-border/60 p-4">
        <legend className="px-1 text-sm font-medium">Connection</legend>
        {settings.binding && <p className="text-sm">{deploymentBindingSummary(settings)}</p>}
        {settings.canBind ? (
          <>
            {connections.isPending && <LoadingState label="Loading your Vercel connections…" />}
            {connections.isError && (
              <ErrorState error={connections.error} title="Your Vercel connections couldn't load" onRetry={() => void connections.refetch()} />
            )}
            {connections.isSuccess && (
              <SelectField
                id={setupIds.connectionId}
                label="Vercel connection"
                hint={
                  boundByOther && projectMode !== 'keep'
                    ? 'Changing the Vercel project uses your own Vercel connection: pick one of yours, and the project is rebound to it.'
                    : 'Bind one of your own tokens. Every deployment of this project uses it, whoever triggers it.'
                }
                error={errors.connectionId?.message}
                {...register('connectionId')}
              >
                <option value="">{settings.binding && !boundIsMine ? 'Keep the bound connection' : 'Pick a connection'}</option>
                {own.map((c) => (
                  <option key={c.id} value={c.id}>
                    {connectionLabel(c)}
                    {c.teamSlug ? ` · team ${c.teamSlug}` : ''}
                    {c.status === 'valid' ? '' : ` (${credentialStatusLabels[c.status].toLowerCase()})`}
                  </option>
                ))}
              </SelectField>
            )}
            {connections.isSuccess && own.length === 0 && (
              <p className="text-sm text-muted-foreground">You have no Vercel connections yet.</p>
            )}
            {selected?.status === 'invalid' && (
              <p className="text-sm text-destructive">Vercel rejected this token; deployments with it will fail.</p>
            )}
            <p>
              <Link to="/connections" className={buttonClasses('secondary')}>
                Add a token in Connections
              </Link>
            </p>
          </>
        ) : (
          <p className="text-sm text-muted-foreground">Only project Owners can bind one of their own Vercel connections.</p>
        )}
      </fieldset>

      <fieldset className="flex flex-col gap-3 rounded-lg border border-border/60 p-4">
        <legend className="px-1 text-sm font-medium">Vercel project</legend>
        <RadioGroup
          id={setupIds.projectMode}
          legend="Which Vercel project deploys this repository?"
          inputProps={register('projectMode')}
          error={errors.projectMode?.message}
          options={[
            ...(settings.vercelProject
              ? [{ value: 'keep', label: `Keep ${settings.vercelProject.name}`, description: 'The linked project stays as it is.' }]
              : []),
            {
              value: 'create',
              label: 'Create a new Vercel project',
              description: "Linked to the project's GitHub repository, framework Next.js.",
            },
            { value: 'link', label: 'Link an existing Vercel project', description: 'One the connection can access, by id or name.' },
          ]}
        />
        {projectMode === 'create' && (
          <div className="grid gap-4 sm:grid-cols-2">
            <TextField
              id={setupIds.projectName}
              label="Project name"
              hint="Lowercase letters, digits, '.', '_' and '-'."
              autoComplete="off"
              spellCheck={false}
              maxLength={100}
              error={errors.projectName?.message}
              {...register('projectName')}
            />
            <TextField
              id={setupIds.rootDirectory}
              label="Root directory (optional)"
              hint="The app's directory in the repository. Empty: the repository root."
              autoComplete="off"
              spellCheck={false}
              maxLength={255}
              error={errors.rootDirectory?.message}
              {...register('rootDirectory')}
            />
          </div>
        )}
        {projectMode === 'link' && (
          <TextField
            id={setupIds.vercelProjectId}
            label="Vercel project id or name"
            hint="For example prj_abc123 or my-app, from the project's settings in Vercel."
            autoComplete="off"
            spellCheck={false}
            maxLength={100}
            error={errors.vercelProjectId?.message}
            {...register('vercelProjectId')}
          />
        )}
      </fieldset>

      <div className="flex flex-wrap gap-2">
        <Button type="submit" pending={update.isPending} pendingLabel="Saving…">
          {enabled ? 'Save deployment settings' : 'Set up Vercel'}
        </Button>
        {enabled && (
          <Button
            variant="ghost"
            onClick={() => {
              setConfirmOff(true)
            }}
          >
            Turn off deployment
          </Button>
        )}
      </div>
      <ConfirmDialog
        open={confirmOff}
        title="Turn off deployment?"
        confirmLabel="Turn off"
        pendingLabel="Turning off…"
        pending={turnOff.isPending}
        destructive
        onConfirm={doTurnOff}
        onCancel={() => {
          setConfirmOff(false)
        }}
      >
        <p>
          AIDE stops deploying previews and production for this project. The deployment history and environment variables are
          kept, and nothing is deleted in Vercel.
        </p>
      </ConfirmDialog>
    </form>
  )
}

/**
 * Settings → Deployment (FR-88). Every member reads it: the provider, "Vercel · connected by <name>", the linked
 * project and the preset's required env vars (missing ones flagged). Owners set Vercel up (bind an own connection,
 * create or link the Vercel project) and edit the env vars. A Local Git project can't deploy.
 */
export function DeploymentSettingsTab({ project, canEdit }: { project: Project; canEdit: boolean }) {
  const settings = useDeploymentSettings(project.id)

  let body
  if (settings.isPending) {
    body = <LoadingState label="Loading the deployment settings…" />
  } else if (settings.isError) {
    body = <ErrorState error={settings.error} onRetry={() => void settings.refetch()} title="The deployment settings couldn't load" />
  } else {
    const s = settings.data
    const enabled = s.provider !== null
    body = (
      <div className="flex flex-col gap-6">
        {enabled ? (
          <CurrentSetup settings={s} />
        ) : (
          !canEdit && (
            <p className="text-sm text-muted-foreground">
              Deployment isn&apos;t set up for this project. An Owner can set up Vercel here.
            </p>
          )
        )}
        {!canEdit && s.gitConnected === false && <VercelGitAlert installUrl={s.installUrl} />}
        {canEdit && <DeploymentSetupForm key={`${s.provider ?? 'off'}:${s.binding?.connectionId ?? ''}:${s.vercelProject?.id ?? ''}`} project={project} settings={s} />}
        {(enabled || s.supported) && <RequiredEnvList required={s.requiredEnv} />}
        {canEdit && enabled && <EnvVarsSection projectId={project.id} required={s.requiredEnv} />}
      </div>
    )
  }

  return (
    <section aria-labelledby="deployment-title" className="max-w-3xl">
      <h2 id="deployment-title" className="mb-4 text-xl">
        Deployment
      </h2>
      {body}
    </section>
  )
}
