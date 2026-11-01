import { KeyRound } from 'lucide-react'
import { useRef, useState, useSyncExternalStore } from 'react'
import { toast } from 'sonner'

import { EmptyState } from '../../components/shared/empty-state'
import { PageHeader } from '../../components/shared/page-header'
import { panelClass } from '../../components/shared/panel-class'
import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { errorMessage } from '../../lib/errors'
import { Link } from '../../lib/navigation'
import { cn } from '../../lib/utils'
import { llmProviderLabels } from '../projects/labels'
import { hasWizardDraft } from '../projects/wizard-draft'
import { RelativeTime } from '../projects/relative-time'
import { credentialStatusLabels, maskedKey } from './connection-format'
import { useDeleteLlmConnection, useLlmConnections, useTestLlmConnection, type LlmCredential } from './connections-api'
import { CredentialStatusBadge } from './credential-status-badge'
import { BitbucketConnectionSection } from './bitbucket-connection-section'
import { GitHubConnectionSection } from './github-connection-section'
import { GitLabConnectionSection } from './gitlab-connection-section'
import { AddLlmKeyDialog, RenameLlmKeyDialog } from './llm-connection-dialogs'
import { PlatformConnectionSection } from './platform-connection-section'
import { databasePlatformIds, deploymentPlatformIds } from './platform-providers'

const keylessDescriptions: Partial<Record<LlmCredential['provider'], string>> = {
  ollama: 'Uses the Ollama server configured for AIDE. No key needed.',
  fake: 'Deterministic test replies. No key needed.',
}

