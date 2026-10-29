import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'

import { StatusBadge } from '../../../components/shared/status-badge'
import { Alert } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { SelectField } from '../../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../../components/ui/states'
import { errorMessage, isApiError } from '../../../lib/errors'
import { Link } from '../../../lib/navigation'
import { gitConnectionStatusLabels, gitConnectionStatusTones } from '../../connections/gitlab-connection-section'
import { gitlabHost, useGitLabConnections, type GitLabConnection } from '../../gitlab/gitlab-api'
import { GitLabLink } from '../../gitlab/gitlab-link'
import { gitProviderLabels } from '../labels'
import { useUpdateGitSettings, type GitSettings } from '../projects-api'

/** FR-136: the Git hosts whose projects carry a binding (the Owner's connection all background Git work uses). */
export const isBoundHost = (git: GitSettings): git is GitSettings & { mode: 'gitlab' | 'bitbucket' } =>
  git.mode === 'gitlab' || git.mode === 'bitbucket'

/** SPEC §6 / brief copy for a cleared binding (`git_binding_cleared`). */
export function bindingClearedMessage(host: string): string {
  return `The project's ${host} connection was removed. An Owner needs to re-bind it in Settings → Git.`
}

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="grid gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-muted-foreground">{term}</dt>
      <dd className="break-words text-sm sm:col-span-2">{children}</dd>
    </div>
  )
}

/**
 * FR-136: the host rows of a GitLab or Bitbucket project in the Connection summary: the instance, the repository (a
 * link to its page) and the binding ("connected by Ana (@ana)" with the connection's status), or the cleared state.
 */
export function BoundHostRows({ git }: { git: GitSettings }) {
  const host = gitProviderLabels[git.mode]
  const instance = git.instance
  return (
    <>
      <Row term="Host">
        {instance ? (
          <span data-testid="git-instance">
            {instance.name} ({gitlabHost(instance.baseUrl)})
            {!instance.enabled && (
              <span className="ml-2">
                <StatusBadge tone="warning">Instance disabled</StatusBadge>
              </span>
            )}
          </span>
        ) : (
          host
        )}
      </Row>
      <Row term="Repository">
        {git.repoWebUrl && git.repoFullName ? (
          <GitLabLink href={git.repoWebUrl}>{git.repoFullName}</GitLabLink>
        ) : (
          (git.repoFullName ?? 'Not set')
        )}
      </Row>
      <Row term="Connection">
        {git.binding ? (
          <span className="flex flex-col gap-1" data-testid="git-binding">
            <span>
              {host} · connected by <span className="font-medium">{git.binding.boundBy.name}</span> (@{git.binding.login})
            </span>
            <span>
              <StatusBadge tone={gitConnectionStatusTones[git.binding.status]}>
                {gitConnectionStatusLabels[git.binding.status]}
              </StatusBadge>
            </span>
            <span className="text-muted-foreground">
              Clones, pushes, merge requests and releases use this connection&apos;s token.
            </span>
          </span>
        ) : (
          <span data-testid="git-binding">
            <StatusBadge tone="destructive">Not bound</StatusBadge>
          </span>
        )}
      </Row>
    </>
  )
}

/**
 * FR-136: the binding-cleared warning (Git work fails with GIT_NOT_CONNECTED until an Owner re-binds), and an invalid
 * binding's warning.
 */
export function BindingAlert({ git }: { git: GitSettings }) {
  const host = gitProviderLabels[git.mode]
  if (!git.binding) {
    return (
      <Alert tone="warning" role="status" title={`No ${host} connection is bound`}>
        <p>{bindingClearedMessage(host)}</p>
        <p className="mt-1">
          {git.canRebind
            ? 'Until then, pushes, merge requests and releases fail. Re-bind one of your own connections below.'
            : 'Until then, pushes, merge requests and releases fail. Ask an Owner to re-bind it.'}
        </p>
      </Alert>
    )
  }
  if (git.binding.status !== 'valid') {
    return (
      <Alert tone="warning" title={`The bound ${host} connection doesn't work`}>
        <p>
          {host} rejected {git.binding.boundBy.name}&apos;s connection (@{git.binding.login}), so Git work fails until it is
          reconnected under Connections or an Owner re-binds their own connection.
        </p>
      </Alert>
    )
  }
  return null
}

const usable = (c: GitLabConnection) => c.status === 'valid' && c.instanceEnabled

