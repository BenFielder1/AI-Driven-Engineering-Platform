import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'

import type { Schemas } from '../../api/client'
import { EmptyState } from '../../components/shared/empty-state'
import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge, type StatusTone } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, gitHostFailureMessage } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { isAdmin } from '../../lib/permissions'
import { cn } from '../../lib/utils'
import { useCurrentUser } from '../auth/auth-api'
import {
  gitlabHost,
  gitlabStartHref,
  useDeleteGitLabConnection,
  useGitLabConnections,
  useGitLabInstances,
  useTestGitLabConnection,
  type GitLabConnection,
  type GitLabInstanceSummary,
} from '../gitlab/gitlab-api'
import { GitLabLogo } from '../gitlab/gitlab-logo'
import { formatDateTime } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { isPast } from './connection-format'
import { GitLabTokenDialog } from './gitlab-token-dialog'

/** OAuth connect comes back here; `gitlab=connected` announces it. */
const CONNECT_RETURN_URL = '/connections?gitlab=connected'

type Status = Schemas['GitConnectionStatus']
/** FR-131, FR-136: a Git connection's status in words and as a badge tone (Connections, Settings → Git binding). */
export const gitConnectionStatusLabels: Record<Status, string> = { valid: 'Valid', invalid: 'Invalid', insufficient_scope: 'Missing permissions' }
export const gitConnectionStatusTones: Record<Status, StatusTone> = { valid: 'success', invalid: 'destructive', insufficient_scope: 'warning' }
const statusLabels = gitConnectionStatusLabels
const statusTones = gitConnectionStatusTones

function ExpiryText({ connection }: { connection: GitLabConnection }) {
  const c = connection
  if (c.authKind === 'oauth') {
    return <>Renews automatically</>
  }
  if (!c.expiresAt) {
    return <>No expiry</>
  }
  if (isPast(c.expiresAt)) {
    return <span className="text-destructive">Expired {formatDateTime(c.expiresAt)}</span>
  }
  return (
    <span className={cn(c.expiryWarning && 'font-medium')}>
      Expires {formatDateTime(c.expiresAt)}
    </span>
  )
}

