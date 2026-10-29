import { useEffect } from 'react'

import { Alert } from '../../components/ui/alert'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { useBitbucketConnections, type BitbucketConnection, type BitbucketRepo } from '../bitbucket/bitbucket-api'
import { BitbucketRepoPicker } from '../bitbucket/bitbucket-repo-picker'
import { bitbucketProjectCreationAvailable } from './schemas'

/** Whether a connection can list and create repositories. */
export const usableBitbucketConnection = (c: BitbucketConnection) => c.status === 'valid'

function connectionLabel(c: BitbucketConnection): string {
  const problem = c.status === 'valid' ? '' : c.status === 'invalid' ? ' (needs reconnecting)' : ' (missing scopes)'
  return `${c.login} (${c.authKind === 'oauth' ? 'OAuth' : 'API token'})${problem}`
}

/**
 * FR-135 Repository step, Bitbucket option: choose one of the caller's Bitbucket connections, then pick a repository
 * (or create one once projects can bind Bitbucket, slice 2). Without a usable connection it links to Connections,
 * keeping the wizard (FR-159 draft).
 */
export function WizardBitbucketRepository({
  connectionFieldId,
  searchId,
  connectionId,
  selected,
  importing,
  connectionError,
  repoError,
  onConnection,
  onSelect,
  onAddConnection,
}: {
  connectionFieldId: string
  searchId: string
  connectionId: string
  /** The selected repository's `workspace/slug`. */
  selected: string
  /** Importing an existing repository (no Create). */
  importing: boolean
  connectionError?: string | undefined
  repoError?: string | undefined
  /** The chosen connection ('' when none is usable). */
  onConnection: (id: string) => void
  onSelect: (repo: BitbucketRepo) => void
  /** Saves the wizard before Connections opens. */
  onAddConnection: () => void
}) {
  const connections = useBitbucketConnections()
  const usable = (connections.data ?? []).filter(usableBitbucketConnection)
  const firstUsable = usable[0]?.id ?? ''
  const current = usable.find((c) => c.id === connectionId)

  // Preselect the first usable connection; drop one that is no longer usable.
  useEffect(() => {
    if (connections.data && !current && firstUsable !== connectionId) {
      onConnection(firstUsable)
    }
  }, [connections.data, current, firstUsable, connectionId, onConnection])

  const addLink = (
    <Link to="/connections" className="text-primary underline underline-offset-2" onClick={onAddConnection}>
      Connect Bitbucket under Connections
    </Link>
  )
  const creationAvailable = bitbucketProjectCreationAvailable()

  if (connections.isPending) {
    return <LoadingState label="Loading your Bitbucket connections…" />
  }
  if (connections.isError) {
    return (
      <ErrorState
        error={connections.error}
        title="Your Bitbucket connections couldn't load"
        onRetry={() => void connections.refetch()}
        context="bitbucketConnection"
      />
    )
  }
  return (
    // The connection field's id is on this wrapper too, so the error summary's link always has a target.
    <div id={usable.length === 0 ? connectionFieldId : undefined} tabIndex={-1} className="flex flex-col gap-3 outline-none">
      {!creationAvailable && (
        <Alert tone="info" title="Bitbucket projects arrive in the next AIDE update">
          <p>
            You can browse your Bitbucket repositories here already, but AIDE can&apos;t create an AIDE project on Bitbucket
            yet. Choose another option to continue.
          </p>
        </Alert>
      )}
      {usable.length === 0 ? (
        <Alert tone="info" title="Connect Bitbucket to pick a repository">
          <p>
            {connections.data.length > 0
              ? 'None of your Bitbucket connections work right now (they need reconnecting or more scopes). '
              : 'You have no Bitbucket connection yet. '}
            {addLink}; what you entered here is kept.
          </p>
          {connectionError && <p className="mt-2 text-destructive">{connectionError}</p>}
        </Alert>
      ) : (
        <>
          <SelectField
            id={connectionFieldId}
            label="Bitbucket connection"
            hint="The Bitbucket account AIDE uses for this project."
            value={connectionId}
            error={connectionError}
            onChange={(e) => {
              onConnection(e.target.value)
            }}
          >
            {connections.data.map((c) => (
              <option key={c.id} value={c.id} disabled={!usableBitbucketConnection(c)}>
                {connectionLabel(c)}
              </option>
            ))}
          </SelectField>
          <p className="text-sm text-muted-foreground">Another account? {addLink}.</p>
          {current && (
            <BitbucketRepoPicker
              key={current.id}
              connectionId={current.id}
              searchId={searchId}
              selected={selected}
              error={repoError}
              onSelect={onSelect}
              allowCreate={creationAvailable && !importing}
            />
          )}
          <p role="status" aria-live="polite" className="text-sm">
            {selected ? (
              <>
                Selected: <span className="font-medium">{selected}</span>
              </>
            ) : (
              <span className="text-muted-foreground">No repository selected yet.</span>
            )}
          </p>
        </>
      )}
    </div>
  )
}
