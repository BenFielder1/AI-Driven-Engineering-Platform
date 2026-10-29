import { useEffect } from 'react'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { useBitbucketConnections, type BitbucketConnection, type BitbucketRepo } from '../bitbucket/bitbucket-api'
import { BitbucketRepoPicker } from '../bitbucket/bitbucket-repo-picker'

/** Whether a connection can list and create repositories. */
export const usableBitbucketConnection = (c: BitbucketConnection) => c.status === 'valid'

function connectionLabel(c: BitbucketConnection): string {
  const problem = c.status === 'valid' ? '' : c.status === 'invalid' ? ' (needs reconnecting)' : ' (missing scopes)'
  return `${c.login} (${c.authKind === 'oauth' ? 'OAuth' : 'API token'})${problem}`
}

/**
 * FR-135, FR-136, FR-141 Repository step, Bitbucket option: choose one of the caller's Bitbucket connections (it becomes
 * the project's binding), then pick a repository, or create one when starting from scratch. Without a usable connection
 * it links to Connections, keeping the wizard (FR-159 draft).
 */
export function WizardBitbucketRepository({
  connectionFieldId,
  searchId,
  connectionId,
  selected,
  importing,
  empty,
  connectionError,
  repoError,
  onConnection,
  onSelect,
  onAddConnection,
  onImportInstead,
  onScratchInstead,
}: {
  connectionFieldId: string
  searchId: string
  connectionId: string
  /** The selected repository's `workspace/slug`. */
  selected: string
  /** FR-141: importing an existing repository (no Create; the empty-repository notice). */
  importing: boolean
  /** Whether the selected repository is empty (from its refs), `undefined` while unknown. */
  empty: boolean | undefined
  connectionError?: string | undefined
  repoError?: string | undefined
  /** The chosen connection ('' when none is usable). */
  onConnection: (id: string) => void
  onSelect: (repo: BitbucketRepo) => void
  /** Saves the wizard before Connections opens. */
  onAddConnection: () => void
  /** FR-86: a repository with code, from scratch, becomes an import that was never released. */
  onImportInstead: () => void
  /** FR-141: an empty repository has nothing to import. */
  onScratchInstead: () => void
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
              allowCreate={!importing}
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
          {selected && empty === false && !importing && (
            <Alert tone="warning" title="This repository has code. AIDE will build on it">
              <p>
                The repository is treated as an import that was never released: the Repository Analyst reads the code
                first, and the first specification covers what is already there. Import it instead to say which version is
                released.
              </p>
              <p className="mt-2">
                <Button variant="secondary" onClick={onImportInstead}>
                  Import instead
                </Button>
              </p>
            </Alert>
          )}
          {selected && empty === true && importing && (
            <Alert tone="info" title="This repository is empty">
              <p>
                It has no commits yet (or only starter files), so there is no code for the Repository Analyst to read and
                no released version. Start from scratch on it instead, or pick another repository.
              </p>
              <p className="mt-2">
                <Button variant="secondary" onClick={onScratchInstead}>
                  Start from scratch instead
                </Button>
              </p>
            </Alert>
          )}
        </>
      )}
    </div>
  )
}
