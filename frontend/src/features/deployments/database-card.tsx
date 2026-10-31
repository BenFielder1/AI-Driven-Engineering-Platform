import { useState } from 'react'
import { toast } from 'sonner'

import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { errorMessage, isApiError, withJobDetail, withPlatform } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { CredentialStatusBadge } from '../connections/credential-status-badge'
import { PlatformLogo } from '../connections/platform-logos'
import { isPlatformProvider } from '../connections/platform-connections-api'
import type { Project } from '../projects/projects-api'
import { formatDateTime } from '../projects/labels'
import {
  databasePlatformNames,
  databaseProviderLabel,
  databaseStatusLabel,
  databaseStatusTones,
  databaseWarningCopy,
  environmentLabels,
  isProvisioningActive,
  provisioningStepLabels,
} from './database-format'
import { DatabaseSetupForm } from './database-setup-form'
import { safeUrl, targetBindingSummary } from './deployment-format'
import { useDeleteManagedDatabase, useRestoreManagedDatabase, type DatabaseCard } from './deployments-api'

/** SPEC §7 inline SVG: the platform's mark, or a plain database cylinder for External and Fake. Decorative. */
export function DatabaseProviderLogo({ provider, className }: { provider: string; className?: string }) {
  if (isPlatformProvider(provider)) {
    return <PlatformLogo provider={provider} className={className} />
  }
  return (
    <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" className={cn('size-4 shrink-0', className)}>
      <g fill="none" stroke="currentColor" strokeWidth="1.75" strokeDasharray={provider === 'fake' ? '3 2' : undefined}>
        <ellipse cx="12" cy="5.5" rx="7.5" ry="2.75" />
        <path d="M4.5 5.5v13c0 1.5 3.4 2.75 7.5 2.75s7.5-1.25 7.5-2.75v-13M4.5 12c0 1.5 3.4 2.75 7.5 2.75s7.5-1.25 7.5-2.75" />
      </g>
    </svg>
  )
}

const stepLabel = (s: string) => provisioningStepLabels[s] ?? s

/** FR-152: the `database_provisioning` job's steps while it runs, as text (announced politely). */
function ProvisioningProgress({ database: db }: { database: DatabaseCard }) {
  if (!db.provisioning || !isProvisioningActive(db)) {
    return null
  }
  const current = db.provisioning.step ?? 'queued'
  const steps = ['queued', current === 'linking' ? 'linking' : 'creating', 'waiting', 'syncing_env']
  const index = Math.max(0, steps.indexOf(current))
  return (
    <div role="status" aria-live="polite" data-testid="provisioning-progress" className="rounded-lg border border-primary/40 bg-primary/10 p-3 text-sm">
      <p className="font-medium">Setting up: {stepLabel(current)}…</p>
      <ol aria-label="Provisioning steps" className="mt-2 flex flex-col gap-1">
        {steps.map((s, i) => (
          <li key={s}>
            {stepLabel(s)} <span className="text-muted-foreground">({i < index ? 'done' : i === index ? 'in progress' : 'next'})</span>
          </li>
        ))}
      </ol>
    </div>
  )
}

/** FR-152: a failed provisioning job: the shared copy for its code with the platform's message. */
function ProvisioningFailure({ database: db }: { database: DatabaseCard }) {
  const p = db.provisioning
  if (p?.state !== 'failed') {
    return null
  }
  const copy = withJobDetail(p.errorCode, db.provider, p.errorMessage)
  const extra = !copy ? p.errorMessage : null
  return (
    <Alert tone="error" role="alert" title="The database setup failed">
      <p data-testid="provisioning-error">{copy ?? extra ?? 'The database setup failed.'}</p>
      {p.errorCode === 'PLATFORM_PLAN_LIMIT' && db.provider === 'supabase' && (
        <p className="mt-1">Link an existing Supabase project instead, or upgrade the organisation&apos;s plan.</p>
      )}
    </Alert>
  )
}

/** FR-152, §4.21: the Supabase scoped-token permissions the bound connection lacks, listed (e.g. "API Key Secrets: Read"). */
function MissingPermissions({ database: db }: { database: DatabaseCard }) {
  if (db.missingPermissions.length === 0) {
    return null
  }
  const platform = databasePlatformNames[db.provider] ?? databaseProviderLabel(db.provider)
  return (
    <Alert tone="warning" title={`The ${platform} token is missing permissions`}>
      <ul aria-label="Missing permissions" data-testid="missing-permissions" className="list-disc pl-5">
        {db.missingPermissions.map((m) => (
          <li key={m}>{m}</li>
        ))}
      </ul>
      <p className="mt-1">Give the token these permissions in {platform}, then test it again under Connections.</p>
    </Alert>
  )
}

