import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'

import { panelClass } from '../../components/shared/panel-class'
import { StatusBadge } from '../../components/shared/status-badge'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { buttonClasses } from '../../components/ui/button-classes'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage, gitHostFailureMessage } from '../../lib/errors'
import { cn } from '../../lib/utils'
import { useAuthProviders } from '../auth/auth-api'
import {
  bitbucketStartHref,
  useBitbucketConnections,
  useDeleteBitbucketConnection,
  useTestBitbucketConnection,
  type BitbucketConnection,
} from '../bitbucket/bitbucket-api'
import { BitbucketLogo } from '../bitbucket/bitbucket-logo'
import { RelativeTime } from '../projects/relative-time'
import { BitbucketTokenDialog } from './bitbucket-token-dialog'
import { gitConnectionStatusLabels, gitConnectionStatusTones } from './gitlab-connection-section'

/** OAuth connect comes back here; `bitbucket=connected` announces it. */
const CONNECT_RETURN_URL = '/connections?bitbucket=connected'

function ConnectionRow({
  connection,
  oauthAvailable,
  testing,
  onTest,
  onDelete,
}: {
  connection: BitbucketConnection
  /** The OAuth consumer is configured (Reconnect offered for an OAuth connection). */
  oauthAvailable: boolean
  testing: boolean
  onTest: () => void
  onDelete: () => void
}) {
  const c = connection
  const label = `${c.login} on Bitbucket`
  const canReconnect = c.authKind === 'oauth' && oauthAvailable
  return (
    <li aria-label={label} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="flex min-w-0 flex-col gap-1 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <BitbucketLogo />
          <span className="font-semibold break-words" data-testid="bitbucket-login">
            {c.login}
          </span>
          <StatusBadge tone={gitConnectionStatusTones[c.status]}>
            <span data-testid="bitbucket-connection-status">{gitConnectionStatusLabels[c.status]}</span>
          </StatusBadge>
        </span>
        <span>
          {c.authKind === 'oauth' ? 'OAuth · renews automatically' : 'Atlassian API token · replace it before it expires'}
        </span>
        <span data-testid="bitbucket-scopes">Scopes: {c.scopes.length > 0 ? c.scopes.join(', ') : 'none reported'}</span>
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
        {c.status === 'invalid' && (
          <span className="text-destructive">
            Bitbucket rejected this connection (it expired, was revoked, or the consumer was removed).{' '}
            {canReconnect ? 'Reconnect' : 'Connect a new token'}, then delete this one.
          </span>
        )}
        {c.status === 'insufficient_scope' && (
          <span className="text-destructive">
            This connection is missing scopes AIDE needs. Connect a token with all the listed scopes, then delete this one.
          </span>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        {c.status === 'invalid' && canReconnect && (
          <a href={bitbucketStartHref('connect', CONNECT_RETURN_URL)} className={buttonClasses('primary')}>
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
 * /connections → Source control → Bitbucket (FR-134): connect with OAuth (when an Admin configured the consumer) or an
 * Atlassian API token, and the caller's connections with test and delete. `callbackError` is a failed OAuth connect
 * (`?error=GIT_AUTH_FAILED&reason=…&provider=bitbucket`); `connected` is a successful one.
 */
export function BitbucketConnectionSection({
  callbackError,
  connected = false,
}: {
  callbackError?: { reason?: string }
  connected?: boolean
}) {
  const providers = useAuthProviders()
  const oauthAvailable = providers.data?.bitbucket === true
  const connections = useBitbucketConnections()
  const test = useTestBitbucketConnection()
  const remove = useDeleteBitbucketConnection()
  const [addingToken, setAddingToken] = useState(false)
  const [toDelete, setToDelete] = useState<BitbucketConnection | null>(null)
  const [actionError, setActionError] = useState<{ title: string; error: unknown } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const announced = useRef(false)
  useEffect(() => {
    if (connected && !announced.current) {
      announced.current = true
      toast.success('Bitbucket connected.')
    }
  }, [connected])

  const runTest = (c: BitbucketConnection) => {
    setActionError(null)
    test.mutate(c.id, {
      onSuccess: (result) => {
        if (result.status === 'valid') {
          toast.success(`${result.login} on Bitbucket works.`)
        } else {
          toast.error(`${result.login} on Bitbucket: ${gitConnectionStatusLabels[result.status].toLowerCase()}.`)
        }
      },
      onError: (error) => {
        setActionError({ title: `We couldn't test ${c.login} on Bitbucket`, error })
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
        toast.success(`${target.login} on Bitbucket deleted.`)
        headingRef.current?.focus()
      },
      onError: (error) => {
        setToDelete(null)
        setActionError({ title: `We couldn't delete ${target.login} on Bitbucket`, error })
      },
    })
  }

  let body
  if (connections.isPending) {
    body = <LoadingState label="Loading your Bitbucket connections…" />
  } else if (connections.isError) {
    body = (
      <ErrorState
        error={connections.error}
        title="Your Bitbucket connections couldn't load"
        onRetry={() => void connections.refetch()}
      />
    )
  } else if (connections.data.length === 0) {
    body = <p className="text-sm text-muted-foreground">Not connected.</p>
  } else {
    body = (
      <ul aria-label="Your Bitbucket connections" className="divide-y divide-border/60 rounded-lg border border-border/60">
        {connections.data.map((c) => (
          <ConnectionRow
            key={c.id}
            connection={c}
            oauthAvailable={oauthAvailable}
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
  }

  return (
    <section aria-labelledby="bitbucket-connection-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h3
            id="bitbucket-connection-title"
            ref={headingRef}
            tabIndex={-1}
            className="flex items-center gap-2 text-lg outline-none"
          >
            <BitbucketLogo className="size-5" />
            Bitbucket
          </h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Your Bitbucket Cloud accounts, used to pick and create repositories. Background Git work on a Bitbucket project
            uses the connection of the Owner who bound it.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          {oauthAvailable && (
            <a href={bitbucketStartHref('connect', CONNECT_RETURN_URL)} className={buttonClasses('primary')}>
              Connect with Bitbucket
            </a>
          )}
          <Button
            variant="secondary"
            onClick={() => {
              setAddingToken(true)
            }}
          >
            Use an API token
          </Button>
        </div>
      </div>
      {providers.isSuccess && !oauthAvailable && (
        <p className="text-sm text-muted-foreground">
          An administrator hasn&apos;t set up the Bitbucket OAuth consumer, so connect with an Atlassian API token.
        </p>
      )}
      {callbackError && (
        <Alert tone="error" role="alert" title="Bitbucket didn't connect">
          <p>{gitHostFailureMessage('bitbucket', callbackError.reason)}</p>
        </Alert>
      )}
      {actionError && (
        <Alert tone="error" role="alert" title={actionError.title}>
          <p>{errorMessage(actionError.error, 'bitbucketConnection')}</p>
        </Alert>
      )}
      {body}
      {addingToken && (
        <BitbucketTokenDialog
          oauthAvailable={oauthAvailable}
          onClose={() => {
            setAddingToken(false)
          }}
          onAdded={(c) => {
            setAddingToken(false)
            toast.success(`${c.login} on Bitbucket connected and checked.`)
          }}
        />
      )}
      <ConfirmDialog
        open={toDelete !== null}
        title={`Delete ${toDelete?.login ?? ''} on Bitbucket?`}
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
          their Git work fails until then. Signing in with Bitbucket still works and reconnects it.
        </p>
      </ConfirmDialog>
    </section>
  )
}
