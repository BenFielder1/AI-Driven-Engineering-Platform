import { Triangle } from 'lucide-react'
import { useRef, useState } from 'react'
import { toast } from 'sonner'

import { EmptyState } from '../../components/shared/empty-state'
import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import { formatDateTime } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { credentialStatusLabels, isPast, maskedKey, vercelScopeLabel } from './connection-format'
import { CredentialStatusBadge } from './credential-status-badge'
import { AddVercelTokenDialog, RenameVercelTokenDialog } from './vercel-connection-dialogs'
import {
  useDeleteVercelConnection,
  useTestVercelConnection,
  useVercelConnections,
  type VercelConnection,
} from './vercel-connections-api'

function ExpiryText({ expiresAt }: { expiresAt: string | null }) {
  if (!expiresAt) {
    return <>No expiry</>
  }
  const expired = isPast(expiresAt)
  return (
    <span className={cn(expired && 'text-destructive')}>
      {expired ? 'Expired' : 'Expires'} {formatDateTime(expiresAt)}
    </span>
  )
}

function VercelRow({
  connection,
  testing,
  onTest,
  onRename,
  onDelete,
}: {
  connection: VercelConnection
  testing: boolean
  onTest: () => void
  onRename: () => void
  onDelete: () => void
}) {
  const c = connection
  return (
    <li aria-label={`Vercel token ${c.label}`} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="flex min-w-0 flex-col gap-1 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <Triangle aria-hidden className="size-4 text-muted-foreground" />
          <span className="font-semibold break-words">{c.label}</span>
          <span className="font-mono text-muted-foreground" data-testid="vercel-last4">
            {maskedKey(c.last4)}
          </span>
          <CredentialStatusBadge status={c.status} />
        </span>
        <span data-testid="vercel-scope">
          {vercelScopeLabel(c)}
          {' · '}
          <ExpiryText expiresAt={c.expiresAt} />
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
        <span className="text-muted-foreground">
          {c.boundProjects.length === 0 ? (
            'Not used by any project'
          ) : (
            <>
              Used by{' '}
              {c.boundProjects.map((p, i) => (
                <span key={p.id}>
                  {i > 0 && ', '}
                  <Link
                    to="/projects/$projectId/settings/$section"
                    params={{ projectId: p.id, section: 'deployment' }}
                    className="text-foreground underline underline-offset-2"
                  >
                    {p.name}
                  </Link>
                </span>
              ))}
            </>
          )}
        </span>
        {c.status === 'invalid' && (
          <span className="text-destructive">Vercel rejected this token. Add a new token, then delete this one.</span>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" pending={testing} pendingLabel="Testing…" onClick={onTest} aria-label={`Test ${c.label}`}>
          Test
        </Button>
        <Button variant="secondary" onClick={onRename} aria-label={`Rename ${c.label}`}>
          Rename
        </Button>
        <Button variant="danger" onClick={onDelete} aria-label={`Delete ${c.label}`}>
          Delete
        </Button>
      </div>
    </li>
  )
}

/** /connections → Vercel (FR-87): the caller's Vercel tokens (never the tokens themselves), with test, rename, delete. */
export function VercelConnectionSection() {
  const connections = useVercelConnections()
  const test = useTestVercelConnection()
  const remove = useDeleteVercelConnection()
  const [adding, setAdding] = useState(false)
  const [renaming, setRenaming] = useState<VercelConnection | null>(null)
  const [toDelete, setToDelete] = useState<VercelConnection | null>(null)
  const [actionError, setActionError] = useState<{ title: string; error: unknown } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const runTest = (c: VercelConnection) => {
    setActionError(null)
    test.mutate(c.id, {
      onSuccess: (result) => {
        if (result.status === 'valid') {
          toast.success(`"${result.label}" works.`)
        } else {
          toast.error(`"${result.label}" is ${credentialStatusLabels[result.status].toLowerCase()}: Vercel rejected it.`)
        }
      },
      onError: (error) => {
        setActionError({ title: `We couldn't test "${c.label}"`, error })
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
        toast.success(`"${target.label}" deleted.`)
        headingRef.current?.focus()
      },
      onError: (error) => {
        setToDelete(null)
        setActionError({ title: `We couldn't delete "${target.label}"`, error })
      },
    })
  }

  let body
  if (connections.isPending) {
    body = <LoadingState label="Loading Vercel connections…" />
  } else if (connections.isError) {
    body = <ErrorState error={connections.error} title="Your Vercel connections couldn't load" onRetry={() => void connections.refetch()} />
  } else if (connections.data.length === 0) {
    body = (
      <EmptyState
        variant="not_connected"
        headingLevel={3}
        bare
        title="Not connected"
        description="Add a Vercel access token, then bind it to a project you own in its Settings → Deployment."
      />
    )
  } else {
    body = (
      <ul aria-label="Your Vercel tokens" className="divide-y divide-border/60 rounded-lg border border-border/60">
        {connections.data.map((c) => (
          <VercelRow
            key={c.id}
            connection={c}
            testing={test.isPending && test.variables === c.id}
            onTest={() => {
              runTest(c)
            }}
            onRename={() => {
              setRenaming(c)
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
    <section aria-labelledby="vercel-connection-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h2 id="vercel-connection-title" ref={headingRef} tabIndex={-1} className="text-lg outline-none">
            Vercel
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">
            Your Vercel access tokens. A project deploys with the token its Owner binds in Settings → Deployment.
          </p>
        </div>
        <Button
          onClick={() => {
            setAdding(true)
          }}
        >
          Add token
        </Button>
      </div>
      {actionError && (
        <Alert tone="error" role="alert" title={actionError.title}>
          <p>{errorMessage(actionError.error, 'vercelConnection')}</p>
        </Alert>
      )}
      {body}

      <AddVercelTokenDialog
        open={adding}
        onClose={() => {
          setAdding(false)
        }}
        onAdded={(c) => {
          setAdding(false)
          toast.success(`"${c.label}" added and checked.`)
        }}
      />
      {renaming && (
        <RenameVercelTokenDialog
          connection={renaming}
          onClose={(renamed) => {
            setRenaming(null)
            if (renamed) {
              toast.success('Label saved.')
            }
          }}
        />
      )}
      <ConfirmDialog
        open={toDelete !== null}
        title={`Delete "${toDelete?.label ?? ''}"?`}
        confirmLabel="Delete token"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmDelete}
        onCancel={() => {
          setToDelete(null)
        }}
      >
        {toDelete && toDelete.boundProjects.length > 0 ? (
          <div className="flex flex-col gap-2" data-testid="vercel-delete-bound-warning">
            <p>
              This token is bound to{' '}
              {toDelete.boundProjects.length === 1 ? 'a project' : `${String(toDelete.boundProjects.length)} projects`}. Deleting
              it unbinds it, and their new deployments fail (DEPLOYMENT_NOT_CONFIGURED) until an Owner binds another token. Their
              deployment settings and history stay:
            </p>
            <ul className="list-disc pl-5">
              {toDelete.boundProjects.map((p) => (
                <li key={p.id}>{p.name}</li>
              ))}
            </ul>
          </div>
        ) : (
          <p>The token is removed from AIDE. No project uses it. This can&apos;t be undone.</p>
        )}
      </ConfirmDialog>
    </section>
  )
}