function DatabaseFacts({ database: db }: { database: DatabaseCard }) {
  const url = safeUrl(db.resourceUrl)
  const providerName = databaseProviderLabel(db.provider)
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[max-content_1fr]">
      <dt className="text-muted-foreground">Connection</dt>
      <dd className="min-w-0">
        {db.binding ? (
          <span className="flex flex-wrap items-center gap-2">
            <span data-testid="database-binding">
              {databasePlatformNames[db.provider] ?? providerName} · {targetBindingSummary(db.binding)}
            </span>
            <CredentialStatusBadge status={db.binding.status} />
          </span>
        ) : (
          <span data-testid="database-binding">
            {db.provider === 'external'
              ? 'No platform connection: AIDE uses the connection string you entered.'
              : db.needsConnection
                ? 'No connection bound yet.'
                : 'No connection needed.'}
          </span>
        )}
      </dd>
      <dt className="text-muted-foreground">Database</dt>
      <dd className="min-w-0 break-all" data-testid="database-resource">
        {db.name || db.resourceId ? (
          <>
            <span className="font-medium">{db.name ?? db.resourceId}</span>
            {db.resourceId && db.resourceId !== db.name && (
              <>
                {' '}
                <code className="font-mono text-xs text-muted-foreground">{db.resourceId}</code>
              </>
            )}
            {url && (
              <>
                {' '}
                <a href={url} target="_blank" rel="noreferrer noopener" className="font-medium text-primary underline underline-offset-2">
                  Open in {databasePlatformNames[db.provider] ?? providerName}
                  <span className="sr-only"> (opens in a new tab)</span>
                </a>
              </>
            )}
          </>
        ) : db.provider === 'external' && db.status !== 'not_configured' ? (
          'External database'
        ) : (
          'Not created or linked yet'
        )}
      </dd>
      {db.region && (
        <>
          <dt className="text-muted-foreground">Region</dt>
          <dd data-testid="database-region">{db.region}</dd>
        </>
      )}
      {db.plan && (
        <>
          <dt className="text-muted-foreground">Plan</dt>
          <dd data-testid="database-plan">{db.plan}</dd>
        </>
      )}
      {db.expiresAt && (
        <>
          <dt className="text-muted-foreground">Expires</dt>
          <dd data-testid="database-expires">{formatDateTime(db.expiresAt)}</dd>
        </>
      )}
      {db.connectionSummary && (
        <>
          <dt className="text-muted-foreground">Connects to</dt>
          <dd className="min-w-0 break-all">
            <code data-testid="database-connection-summary" className="font-mono text-xs">
              {db.connectionSummary}
            </code>
          </dd>
        </>
      )}
    </dl>
  )
}

/** §4.21 Paused (Supabase `INACTIVE`): said in words, with Restore for Owners when the platform can restore it. */
function PausedNotice({ project, database: db, canManage }: { project: Project; database: DatabaseCard; canManage: boolean }) {
  const restore = useRestoreManagedDatabase(project.id, db.environment)
  if (!db.paused && db.status !== 'paused') {
    return null
  }
  const platform = databasePlatformNames[db.provider] ?? databaseProviderLabel(db.provider)
  const restoreError = restore.isError
    ? isApiError(restore.error)
      ? withPlatform(errorMessage(restore.error, 'databaseRestore'), restore.error.platform ?? db.provider)
      : errorMessage(restore.error, 'databaseRestore')
    : null
  return (
    <Alert tone="warning" title="Paused">
      <p>
        {platform} paused this database, so the app can&apos;t reach it.
        {db.canRestore ? (canManage ? ' Restore it to bring it back.' : ' A project Owner can restore it.') : ` Restore it in ${platform}.`}
      </p>
      {canManage && db.canRestore && (
        <p className="mt-2">
          <Button
            variant="secondary"
            pending={restore.isPending}
            pendingLabel="Restoring…"
            aria-label={`Restore the ${db.environment} database`}
            onClick={() => {
              restore.mutate(undefined, {
                onSuccess: () => {
                  toast.success(`Restoring the ${db.environment} database.`)
                },
              })
            }}
          >
            Restore
          </Button>
        </p>
      )}
      {restoreError && (
        <p role="alert" className="mt-2 font-medium">
          {restoreError}
        </p>
      )}
    </Alert>
  )
}

