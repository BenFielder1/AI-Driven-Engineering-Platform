import { zodResolver } from '@hookform/resolvers/zod'
import { useMemo, useState, type ReactNode } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, isApiError } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { connectionLabel } from '../connections/connection-format'
import { CredentialStatusBadge } from '../connections/credential-status-badge'
import { isPlatformProvider, usePlatformConnections } from '../connections/platform-connections-api'
import { environmentLabels } from './database-format'
import { deploymentProviderLabels, safeUrl, targetBindingSummary, targetStatusTones } from './deployment-format'
import {
  deploymentPartLabels,
  deploymentPartsTitle,
  groupPartStatusLabel,
  groupReasonText,
  groupStatusLabel,
  SHARED_SETUP_NOTE,
} from './deployment-group-format'
import { DeploymentProviderLogo } from './deployment-provider-logo'
import { useDeleteDeploymentGroup, usePutDeploymentGroup, useRetryDeploymentGroup, type DeploymentGroup } from './deployments-api'
import { GroupSetupFields, type GroupSetupField } from './group-setup-fields'
import { groupChecks, groupRequestBody, type GroupSetupValues } from './host-setup-body'

const statusTone = (status: string) =>
  status === 'ready' || status === 'available'
    ? targetStatusTones.ready
    : status === 'configuring' || status === 'provisioning'
      ? targetStatusTones.configuring
      : status === 'error'
        ? targetStatusTones.error
        : status === 'attention' || status === 'paused' || status === 'expiring'
          ? targetStatusTones.attention
          : targetStatusTones.not_configured

/** Server VALIDATION keys → form fields. */
const serverFields: Record<string, keyof GroupSetupValues> = {
  connectionid: 'connectionId',
  'create.name': 'name',
  'create.region': 'region',
  'create.plan': 'plan',
  'link.resourceid': 'resourceId',
  'link.parts.frontend': 'linkFrontend',
  'link.parts.backend': 'linkBackend',
  'link.parts.database': 'linkDatabase',
  create: 'mode',
  link: 'mode',
}