function ConnectionRow({
  connection,
  instance,
  testing,
  onTest,
  onDelete,
}: {
  connection: GitLabConnection
  /** The enabled instance, or undefined when an Admin disabled (or removed) it. */
  instance: GitLabInstanceSummary | undefined
  testing: boolean
  onTest: () => void
  onDelete: () => void
}) {
  const c = connection
  const label = `${c.login} on ${c.instanceName}`
  const canReconnect = c.authKind === 'oauth' && instance?.oauthConfigured === true
  return (
    <li aria-label={label} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="flex min-w-0 flex-col gap-1 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <GitLabLogo />
          <span className="font-semibold break-words" data-testid="gitlab-login">
            {c.login}
          </span>
          <StatusBadge tone={statusTones[c.status]}>
            <span data-testid="gitlab-connection-status">{statusLabels[c.status]}</span>
          </StatusBadge>
          {!c.instanceEnabled && (
            <StatusBadge tone="neutral">
              <span data-testid="gitlab-instance-disabled">GitLab instance disabled</span>
            </StatusBadge>
          )}
        </span>
        <span>
          {c.instanceName} ({gitlabHost(c.instanceBaseUrl)}) · {c.authKind === 'oauth' ? 'OAuth' : 'Access token'}
        </span>
        <span data-testid="gitlab-scopes">
          Scopes: {c.scopes.length > 0 ? c.scopes.join(', ') : 'none reported'} · <ExpiryText connection={c} />
        </span>
        <span className="text-muted-foreground">
          {c.lastValidatedAt ? (
            <>
              Last checked <RelativeTime iso={c.lastValidatedAt} />
            </>
          ) : (
            'Not checked yet'
          )}
          {' · '}Added <RelativeTime iso={c.createdAt} />
        </span>
        {c.expiryWarning && c.expiresAt && !isPast(c.expiresAt) && (
          <span className="text-foreground" data-testid="gitlab-expiry-warning">
            This token expires within 14 days. Create a new token, connect it, then delete this one.
          </span>
        )}
        {c.status === 'invalid' && (
          <span className="text-destructive">
            GitLab rejected this connection. {canReconnect ? 'Reconnect' : 'Connect a new token'}, then delete this one.
          </span>
        )}
        {c.status === 'insufficient_scope' && (
          <span className="text-destructive">This connection lacks the api or write_repository scope. Connect again with both.</span>
        )}
        {!c.instanceEnabled && (
          <span className="text-muted-foreground">An administrator disabled this instance, so the connection doesn&apos;t work.</span>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        {c.status === 'invalid' && canReconnect && (
          <a href={gitlabStartHref('connect', c.instanceId, CONNECT_RETURN_URL)} className={buttonClasses('primary')}>
            Reconnect
          </a>
        )}
        <Button variant="secondary" pending={testing} pendingLabel="Testing…" onClick={onTest} aria-label={`Test ${label}`}>
          Test
        </Button>
        <Button variant="danger" onClick={onDelete} aria-label={`Delete ${label}`}>
          Delete
        </Button>
      </div>
    </li>
  )
}

/**
 * /connections → Source control → GitLab (FR-131): per enabled instance, connect with OAuth (when the instance has an
 * OAuth application) or an access token, and the caller's connections with test and delete. `callbackError` is a
 * failed OAuth connect (`?error=GIT_AUTH_FAILED&reason=…&provider=gitlab`); `connected` is a successful one.
 */
export function GitLabConnectionSection({
  callbackError,
  connected = false,
}: {
  callbackError?: { reason?: string }
  connected?: boolean
}) {
  const user = useCurrentUser()
  const instances = useGitLabInstances()
  const connections = useGitLabConnections()
  const test = useTestGitLabConnection()
  const remove = useDeleteGitLabConnection()
  const [tokenFor, setTokenFor] = useState<string | null>(null)
  const [toDelete, setToDelete] = useState<GitLabConnection | null>(null)
  const [actionError, setActionError] = useState<{ title: string; error: unknown } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const announced = useRef(false)
  useEffect(() => {
    if (connected && !announced.current) {
      announced.current = true
      toast.success('GitLab connected.')
    }
  }, [connected])

  const runTest = (c: GitLabConnection) => {
    setActionError(null)
    test.mutate(c.id, {
      onSuccess: (result) => {
        if (result.status === 'valid') {
          toast.success(`${result.login} on ${result.instanceName} works.`)
        } else {
          toast.error(`${result.login} on ${result.instanceName}: ${statusLabels[result.status].toLowerCase()}.`)
        }
      },
      onError: (error) => {
        setActionError({ title: `We couldn't test ${c.login} on ${c.instanceName}`, error })
      },
    })
  }

  const confirmDelete = () => {
    if (!toDelete) {
      return
    }
    const target = toDelete
    setActionError(null)
    remove.mutate(target.id, {
      onSuccess: () => {
        setToDelete(null)
        toast.success(`${target.login} on ${target.instanceName} deleted.`)
        headingRef.current?.focus()
      },
      onError: (error) => {
        setToDelete(null)
        setActionError({ title: `We couldn't delete ${target.login} on ${target.instanceName}`, error })
      },
    })
  }

  const renderRows = (list: readonly GitLabConnection[], instance: GitLabInstanceSummary | undefined, label: string) => (
    <ul aria-label={label} className="divide-y divide-border/60 rounded-lg border border-border/60">
      {list.map((c) => (
        <ConnectionRow
          key={c.id}
          connection={c}
          instance={instance}
          testing={test.isPending && test.variables === c.id}
          onTest={() => {
            runTest(c)
          }}
          onDelete={() => {
            setToDelete(c)
          }}
        />
      ))}
    </ul>
  )

  let body
  if (instances.isPending || connections.isPending) {
    body = <LoadingState label="Loading your GitLab connections…" />
  } else if (instances.isError || connections.isError) {
    const error = instances.isError ? instances.error : connections.error
    body = (
      <ErrorState
        error={error}
        title="Your GitLab connections couldn't load"
        onRetry={() => {
          void instances.refetch()
          void connections.refetch()
        }}
      />
    )
  } else if (instances.data.length === 0 && connections.data.length === 0) {
    body = (
      <EmptyState
        variant="not_connected"
        headingLevel={3}
        bare
        title="GitLab isn't available"
        description="An administrator has disabled every GitLab instance."
        action={
          isAdmin(user) ? (
            <Link to="/admin/integrations" className={buttonClasses('secondary')}>
              Manage GitLab instances
            </Link>
          ) : undefined
        }
      />
    )
  } else {
    const enabledIds = new Set(instances.data.map((i) => i.id))
    const orphaned = connections.data.filter((c) => !enabledIds.has(c.instanceId))
    body = (
      <div className="flex flex-col gap-5">
        {instances.data.map((instance) => {
          const mine = connections.data.filter((c) => c.instanceId === instance.id)
          const titleId = `gitlab-instance-${instance.id}`
          return (
            <section key={instance.id} aria-labelledby={titleId} className="flex flex-col gap-3">
              <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                <h3 id={titleId} className="flex flex-wrap items-center gap-2 font-semibold">
                  <GitLabLogo />
                  {instance.name}
                  <span className="font-normal text-muted-foreground">{gitlabHost(instance.baseUrl)}</span>
                </h3>
                <div className="flex flex-wrap gap-2">
                  {instance.oauthConfigured && (
                    <a
                      href={gitlabStartHref('connect', instance.id, CONNECT_RETURN_URL)}
                      className={buttonClasses('primary')}
                      aria-label={`Connect with GitLab (${instance.name})`}
                    >
                      Connect with GitLab
                    </a>
                  )}
                  <Button
                    variant="secondary"
                    aria-label={`Use an access token (${instance.name})`}
                    onClick={() => {
                      setTokenFor(instance.id)
                    }}
                  >
                    Use an access token
                  </Button>
                </div>
              </div>
              {!instance.oauthConfigured && (
                <p className="text-sm text-muted-foreground">
                  This instance has no OAuth application yet, so connect with an access token.
                </p>
              )}
              {mine.length === 0 ? (
                <p className="text-sm text-muted-foreground">Not connected.</p>
              ) : (
                renderRows(mine, instance, `Your connections on ${instance.name}`)
              )}
            </section>
          )
        })}
        {orphaned.length > 0 && (
          <section aria-labelledby="gitlab-disabled-title" className="flex flex-col gap-3">
            <h3 id="gitlab-disabled-title" className="font-semibold">
              Disabled instances
            </h3>
            {renderRows(orphaned, undefined, 'Connections on disabled instances')}
          </section>
        )}
      </div>
    )
  }

  return (
    <section aria-labelledby="gitlab-connection-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div>
        <h2 id="gitlab-connection-title" ref={headingRef} tabIndex={-1} className="flex items-center gap-2 text-lg outline-none">
          <GitLabLogo className="size-5" />
          GitLab
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Your GitLab accounts, per instance, used to pick and create projects. Background Git work on a GitLab project uses
          the connection of the Owner who bound it.
        </p>
      </div>
      {callbackError && (
        <Alert tone="error" role="alert" title="GitLab didn't connect">
          <p>{gitHostFailureMessage('gitlab', callbackError.reason)}</p>
        </Alert>
      )}
      {actionError && (
        <Alert tone="error" role="alert" title={actionError.title}>
          <p>{errorMessage(actionError.error, 'gitlabConnection')}</p>
        </Alert>
      )}
      {body}
      {tokenFor !== null && instances.data && (
        <GitLabTokenDialog
          instances={instances.data}
          initialInstanceId={tokenFor}
          onClose={() => {
            setTokenFor(null)
          }}
          onAdded={(c) => {
            setTokenFor(null)
            toast.success(`${c.login} on ${c.instanceName} connected and checked.`)
          }}
        />
      )}
      <ConfirmDialog
        open={toDelete !== null}
        title={`Delete ${toDelete?.login ?? ''} on ${toDelete?.instanceName ?? ''}?`}
        confirmLabel="Delete connection"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmDelete}
        onCancel={() => {
          setToDelete(null)
        }}
      >
        <p>
          AIDE forgets this connection&apos;s tokens. Projects bound to it lose the binding until an Owner re-binds one, and
          their Git work fails until then. Signing in with GitLab still works and reconnects it.
        </p>
      </ConfirmDialog>
    </section>
  )
}
