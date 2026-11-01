import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'

import { StatusBadge } from '../../../components/shared/status-badge'
import { Alert } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { SelectField } from '../../../components/ui/select-field'
import { ErrorState, LoadingState } from '../../../components/ui/states'
import { errorMessage, isApiError } from '../../../lib/errors'
import { gitHostTerms, withGitHost } from '../../../lib/git-host'
import { Link } from '../../../lib/navigation'
import { useBitbucketConnections, type BitbucketConnection } from '../../bitbucket/bitbucket-api'
import { gitConnectionStatusLabels, gitConnectionStatusTones } from '../../connections/gitlab-connection-section'
import { GitHostLink } from '../../git-hosts/git-host-link'
import { gitlabHost, useGitLabConnections, type GitLabConnection } from '../../gitlab/gitlab-api'
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
  const host = gitHostTerms(git.mode).host
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
          <GitHostLink site={host} href={git.repoWebUrl}>
            {git.repoFullName}
          </GitHostLink>
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
              {withGitHost("Clones, pushes, {pr}s and releases use this connection's token.", gitHostTerms(git.mode))}
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
  const host = gitHostTerms(git.mode).host
  const terms = gitHostTerms(git.mode)
  if (!git.binding) {
    return (
      <Alert tone="warning" role="status" title={`No ${host} connection is bound`}>
        <p>{bindingClearedMessage(host)}</p>
        <p className="mt-1">
          {git.canRebind
            ? withGitHost('Until then, pushes, {pr}s and releases fail. Re-bind one of your own connections below.', terms)
            : withGitHost('Until then, pushes, {pr}s and releases fail. Ask an Owner to re-bind it.', terms)}
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

/** A connection the Rebind picker lists, whichever host it is on. */
interface RebindOption {
  id: string
  label: string
  usable: boolean
}

const usableGitLab = (c: GitLabConnection) => c.status === 'valid' && c.instanceEnabled

function gitlabOptionLabel(c: GitLabConnection): string {
  const problem = !c.instanceEnabled ? ' (instance disabled)' : c.status === 'valid' ? '' : ' (needs reconnecting)'
  return `${c.login} on ${c.instanceName} (${gitlabHost(c.instanceBaseUrl)})${problem}`
}

function bitbucketOptionLabel(c: BitbucketConnection): string {
  const problem = c.status === 'valid' ? '' : c.status === 'invalid' ? ' (needs reconnecting)' : ' (missing scopes)'
  return `${c.login} (${c.authKind === 'oauth' ? 'OAuth' : 'API token'})${problem}`
}

/** The connections a GitLab project can re-bind: the caller's, on the project's instance. */
function gitlabOptions(connections: readonly GitLabConnection[] | undefined, instanceId: string | undefined): RebindOption[] {
  return (connections ?? [])
    .filter((c) => c.instanceId === instanceId)
    .map((c) => ({ id: c.id, label: gitlabOptionLabel(c), usable: usableGitLab(c) }))
}

/** The connections a Bitbucket project can re-bind: all of the caller's (Bitbucket Cloud). */
function bitbucketOptions(connections: readonly BitbucketConnection[] | undefined): RebindOption[] {
  return (connections ?? []).map((c) => ({ id: c.id, label: bitbucketOptionLabel(c), usable: c.status === 'valid' }))
}

/**
 * FR-136 Rebind (Owners, `canRebind`): choose one of your own connections for the project's host (GitLab: on the
 * project's instance); AIDE checks it has Developer (GitLab) or write (Bitbucket) access to the repository, then binds
 * it.
 */
export function RebindConnection({ projectId, git }: { projectId: string; git: GitSettings }) {
  const enabled = git.canRebind === true
  const gitlab = useGitLabConnections(enabled && git.mode === 'gitlab')
  const bitbucket = useBitbucketConnections(enabled && git.mode === 'bitbucket')
  const update = useUpdateGitSettings(projectId)
  const [choice, setChoice] = useState('')
  const [error, setError] = useState<unknown>(null)
  if (!enabled || !isBoundHost(git)) {
    return null
  }
  const isBitbucket = git.mode === 'bitbucket'
  const host = gitHostTerms(git.mode).host
  const connections = isBitbucket ? bitbucket : gitlab
  const boundId = git.binding?.connectionId
  const candidates = isBitbucket ? bitbucketOptions(bitbucket.data) : gitlabOptions(gitlab.data, git.instance?.id)
  const options = candidates.filter((c) => c.usable && c.id !== boundId)
  const selected = options.some((c) => c.id === choice) ? choice : (options[0]?.id ?? '')
  const instanceName = git.instance ? `${git.instance.name} (${gitlabHost(git.instance.baseUrl)})` : 'this instance'
  const context = isBitbucket ? 'gitBindingBitbucket' : 'gitBinding'
  const fieldId = 'git-rebind-connection'
  const fieldError =
    error !== null && !isApiError(error, 'UPSTREAM_ERROR') && !isApiError(error, 'UPSTREAM_RATE_LIMITED')
      ? errorMessage(error, context)
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
              ? `The project now uses @${next.binding.login}'s ${host} connection.`
              : `The project now uses your ${host} connection.`,
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
        {git.binding ? 'Rebind' : `Re-bind the ${host} connection`}
      </h3>
      <p className="mb-3 mt-1 text-sm text-muted-foreground">
        {isBitbucket
          ? "Bind one of your own Bitbucket connections. AIDE checks it has write access to the repository, then uses it for all of this project's Git work."
          : `Bind one of your own GitLab connections on ${instanceName}. AIDE checks it has Developer access or higher on the repository, then uses it for all of this project's Git work.`}
      </p>
      {connections.isPending ? (
        <LoadingState label={`Loading your ${host} connections…`} />
      ) : connections.isError ? (
        <ErrorState
          error={connections.error}
          title={`Your ${host} connections couldn't load`}
          context={isBitbucket ? 'bitbucketConnection' : 'gitlabConnection'}
          onRetry={() => void connections.refetch()}
        />
      ) : options.length === 0 ? (
        <Alert
          tone="info"
          title={isBitbucket ? 'You have no other usable Bitbucket connection' : `You have no other usable GitLab connection on ${instanceName}`}
        >
          <p>
            {candidates.length > 0
              ? 'Your connections there need reconnecting, or are already bound. '
              : isBitbucket
                ? 'Connect Bitbucket first. '
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
            label={`Your ${host} connection`}
            value={selected}
            error={fieldError}
            onChange={(e) => {
              setChoice(e.target.value)
              setError(null)
            }}
          >
            {candidates.map((c) => (
              <option key={c.id} value={c.id} disabled={!c.usable || c.id === boundId}>
                {`${c.label}${c.id === boundId ? ' (bound now)' : ''}`}
              </option>
            ))}
          </SelectField>
          {error !== null && fieldError === undefined && (
            <Alert tone="error" role="alert" title="The connection wasn't bound">
              <p>{errorMessage(error, context)}</p>
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
