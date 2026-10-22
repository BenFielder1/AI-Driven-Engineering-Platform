import { Link } from '@tanstack/react-router'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'

import { EmptyState } from '../../components/shared/empty-state'
import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, githubFailureMessage } from '../../lib/errors'
import { isAdmin } from '../../lib/permissions'
import { cn } from '../../lib/utils'
import { useAuthProviders, useCurrentUser } from '../auth/auth-api'
import { connectGitHubHref, githubKeys, useDisconnectGitHub, useGitHubConnection, type GitHubConnection } from '../github/github-api'
import { GitHubLink } from '../github/github-error-alert'
import { RelativeTime } from '../projects/relative-time'

const RETURN_URL = '/connections'

function ConnectedView({ connection, onDisconnect }: { connection: GitHubConnection; onDisconnect: () => void }) {
  const valid = connection.status === 'valid'
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex flex-col gap-1 text-sm">
          <span className="flex flex-wrap items-center gap-2">
            <span className="font-semibold" data-testid="github-login">
              {connection.login}
            </span>
            <StatusBadge tone={valid ? 'success' : 'destructive'}>
              <span data-testid="github-connection-status">{valid ? 'Connected' : 'Needs reconnecting'}</span>
            </StatusBadge>
          </span>
          {valid && connection.accessExpiresAt && (
            <span className="text-muted-foreground">
              Access renews automatically (current token expires <RelativeTime iso={connection.accessExpiresAt} />)
            </span>
          )}
          {!valid && (
            <span className="text-destructive">GitHub no longer accepts this connection. Reconnect to keep using the repo picker.</span>
          )}
        </div>
        <div className="flex flex-wrap gap-2">
          {!valid && (
            <a href={connectGitHubHref(RETURN_URL)} className={buttonClasses('primary')}>
              Reconnect GitHub
            </a>
          )}
          <Button variant="danger" onClick={onDisconnect}>
            Disconnect
          </Button>
        </div>
      </div>

      <div>
        <h3 className="mb-2 text-sm font-semibold">Where AIDE is installed</h3>
        {connection.installations.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            The AIDE GitHub App isn&apos;t installed on any account you can access yet. Install it to pick repositories.
          </p>
        ) : (
          <ul aria-label="GitHub App installations" className="divide-y divide-border/60 rounded-lg border border-border/60">
            {connection.installations.map((i) => (
              <li
                key={i.id}
                aria-label={i.account.login}
                className="flex flex-col gap-2 p-3 text-sm sm:flex-row sm:items-center sm:justify-between"
              >
                <span>
                  <span className="font-medium">{i.account.login}</span>
                  <span className="text-muted-foreground">
                    {' '}
                    ({i.account.type === 'organization' ? 'organisation' : 'personal account'}) ·{' '}
                    {i.repositorySelection === 'all' ? 'All repositories' : 'Selected repositories'}
                  </span>
                </span>
                <GitHubLink href={i.manageUrl} variant="link">
                  {`Manage ${i.account.login}`}
                </GitHubLink>
              </li>
            ))}
          </ul>
        )}
      </div>
      {connection.installUrl && (
        <div>
          <GitHubLink href={connection.installUrl}>Install AIDE on more repositories</GitHubLink>
        </div>
      )}
    </div>
  )
}

/**
 * /connections → GitHub (FR-81): connect (the App's user authorization), the installations the user can access, and
 * disconnect. Without a registered App, it explains that an Admin must register one.
 */
export function GitHubConnectionSection({
  callbackError,
  installed = false,
}: {
  callbackError?: { reason?: string }
  /** Back from the App's setup URL: announce it and refetch the installations (once). */
  installed?: boolean
}) {
  const queryClient = useQueryClient()
  const announced = useRef(false)
  useEffect(() => {
    if (installed && !announced.current) {
      announced.current = true
      toast.success('AIDE installed on GitHub')
      void queryClient.invalidateQueries({ queryKey: githubKeys.connection() })
      void queryClient.invalidateQueries({ queryKey: githubKeys.all })
    }
  }, [installed, queryClient])
  const user = useCurrentUser()
  const providers = useAuthProviders()
  const appRegistered = providers.data?.githubMode === 'app'
  const connection = useGitHubConnection(appRegistered)
  const disconnect = useDisconnectGitHub()
  const [confirming, setConfirming] = useState(false)
  const [actionError, setActionError] = useState<unknown>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const confirmDisconnect = () => {
    setActionError(null)
    disconnect.mutate(undefined, {
      onSuccess: () => {
        setConfirming(false)
        toast.success('GitHub disconnected.')
        headingRef.current?.focus()
      },
      onError: (error) => {
        setConfirming(false)
        setActionError(error)
      },
    })
  }

  let body
  if (providers.isPending || (appRegistered && connection.isPending)) {
    body = <LoadingState label="Loading your GitHub connection…" />
  } else if (providers.isError) {
    body = <ErrorState error={providers.error} title="GitHub settings couldn't load" onRetry={() => void providers.refetch()} />
  } else if (!appRegistered) {
    body = (
      <EmptyState
        variant="not_connected"
        headingLevel={3}
        bare
        title="GitHub isn't set up yet"
        description="AIDE connects to GitHub through its GitHub App, and an Admin must register it first under Admin → Integrations → GitHub. Projects keep using the Git settings they have."
        action={
          isAdmin(user) ? (
            <Link to="/admin/integrations" className={buttonClasses('secondary')}>
              Register the GitHub App
            </Link>
          ) : undefined
        }
      />
    )
  } else if (connection.isError) {
    body = (
      <ErrorState
        error={connection.error}
        title="Your GitHub connection couldn't load"
        onRetry={() => void connection.refetch()}
        context="githubConnection"
      />
    )
  } else if (!connection.data?.connected) {
    body = (
      <EmptyState
        variant="not_connected"
        headingLevel={3}
        bare
        title="Not connected"
        description="Connect GitHub to pick or create repositories for your projects. Signing in with GitHub connects it automatically."
        action={
          <a href={connectGitHubHref(RETURN_URL)} className={buttonClasses('primary')}>
            Connect GitHub
          </a>
        }
      />
    )
  } else {
    body = (
      <ConnectedView
        connection={connection.data}
        onDisconnect={() => {
          setConfirming(true)
        }}
      />
    )
  }

  return (
    <section aria-labelledby="github-connection-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div>
        <h2 id="github-connection-title" ref={headingRef} tabIndex={-1} className="text-lg outline-none">
          GitHub
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Your GitHub account, used to pick and create repositories. Background Git work uses the App, never your token.
        </p>
      </div>
      {callbackError && (
        <Alert tone="error" role="alert" title="GitHub didn't connect">
          <p>{githubFailureMessage(callbackError.reason)}</p>
        </Alert>
      )}
      {actionError !== null && (
        <Alert tone="error" role="alert" title="We couldn't disconnect GitHub">
          <p>{errorMessage(actionError, 'githubConnection')}</p>
        </Alert>
      )}
      {body}
      <ConfirmDialog
        open={confirming}
        title="Disconnect GitHub?"
        confirmLabel="Disconnect"
        pendingLabel="Disconnecting…"
        pending={disconnect.isPending}
        destructive
        onConfirm={confirmDisconnect}
        onCancel={() => {
          setConfirming(false)
        }}
      >
        <p>
          AIDE forgets your GitHub tokens. Projects keep working (they use the App), but you can&apos;t pick or create
          repositories until you connect again. Signing in with GitHub reconnects it.
        </p>
      </ConfirmDialog>
    </section>
  )
}