function optionLabel(c: GitLabConnection, boundId: string | undefined): string {
  const problem = !c.instanceEnabled
    ? ' (instance disabled)'
    : c.status === 'valid'
      ? ''
      : ' (needs reconnecting)'
  return `${c.login} on ${c.instanceName} (${gitlabHost(c.instanceBaseUrl)})${c.id === boundId ? ' (bound now)' : ''}${problem}`
}

/**
 * FR-136 Rebind (Owners, `canRebind`): choose one of your own GitLab connections on the project's instance; AIDE checks
 * it has Developer access to the repository, then binds it.
 */
export function RebindGitLab({ projectId, git }: { projectId: string; git: GitSettings }) {
  const connections = useGitLabConnections(git.canRebind === true)
  const update = useUpdateGitSettings(projectId)
  const [choice, setChoice] = useState('')
  const [error, setError] = useState<unknown>(null)
  if (!git.canRebind || git.mode !== 'gitlab') {
    return null
  }
  const instanceId = git.instance?.id
  const boundId = git.binding?.connectionId
  const onInstance = (connections.data ?? []).filter((c) => c.instanceId === instanceId)
  const options = onInstance.filter((c) => usable(c) && c.id !== boundId)
  const selected = options.some((c) => c.id === choice) ? choice : (options[0]?.id ?? '')
  const instanceName = git.instance ? `${git.instance.name} (${gitlabHost(git.instance.baseUrl)})` : 'this instance'
  const fieldId = 'git-rebind-connection'
  const fieldError = error !== null && !isApiError(error, 'UPSTREAM_ERROR') && !isApiError(error, 'UPSTREAM_RATE_LIMITED')
    ? errorMessage(error, 'gitBinding')
    : undefined

  const rebind = () => {
    if (!selected) {
      return
    }
    setError(null)
    update.mutate(
      { connectionId: selected },
      {
        onSuccess: (next) => {
          setChoice('')
          toast.success(
            next.binding
              ? `The project now uses @${next.binding.login}'s GitLab connection.`
              : 'The project now uses your GitLab connection.',
          )
        },
        onError: (e) => {
          setError(e)
          if (isApiError(e, 'NOT_FOUND') || isApiError(e, 'GIT_NOT_CONNECTED')) {
            void connections.refetch()
          }
        },
      },
    )
  }

  return (
    <section aria-labelledby="git-rebind-title" className="rounded-lg border border-border/60 bg-background/40 p-4">
      <h3 id="git-rebind-title" className="font-semibold">
        {git.binding ? 'Rebind' : 'Re-bind the GitLab connection'}
      </h3>
      <p className="mb-3 mt-1 text-sm text-muted-foreground">
        Bind one of your own GitLab connections on {instanceName}. AIDE checks it has Developer access or higher on the
        repository, then uses it for all of this project&apos;s Git work.
      </p>
      {connections.isPending ? (
        <LoadingState label="Loading your GitLab connections…" />
      ) : connections.isError ? (
        <ErrorState
          error={connections.error}
          title="Your GitLab connections couldn't load"
          context="gitlabConnection"
          onRetry={() => void connections.refetch()}
        />
      ) : options.length === 0 ? (
        <Alert tone="info" title={`You have no other usable GitLab connection on ${instanceName}`}>
          <p>
            {onInstance.length > 0
              ? 'Your connections there need reconnecting, or are already bound. '
              : 'Connect GitLab on this instance first. '}
            <Link to="/connections" className="text-primary underline underline-offset-2">
              Go to Connections
            </Link>
          </p>
        </Alert>
      ) : (
        <div className="flex flex-col gap-3">
          <SelectField
            id={fieldId}
            label="Your GitLab connection"
            value={selected}
            error={fieldError}
            onChange={(e) => {
              setChoice(e.target.value)
              setError(null)
            }}
          >
            {onInstance.map((c) => (
              <option key={c.id} value={c.id} disabled={!usable(c) || c.id === boundId}>
                {optionLabel(c, boundId)}
              </option>
            ))}
          </SelectField>
          {error !== null && fieldError === undefined && (
            <Alert tone="error" role="alert" title="The connection wasn't bound">
              <p>{errorMessage(error, 'gitBinding')}</p>
            </Alert>
          )}
          {isApiError(error, 'GIT_NOT_CONNECTED') && (
            <p className="text-sm">
              <Link to="/connections" className="text-primary underline underline-offset-2">
                Go to Connections
              </Link>
            </p>
          )}
          <div>
            <Button onClick={rebind} pending={update.isPending} pendingLabel="Binding…">
              {git.binding ? 'Rebind' : 'Bind connection'}
            </Button>
          </div>
        </div>
      )}
    </section>
  )
}