function CredentialRow({
  credential,
  testing,
  onTest,
  onRename,
  onDelete,
}: {
  credential: LlmCredential
  testing: boolean
  onTest: () => void
  onRename: () => void
  onDelete: () => void
}) {
  const c = credential
  return (
    <li
      aria-label={`${llmProviderLabels[c.provider]} key ${c.label}`}
      className="flex flex-col gap-3 p-4 sm:flex-row sm:items-start sm:justify-between"
    >
      <div className="flex min-w-0 flex-col gap-1 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <KeyRound aria-hidden className="size-4 text-muted-foreground" />
          <span className="font-semibold">{llmProviderLabels[c.provider]}</span>
          <span className="break-words">{c.label}</span>
          <span className="font-mono text-muted-foreground" data-testid="credential-last4">
            {maskedKey(c.last4)}
          </span>
          <CredentialStatusBadge status={c.status} />
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
                    params={{ projectId: p.id, section: 'llm' }}
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
          <span className="text-destructive">The provider rejected this key. Add a new key, then delete this one.</span>
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

/** /connections → LLM providers (FR-78): the caller's keys (never the keys themselves) and the keyless providers. */
function LlmProvidersSection() {
  const connections = useLlmConnections()
  const test = useTestLlmConnection()
  const remove = useDeleteLlmConnection()
  const [adding, setAdding] = useState(false)
  const [renaming, setRenaming] = useState<LlmCredential | null>(null)
  const [toDelete, setToDelete] = useState<LlmCredential | null>(null)
  const [actionError, setActionError] = useState<{ title: string; error: unknown } | null>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  const runTest = (c: LlmCredential) => {
    setActionError(null)
    test.mutate(c.id, {
      onSuccess: (result) => {
        if (result.status === 'valid') {
          toast.success(`"${result.label}" works.`)
        } else {
          toast.error(`"${result.label}" is ${credentialStatusLabels[result.status].toLowerCase()}: the provider rejected it.`)
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
    body = <LoadingState label="Loading LLM connections…" />
  } else if (connections.isError) {
    body = <ErrorState error={connections.error} title="Your LLM connections couldn't load" onRetry={() => void connections.refetch()} />
  } else {
    const { credentials, keylessProviders } = connections.data
    body = (
      <div className="flex flex-col gap-4">
        {credentials.length === 0 ? (
          <EmptyState
            variant="not_connected"
            headingLevel={3}
            bare
            title="No LLM keys yet"
            description="Add an Anthropic or OpenAI key, then bind it to a project you own in its Settings → LLM."
          />
        ) : (
          <ul aria-label="Your LLM keys" className="divide-y divide-border/60 rounded-lg border border-border/60">
            {credentials.map((c) => (
              <CredentialRow
                key={c.id}
                credential={c}
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
        {keylessProviders.length > 0 && (
          <div>
            <h3 className="mb-2 text-sm font-semibold">Always available</h3>
            <ul aria-label="Providers without a key" className="flex flex-col gap-2">
              {keylessProviders.map((p) => (
                <li key={p} className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-medium">{llmProviderLabels[p]}</span>
                  <span className="text-muted-foreground">{keylessDescriptions[p] ?? 'No key needed.'}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    )
  }

  return (
    <section aria-labelledby="llm-connections-title" className={cn(panelClass, 'flex flex-col gap-4 p-5')}>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h2 id="llm-connections-title" ref={headingRef} tabIndex={-1} className="text-lg outline-none">
            LLM providers
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">
            Your own provider keys. A project uses the key its Owner binds in Settings → LLM, whoever starts a run.
          </p>
        </div>
        <Button
          onClick={() => {
            setAdding(true)
          }}
        >
          Add key
        </Button>
      </div>
      {actionError && (
        <Alert tone="error" role="alert" title={actionError.title}>
          <p>{errorMessage(actionError.error, 'llmConnection')}</p>
        </Alert>
      )}
      {body}

      <AddLlmKeyDialog
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
        <RenameLlmKeyDialog
          credential={renaming}
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
        confirmLabel="Delete key"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onConfirm={confirmDelete}
        onCancel={() => {
          setToDelete(null)
        }}
      >
        {toDelete && toDelete.boundProjects.length > 0 ? (
          <div className="flex flex-col gap-2" data-testid="delete-bound-warning">
            <p>
              This key is bound to {toDelete.boundProjects.length === 1 ? 'a project' : `${String(toDelete.boundProjects.length)} projects`}.
              Deleting it unbinds it from these projects, and their agent runs fail until an Owner binds another connection
              (unless an admin allows system keys):
            </p>
            <ul className="list-disc pl-5">
              {toDelete.boundProjects.map((p) => (
                <li key={p.id}>{p.name}</li>
              ))}
            </ul>
          </div>
        ) : (
          <p>The key is removed from AIDE. No project uses it. This can&apos;t be undone.</p>
        )}
      </ConfirmDialog>
    </section>
  )
}

/**
 * FR-159: "Add a connection" in the new-project wizard saved it in session storage and opened this page; this offers
 * the way back (the wizard restores its state). Session storage only exists in the browser (server snapshot: none).
 */
const noSubscription = () => () => undefined

function WizardReturnNotice() {
  const waiting = useSyncExternalStore(noSubscription, hasWizardDraft, () => false)
  if (!waiting) {
    return null
  }
  return (
    <Alert tone="info" title="You were creating a project">
      <p>Add the connection you need, then go back: what you entered in the new-project wizard is kept.</p>
      <p className="mt-2">
        <Link to="/projects/new" className="text-primary underline">
          Back to the new project
        </Link>
      </p>
    </Alert>
  )
}

/**
 * /connections (alias /account/connections; SPEC §7): four sections (FR-142): LLM providers (FR-78), Source control
 * (GitHub FR-81, GitLab FR-131, Bitbucket FR-134), Deployment platforms and Databases (pasted platform tokens; Vercel
 * from FR-87 migrated). `githubError` is a failed Connect GitHub coming back as `?error=GITHUB_AUTH_FAILED&reason=…`.
 */
export function ConnectionsPage({
  githubError,
  githubInstalled = false,
  gitlabError,
  gitlabConnected = false,
  bitbucketError,
  bitbucketConnected = false,
}: {
  githubError?: { reason?: string }
  /** `?github=installed`: back from installing the GitHub App (its setup URL). */
  githubInstalled?: boolean
  /** FR-131: a failed Connect GitLab (`?error=GIT_AUTH_FAILED&provider=gitlab`). */
  gitlabError?: { reason?: string }
  /** FR-131: `?gitlab=connected`, back from a successful Connect GitLab. */
  gitlabConnected?: boolean
  /** FR-134: a failed Connect Bitbucket (`?error=GIT_AUTH_FAILED&provider=bitbucket`). */
  bitbucketError?: { reason?: string }
  /** FR-134: `?bitbucket=connected`, back from a successful Connect Bitbucket. */
  bitbucketConnected?: boolean
}) {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Connections"
        description="LLM providers, source control, deployment platforms and databases, connected to your account."
      />
      <WizardReturnNotice />
      <LlmProvidersSection />
      <section aria-labelledby="source-control-title" className="flex flex-col gap-4">
        <div>
          <h2 id="source-control-title" className="text-lg">
            Source control
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">GitHub, GitLab and Bitbucket, for picking, creating and working on repositories.</p>
        </div>
        <GitHubConnectionSection callbackError={githubError} installed={githubInstalled} />
        <GitLabConnectionSection callbackError={gitlabError} connected={gitlabConnected} />
        <BitbucketConnectionSection callbackError={bitbucketError} connected={bitbucketConnected} />
      </section>
      <PlatformConnectionSection
        id="deployment-platforms"
        title="Deployment platforms"
        description="Your platform tokens. A project deploys each target with the connection its Owner binds in Settings → Deployment."
        providers={deploymentPlatformIds}
      />
      <PlatformConnectionSection
        id="database-platforms"
        title="Databases"
        description="Your managed database tokens. A project's database cards in Settings → Deployment use the connection its Owner binds."
        providers={databasePlatformIds}
      />
    </div>
  )
}
