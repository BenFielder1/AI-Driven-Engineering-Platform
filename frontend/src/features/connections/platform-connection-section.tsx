import { useRef, useState } from 'react'
import { toast } from 'sonner'

import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { platformConnectionMessage } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import { formatDateTime } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { isPast, maskedKey } from './connection-format'
import { CredentialStatusBadge } from './credential-status-badge'
import { AddPlatformConnectionDialog, RenamePlatformConnectionDialog } from './platform-connection-dialogs'
import {
  useDeletePlatformConnection,
  usePlatformConnections,
  useTestPlatformConnection,
  type PlatformConnection,
  type PlatformProvider,
} from './platform-connections-api'
import { PlatformLogo } from './platform-logos'
import { platformAccountLabel, platformProviders } from './platform-providers'

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

/** The token's scopes, or a Cloudflare token's policies (what the platform reported; nothing when it reported none). */
function grantedOf(c: PlatformConnection): readonly string[] {
  return c.scopes.length > 0 ? c.scopes : c.metadata.policies
}

function PlatformRow({
  connection,
  testing,
  onTest,
  onRename,
  onDelete,
}: {
  connection: PlatformConnection
  testing: boolean
  onTest: () => void
  onRename: () => void
  onDelete: () => void
}) {
  const c = connection
  const info = platformProviders[c.provider]
  const account = platformAccountLabel(c)
  const granted = grantedOf(c)
  const missing = c.metadata.missingPermissions
  return (
    <li aria-label={`${info.name} connection ${c.label}`} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="flex min-w-0 flex-col gap-1 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <span className="font-semibold break-words">{c.label}</span>
          <span className="font-mono text-muted-foreground" data-testid="platform-last4">
            {maskedKey(c.last4)}
          </span>
          <CredentialStatusBadge status={c.status} />
        </span>
        <span data-testid="platform-account">
          {account ?? `${info.accountNoun} not reported`}
          {' · '}
          <ExpiryText expiresAt={c.expiresAt} />
        </span>
        {granted.length > 0 && (
          <span className="break-words text-muted-foreground" data-testid="platform-scopes">
            {c.scopes.length > 0 ? 'Scopes' : 'Permissions'}: {granted.join(', ')}
          </span>
        )}
        {c.status === 'insufficient_scope' && (
          <div data-testid="platform-missing-permissions">
            {missing.length > 0 ? (
              <>
                <span>
                  {info.name} says this token is missing {missing.length === 1 ? 'a permission' : 'permissions'}. Add{' '}
                  {missing.length === 1 ? 'it' : 'them'} to the token on {info.name} (or create a new one), then Re-test:
                </span>
                <ul aria-label={`Missing permissions of ${c.label}`} className="mt-1 list-disc pl-5">
                  {missing.map((m) => (
                    <li key={m}>{m}</li>
                  ))}
                </ul>
              </>
            ) : (
              <span>
                {info.name} says this token is missing permissions. Check them against the list in Add connection, then Re-test.
              </span>
            )}
          </div>
        )}
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
          <span className="text-destructive">
            {info.name} rejected this token. Add a new connection, then delete this one.
          </span>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" pending={testing} pendingLabel="Testing…" onClick={onTest} aria-label={`Re-test ${c.label}`}>
          Re-test
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

function testResultCopy(c: PlatformConnection): { ok: boolean; text: string } {
  const name = platformProviders[c.provider].name
  switch (c.status) {
    case 'valid':
      return { ok: true, text: `"${c.label}" works.` }
    case 'insufficient_scope': {
      const missing = c.metadata.missingPermissions
      return {
        ok: false,
        text:
          missing.length > 0
            ? `"${c.label}" is missing permissions on ${name}: ${missing.join(', ')}.`
            : `"${c.label}" is missing permissions on ${name}.`,
      }
    }
    case 'invalid':
      return { ok: false, text: `"${c.label}" is invalid: ${name} rejected it.` }
    default:
      return { ok: false, text: `"${c.label}" couldn't be checked yet.` }
  }
}

/**
 * /connections → Deployment platforms or Databases (FR-142): per provider, the caller's connections (never the tokens)
 * with Re-test, Rename and Delete (as the v2 Vercel connections), and Add connection with what to create on the provider.
 */
export function PlatformConnectionSection({
  id,
  title,
  description,
  providers,
}: {
  id: string
  title: string
  description: string
  providers: readonly PlatformProvider[]
}) {
  const connections = usePlatformConnections()
  const test = useTestPlatformConnection()
  const remove = useDeletePlatformConnection()
  const [adding, setAdding] = useState<PlatformProvider | null>(null)
  const [renaming, setRenaming] = useState<PlatformConnection | null>(null)
  const [toDelete, setToDelete] = useState<PlatformConnection | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [actionErrorTitle, setActionErrorTitle] = useState('')
  const headingRef = useRef<HTMLHeadingElement>(null)
  const titleId = `${id}-title`

  const fail = (heading: string, error: unknown, c: PlatformConnection) => {
    setActionErrorTitle(heading)
    setActionError(platformConnectionMessage(error, platformProviders[c.provider]))
  }

  const runTest = (c: PlatformConnection) => {
    setActionError(null)
    test.mutate(c.id, {
      onSuccess: (result) => {
        const { ok, text } = testResultCopy(result)
        if (ok) {
          toast.success(text)
        } else {
          toast.error(text)
        }
      },
      onError: (error) => {
        fail(`We couldn't test "${c.label}"`, error, c)
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
        fail(`We couldn't delete "${target.label}"`, error, target)
      },
    })
  }

  let body
  if (connections.isPending) {
    body = <LoadingState label={`Loading your ${title.toLowerCase()} connections…`} />
  } else if (connections.isError) {
    body = (
      <ErrorState
        error={connections.error}
        title={`Your ${title.toLowerCase()} connections couldn't load`}
        onRetry={() => void connections.refetch()}
      />
    )
  } else {
    const all = connections.data
    body = (
      <div className="flex flex-col gap-4">
        {providers.map((provider) => {
          const info = platformProviders[provider]
          const own = all.filter((c) => c.provider === provider)
          const providerTitleId = `${provider}-connections-title`
          return (
            <section key={provider} aria-labelledby={providerTitleId} className="flex flex-col gap-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h3 id={providerTitleId} className="flex items-center gap-2 font-semibold">
                  <PlatformLogo provider={provider} className="size-5" />
                  {info.name}
                </h3>
                <Button
                  variant="secondary"
                  aria-label={`Add ${info.name} connection`}
                  onClick={() => {
                    setAdding(provider)
                  }}
                >
                  Add connection
                </Button>
              </div>
              {own.length === 0 ? (
                <p className="text-sm text-muted-foreground">Not connected</p>
              ) : (
                <ul aria-label={`Your ${info.name} connections`} className="divide-y divide-border/60 rounded-lg border border-border/60">
                  {own.map((c) => (
                    <PlatformRow
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
              )}
            </section>
          )
        })}
      </div>
    )
  }

  return (
    <section aria-labelledby={titleId} className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div>
        <h2 id={titleId} ref={headingRef} tabIndex={-1} className="text-lg outline-none">
          {title}
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">{description}</p>
      </div>
      {actionError && (
        <Alert tone="error" role="alert" title={actionErrorTitle}>
          <p>{actionError}</p>
        </Alert>
      )}
      {body}

      {adding && (
        <AddPlatformConnectionDialog
          provider={adding}
          onClose={() => {
            setAdding(null)
          }}
          onAdded={(c) => {
            setAdding(null)
            if (c.status === 'insufficient_scope') {
              toast.warning(`"${c.label}" added, but it is missing permissions. The list shows which.`)
            } else {
              toast.success(`"${c.label}" added and checked.`)
            }
          }}
        />
      )}
      {renaming && (
        <RenamePlatformConnectionDialog
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
        confirmLabel="Delete connection"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmDelete}
        onCancel={() => {
          setToDelete(null)
        }}
      >
        {toDelete && toDelete.boundProjects.length > 0 ? (
          <div className="flex flex-col gap-2" data-testid="platform-delete-bound-warning">
            <p>
              This connection is bound to{' '}
              {toDelete.boundProjects.length === 1 ? 'a project' : `${String(toDelete.boundProjects.length)} projects`}. Deleting
              it unbinds it, and their new deployments fail (DEPLOYMENT_NOT_CONFIGURED) until an Owner binds another
              connection. Their deployment settings and history stay:
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
