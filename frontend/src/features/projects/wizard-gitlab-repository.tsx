import { useEffect } from 'react'

import { Alert } from '../../components/ui/alert'
import { SelectField } from '../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { Link } from '../../lib/navigation'
import { gitlabHost, useGitLabConnections, type GitLabConnection, type GitLabRepo } from '../gitlab/gitlab-api'
import { GitLabRepoPicker } from '../gitlab/gitlab-repo-picker'
import { gitlabProjectCreationAvailable } from './schemas'

/** Whether a connection can list and create projects (valid, on an enabled instance). */
export const usableGitLabConnection = (c: GitLabConnection) => c.status === 'valid' && c.instanceEnabled

function connectionLabel(c: GitLabConnection): string {
  const problem = !c.instanceEnabled ? ' (instance disabled)' : c.status === 'valid' ? '' : ' (needs reconnecting)'
  return `${c.login} on ${c.instanceName} (${gitlabHost(c.instanceBaseUrl)})${problem}`
}

/**
 * FR-132 Repository step, GitLab option: choose one of the caller's GitLab connections (its instance), then pick a
 * project or create one. Without a usable connection it links to Connections, keeping the wizard (FR-159 draft).
 */
export function WizardGitLabRepository({
  connectionFieldId,
  searchId,
  connectionId,
  selected,
  connectionError,
  repoError,
  onConnection,
  onSelect,
  onAddConnection,
}: {
  connectionFieldId: string
  searchId: string
  connectionId: string
  /** The selected project's full path. */
  selected: string
  connectionError?: string | undefined
  repoError?: string | undefined
  onConnection: (id: string) => void
  onSelect: (repo: GitLabRepo) => void
  /** Saves the wizard before Connections opens. */
  onAddConnection: () => void
}) {
  const connections = useGitLabConnections()
  const usable = (connections.data ?? []).filter(usableGitLabConnection)
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
      Connect GitLab under Connections
    </Link>
  )

  if (connections.isPending) {
    return <LoadingState label="Loading your GitLab connections…" />
  }
  if (connections.isError) {
    return (
      <ErrorState
        error={connections.error}
        title="Your GitLab connections couldn't load"
        onRetry={() => void connections.refetch()}
        context="gitlabConnection"
      />
    )
  }
  return (
    // The connection field's id is on this wrapper too, so the error summary's link always has a target.
    <div id={usable.length === 0 ? connectionFieldId : undefined} tabIndex={-1} className="flex flex-col gap-3 outline-none">
      {!gitlabProjectCreationAvailable() && (
        <Alert tone="info" title="GitLab projects arrive in the next AIDE update">
          <p>
            You can browse your GitLab projects here already, but AIDE can&apos;t create an AIDE project on GitLab yet.
            Choose another option to continue.
          </p>
        </Alert>
      )}
      {usable.length === 0 ? (
        <Alert tone="info" title="Connect GitLab to pick a project">
          <p>
            {(connections.data.length > 0
              ? 'None of your GitLab connections work right now (they need reconnecting, or their instance is disabled). '
              : 'You have no GitLab connection yet. ')}
            {addLink}; what you entered here is kept.
          </p>
          {connectionError && <p className="mt-2 text-destructive">{connectionError}</p>}
        </Alert>
      ) : (
        <>
          <SelectField
            id={connectionFieldId}
            label="GitLab connection"
            hint="The account and instance AIDE uses for this project."
            value={connectionId}
            error={connectionError}
            onChange={(e) => {
              onConnection(e.target.value)
            }}
          >
            {connections.data.map((c) => (
              <option key={c.id} value={c.id} disabled={!usableGitLabConnection(c)}>
                {connectionLabel(c)}
              </option>
            ))}
          </SelectField>
          <p className="text-sm text-muted-foreground">Another account or instance? {addLink}.</p>
          {current && (
            <GitLabRepoPicker
              key={current.id}
              connectionId={current.id}
              searchId={searchId}
              selected={selected}
              error={repoError}
              onSelect={onSelect}
              allowCreate={gitlabProjectCreationAvailable()}
            />
          )}
          <p role="status" aria-live="polite" className="text-sm">
            {selected ? (
              <>
                Selected: <span className="font-medium">{selected}</span>
              </>
            ) : (
              <span className="text-muted-foreground">No project selected yet.</span>
            )}
          </p>
        </>
      )}
    </div>
  )
}