/** FR-190 (Owner): sets up a `not_configured` group: one connection, then a new project or an existing one. */
function GroupSetupForm({ projectId, group, suggestedName }: { projectId: string; group: DeploymentGroup; suggestedName: string }) {
  const put = usePutDeploymentGroup(projectId, group.provider)
  const platform = deploymentProviderLabels[group.provider]
  const [failure, setFailure] = useState<unknown>(null)
  const schema = useMemo(
    () =>
      z
        .object({
          connectionId: z.string(),
          mode: z.string(),
          name: z.string(),
          region: z.string(),
          plan: z.string(),
          resourceId: z.string(),
          linkFrontend: z.string(),
          linkBackend: z.string(),
          linkDatabase: z.string(),
        })
        .superRefine((v, ctx) => {
          for (const [field, message] of groupChecks(group, v, platform)) {
            ctx.addIssue({ code: 'custom', path: [field], message })
          }
        }),
    [group, platform],
  )
  const prefix = `group-${group.provider}`
  const ids: Record<GroupSetupField, string> = {
    connectionId: `${prefix}-connection`,
    mode: `${prefix}-mode`,
    name: `${prefix}-name`,
    region: `${prefix}-region`,
    plan: `${prefix}-plan`,
    resourceId: `${prefix}-resource`,
    linkFrontend: `${prefix}-link-frontend`,
    linkBackend: `${prefix}-link-backend`,
    linkDatabase: `${prefix}-link-database`,
  }
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitted },
  } = useForm<GroupSetupValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      connectionId: '',
      mode: 'create',
      name: group.name ?? suggestedName,
      region: '',
      plan: '',
      resourceId: '',
      linkFrontend: '',
      linkBackend: '',
      linkDatabase: '',
    },
  })
  const mode = useWatch({ control, name: 'mode' })
  const connectionId = useWatch({ control, name: 'connectionId' })
  const resourceId = useWatch({ control, name: 'resourceId' })

  const onSubmit = handleSubmit((v) => {
    setFailure(null)
    put.mutate(groupRequestBody(group, v), {
      onSuccess: () => {
        toast.success(`Setting up the group on ${platform}.`)
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

  return (
    <form
      noValidate
      aria-label={`Set up the ${platform} group`}
      onSubmit={(e) => void onSubmit(e)}
      className="flex flex-col gap-4 rounded-lg border border-border/60 bg-background/40 p-4"
    >
      <ErrorSummary title="Check the group's settings" errors={isSubmitted ? fieldErrorSummary(errors, ids) : []} />
      {failure !== null && (
        <Alert tone="error" role="alert" title="We couldn't set up the group">
          <p>{failure instanceof Error && !isApiError(failure) ? failure.message : errorMessage(failure, 'deploymentGroup')}</p>
        </Alert>
      )}
      <GroupSetupFields
        group={group}
        ids={ids}
        register={(field) => register(field)}
        errors={{
          connectionId: errors.connectionId?.message,
          mode: errors.mode?.message,
          name: errors.name?.message,
          region: errors.region?.message,
          plan: errors.plan?.message,
          resourceId: errors.resourceId?.message,
          linkFrontend: errors.linkFrontend?.message,
          linkBackend: errors.linkBackend?.message,
          linkDatabase: errors.linkDatabase?.message,
        }}
        mode={mode}
        connectionId={connectionId}
        resourceId={resourceId}
      />
      <div>
        <Button type="submit" pending={put.isPending} pendingLabel="Starting…">
          Set up the group
        </Button>
      </div>
    </form>
  )
}

/** FR-191: replaces the connection for every part, once the API has checked it can reach the shared project. */
function GroupRebindForm({ projectId, group, onDone }: { projectId: string; group: DeploymentGroup; onDone: () => void }) {
  const put = usePutDeploymentGroup(projectId, group.provider)
  const platform = deploymentProviderLabels[group.provider]
  const connections = usePlatformConnections(isPlatformProvider(group.provider) ? { provider: group.provider } : {})
  const [connectionId, setConnectionId] = useState('')
  const [missing, setMissing] = useState(false)
  if (connections.isPending) {
    return <LoadingState label={`Loading your ${platform} connections…`} />
  }
  if (connections.isError) {
    return <ErrorState error={connections.error} title={`Your ${platform} connections couldn't load`} onRetry={() => void connections.refetch()} />
  }
  return (
    <form
      noValidate
      aria-label={`Rebind the ${platform} group`}
      className="flex flex-col gap-4 rounded-lg border border-border/60 bg-background/40 p-4"
      onSubmit={(e) => {
        e.preventDefault()
        if (!connectionId) {
          setMissing(true)
          return
        }
        put.mutate(
          { connectionId },
          {
            onSuccess: () => {
              toast.success(`The ${platform} group uses the new connection.`)
              onDone()
            },
          },
        )
      }}
    >
      {put.isError && (
        <Alert tone="error" role="alert" title="We couldn't change the connection">
          <p>{errorMessage(put.error, 'deploymentGroup')}</p>
          <p className="mt-1">The group keeps its current connection.</p>
        </Alert>
      )}
      <SelectField
        id={`group-${group.provider}-rebind`}
        label={`${platform} connection`}
        hint="One of your own tokens. It replaces the connection for every part, once AIDE has checked it can reach the project."
        error={missing ? `Pick one of your ${platform} connections.` : undefined}
        value={connectionId}
        onChange={(e) => {
          setConnectionId(e.target.value)
          setMissing(false)
        }}
      >
        <option value="">Pick a connection</option>
        {connections.data.map((c) => (
          <option key={c.id} value={c.id}>
            {connectionLabel(c)}
            {c.accountName ? ` · ${c.accountName}` : ''}
          </option>
        ))}
      </SelectField>
      <div className="flex flex-wrap gap-2">
        <Button type="submit" pending={put.isPending} pendingLabel="Checking…">
          Save connection
        </Button>
        <Button variant="ghost" onClick={onDone} disabled={put.isPending}>
          Cancel
        </Button>
      </div>
    </form>
  )
}

function GroupFacts({ group }: { group: DeploymentGroup }) {
  const platform = deploymentProviderLabels[group.provider]
  const url = safeUrl(group.sharedResourceUrl)
  const projectName = group.name ?? group.sharedResourceId
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
      <dt className="text-muted-foreground">Connection</dt>
      <dd className="min-w-0">
        {group.binding ? (
          <span className="flex flex-wrap items-center gap-2">
            <span data-testid="group-binding">
              {platform} · {targetBindingSummary(group.binding)}
            </span>
            <CredentialStatusBadge status={group.binding.status} />
          </span>
        ) : (
          <span data-testid="group-binding">No connection bound yet.</span>
        )}
      </dd>
      {group.capability === 'project' && (
        <>
          <dt className="text-muted-foreground">Shared project</dt>
          <dd className="min-w-0 break-all" data-testid="group-project">
            {url ? (
              <a href={url} target="_blank" rel="noopener noreferrer" className="font-medium text-primary underline underline-offset-2">
                {projectName ?? url}
                <span className="sr-only"> (opens in a new tab)</span>
              </a>
            ) : (
              (projectName ?? 'Not created or linked yet')
            )}
          </dd>
        </>
      )}
      {group.capability === 'shared_setup' && group.name && (
        <>
          <dt className="text-muted-foreground">Name</dt>
          <dd className="min-w-0 break-all">{group.name}</dd>
        </>
      )}
      {group.region && (
        <>
          <dt className="text-muted-foreground">Region</dt>
          <dd>{group.region}</dd>
        </>
      )}
      {group.plan && (
        <>
          <dt className="text-muted-foreground">Plan</dt>
          <dd>{group.plan}</dd>
        </>
      )}
    </dl>
  )
}

/** FR-190: each part's own status, in words; a failed part doesn't hide the others. */
function PartStatuses({ group }: { group: DeploymentGroup }) {
  return (
    <ul aria-label={`Parts of the ${deploymentProviderLabels[group.provider]} group`} className="divide-y divide-border/60 rounded-lg border border-border/60">
      {group.partStatuses.map((p) => {
        const label = p.environment ? `${deploymentPartLabels[p.part]} (${environmentLabels[p.environment].toLowerCase()})` : deploymentPartLabels[p.part]
        const reason = groupReasonText(p.statusReason, group.provider)
        return (
          <li key={`${p.part}-${p.environment ?? ''}`} aria-label={label} className="flex flex-wrap items-center gap-2 p-3 text-sm">
            <span className="font-medium">{label}</span>
            <StatusBadge tone={statusTone(p.status)}>
              <span data-testid="group-part-status">{groupPartStatusLabel(p.status)}</span>
            </StatusBadge>
            {reason && <span className="w-full text-muted-foreground">{reason}</span>}
          </li>
        )
      })}
    </ul>
  )
}

/**
 * Settings → Deployment group card (FR-191): "connected by …", the shared project link, shared rebind, each part's status
 * and Retry when a part failed or is missing. `children` are the parts' own target and database cards: readiness,
 * inventory, rollouts and rollback stay per part. A `not_configured` group offers create or link (FR-190).
 */
export function DeploymentGroupCard({
  projectId,
  group,
  canManage,
  suggestedName,
  children,
}: {
  projectId: string
  group: DeploymentGroup
  canManage: boolean
  suggestedName: string
  children?: ReactNode
}) {
  const platform = deploymentProviderLabels[group.provider]
  const title = deploymentPartsTitle(group.parts, platform)
  const configured = group.id !== null
  const manage = canManage && group.canBind
  const headingId = `group-${group.provider}-title`
  const retry = useRetryDeploymentGroup(projectId, group.provider)
  const remove = useDeleteDeploymentGroup(projectId, group.provider)
  const [rebinding, setRebinding] = useState(false)
  const [confirmRemove, setConfirmRemove] = useState(false)
  const reason = groupReasonText(group.statusReason, group.provider)

  return (
    <section aria-labelledby={headingId} data-testid={`group-${group.provider}`} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-4')}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id={headingId} className="flex items-center gap-2 text-lg">
          <DeploymentProviderLogo provider={group.provider} className="size-5" />
          {title}
        </h3>
        <StatusBadge tone={statusTone(group.status)} pulse={group.status === 'configuring'}>
          <span data-testid="group-status">{groupStatusLabel(group.status)}</span>
        </StatusBadge>
      </div>
      {group.capability === 'shared_setup' && <p className="text-sm text-muted-foreground">{SHARED_SETUP_NOTE}</p>}
      {configured && <GroupFacts group={group} />}
      {reason && (
        <p className="text-sm" data-testid="group-status-reason">
          {reason}
        </p>
      )}
      <PartStatuses group={group} />
      {retry.isError && (
        <Alert tone="error" role="alert" title="We couldn't retry the setup">
          <p>{errorMessage(retry.error, 'deploymentGroup')}</p>
        </Alert>
      )}
      {!configured && manage && <GroupSetupForm projectId={projectId} group={group} suggestedName={suggestedName} />}
      {!configured && !canManage && <p className="text-sm text-muted-foreground">A project Owner can set up this group.</p>}
      {rebinding && (
        <GroupRebindForm
          projectId={projectId}
          group={group}
          onDone={() => {
            setRebinding(false)
          }}
        />
      )}
      {configured && manage && !rebinding && (
        <div className="flex flex-wrap gap-2">
          {group.canRetry && (
            <Button
              pending={retry.isPending}
              pendingLabel="Retrying…"
              aria-label={`Retry the ${platform} group`}
              onClick={() => {
                retry.mutate(undefined, {
                  onSuccess: () => {
                    toast.success('Retrying the parts that failed or are missing.')
                  },
                })
              }}
            >
              Retry
            </Button>
          )}
          <Button
            variant="secondary"
            aria-label={`Change the connection of the ${platform} group`}
            onClick={() => {
              setRebinding(true)
            }}
          >
            Change connection
          </Button>
          <Button
            variant="ghost"
            aria-label={`Remove the ${platform} group`}
            onClick={() => {
              setConfirmRemove(true)
            }}
          >
            Remove group
          </Button>
        </div>
      )}
      {remove.isError && (
        <Alert tone="error" role="alert" title="We couldn't remove the group">
          <p>{errorMessage(remove.error, 'deploymentGroup')}</p>
        </Alert>
      )}
      {children}
      <ConfirmDialog
        open={confirmRemove}
        title={`Remove the ${platform} group?`}
        confirmLabel="Remove group"
        pendingLabel="Removing…"
        pending={remove.isPending}
        destructive
        onConfirm={() => {
          remove.mutate(undefined, {
            onSettled: () => {
              setConfirmRemove(false)
            },
            onSuccess: () => {
              toast.success(`The ${platform} group is removed.`)
            },
          })
        }}
        onCancel={() => {
          setConfirmRemove(false)
        }}
      >
        <p>AIDE clears this group and its targets and databases. Nothing is deleted on {platform}: its project, services and databases stay.</p>
      </ConfirmDialog>
    </section>
  )
}