/**
 * Settings → Deployment database card (FR-152, §4.21): the provider, whose connection it uses, status, region, plan,
 * expiry, the dashboard link and connection summary, provisioning progress or failure, missing Supabase permissions,
 * warnings, and Paused with Restore. Owners provision, link or enter an External database and unlink it; other
 * members read it.
 */
export function DatabaseCardView({ project, database: db, canManage }: { project: Project; database: DatabaseCard; canManage: boolean }) {
  const env = environmentLabels[db.environment]
  const providerName = databaseProviderLabel(db.provider)
  const active = isProvisioningActive(db)
  const configured = db.status !== 'not_configured' || db.binding !== null || db.resourceId !== null
  const [editing, setEditing] = useState(false)
  const [confirmUnlink, setConfirmUnlink] = useState(false)
  const unlink = useDeleteManagedDatabase(project.id, db.environment)
  const headingId = `database-${db.environment}-title`
  const showForm = canManage && !active && (editing || !configured)
  const status = active ? 'provisioning' : db.status

  return (
    <section aria-labelledby={headingId} data-testid={`database-${db.environment}`} className={cn(panelClass, 'flex min-w-0 flex-col gap-4 p-4')}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h4 id={headingId} className="flex items-center gap-2 text-base font-semibold">
          <DatabaseProviderLogo provider={db.provider} className="size-5" />
          {env} database
        </h4>
        <StatusBadge tone={databaseStatusTones[status] ?? 'neutral'} pulse={active}>
          <span data-testid="database-status">{databaseStatusLabel(status)}</span>
        </StatusBadge>
      </div>
      <p className="text-sm" data-testid="database-provider">
        {providerName}
      </p>
      {!db.inComposition && (
        <Alert tone="warning" title="No longer in the stack">
          <p>The stack no longer uses {providerName}. Unlink this database, then set up the new one.</p>
        </Alert>
      )}
      <DatabaseFacts database={db} />
      <ProvisioningProgress database={db} />
      <ProvisioningFailure database={db} />
      <PausedNotice project={project} database={db} canManage={canManage} />
      {db.status === 'removed' && (
        <p className="text-sm text-muted-foreground">The database no longer exists on {databasePlatformNames[db.provider] ?? providerName}. Set up a new one.</p>
      )}
      <MissingPermissions database={db} />
      {db.warnings.length > 0 && (
        <ul aria-label={`${env} database warnings`} className="flex flex-col gap-2">
          {db.warnings.map((w) => (
            <li key={w} data-testid="database-warning" className="rounded-lg border border-warning/40 bg-warning/10 p-3 text-sm">
              <span className="font-medium">Warning: </span>
              {databaseWarningCopy(w, db)}
            </li>
          ))}
        </ul>
      )}
      {showForm && (
        <DatabaseSetupForm
          project={project}
          database={db}
          onDone={() => {
            setEditing(false)
          }}
        />
      )}
      {canManage && !active && !showForm && (
        <div className="flex flex-wrap gap-2">
          <Button
            variant="secondary"
            aria-label={`Change the ${db.environment} database`}
            onClick={() => {
              setEditing(true)
            }}
          >
            Change setup
          </Button>
          <Button
            variant="ghost"
            aria-label={`Unlink the ${db.environment} database`}
            onClick={() => {
              setConfirmUnlink(true)
            }}
          >
            Unlink
          </Button>
        </div>
      )}
      {unlink.isError && (
        <Alert tone="error" role="alert" title="We couldn't unlink the database">
          <p>{errorMessage(unlink.error, 'databaseUnlink')}</p>
        </Alert>
      )}
      <ConfirmDialog
        open={confirmUnlink}
        title={`Unlink the ${db.environment} database?`}
        confirmLabel="Unlink"
        pendingLabel="Unlinking…"
        pending={unlink.isPending}
        destructive
        onConfirm={() => {
          unlink.mutate(undefined, {
            onSettled: () => {
              setConfirmUnlink(false)
            },
            onSuccess: () => {
              toast.success(`The ${db.environment} database is unlinked.`)
            },
          })
        }}
        onCancel={() => {
          setConfirmUnlink(false)
        }}
      >
        <p>
          {`AIDE stops using this database and removes its derived variables. Nothing is deleted on ${databasePlatformNames[db.provider] ?? providerName}, and its data is kept.`}
        </p>
      </ConfirmDialog>
    </section>
  )
}
