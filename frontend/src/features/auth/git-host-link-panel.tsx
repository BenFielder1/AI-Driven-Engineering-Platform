import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { LoadingState } from '../../components/ui/states'
import { errorMessage, messageFor } from '../../lib/errors'
import { useCancelBitbucketLink, useConfirmBitbucketLink, useBitbucketPendingLink } from '../bitbucket/bitbucket-api'
import { BitbucketLogo } from '../bitbucket/bitbucket-logo'
import { useCancelGitLabLink, useConfirmGitLabLink, useGitLabPendingLink } from '../gitlab/gitlab-api'
import { GitLabLogo } from '../gitlab/gitlab-logo'
import type { User } from './auth-api'
import type { GitHostLinkProvider } from './auth-search'

/** A pending link as both hosts report it; GitLab also names the instance. */
interface PendingLink {
  login: string
  email: string
  instanceName?: string
}

/** Per host: its name, logo, error-copy context and the link hooks (each panel instance uses one host for its life). */
const hosts = {
  gitlab: {
    name: 'GitLab',
    Logo: GitLabLogo,
    context: 'gitlabLink',
    usePending: useGitLabPendingLink,
    useConfirm: useConfirmGitLabLink,
    useCancel: useCancelGitLabLink,
  },
  bitbucket: {
    name: 'Bitbucket',
    Logo: BitbucketLogo,
    context: 'bitbucketLink',
    usePending: useBitbucketPendingLink,
    useConfirm: useConfirmBitbucketLink,
    useCancel: useCancelBitbucketLink,
  },
} as const

/** The pending link of the host (login page: stay to confirm after signing in while one is waiting). */
export function useGitHostPendingLink(provider: GitHostLinkProvider, enabled: boolean) {
  const gitlab = useGitLabPendingLink(enabled && provider === 'gitlab')
  const bitbucket = useBitbucketPendingLink(enabled && provider === 'bitbucket')
  return provider === 'gitlab' ? gitlab : bitbucket
}

/**
 * FR-131, FR-134, D-V4-18 (`/login?link=gitlab|bitbucket`): the Git host's verified email belongs to an existing AIDE
 * account that isn't linked to this identity. AIDE never links silently: the user signs in to that account (the form
 * below), then confirms here, or cancels. `onDone` is called after confirming or cancelling, with whether the link was
 * made. Render it with `key={provider}`.
 */
export function GitHostLinkPanel({
  provider,
  user,
  onDone,
}: {
  provider: GitHostLinkProvider
  user: User | null
  onDone: (linked: boolean) => void
}) {
  const host = hosts[provider]
  const pending = host.usePending()
  const confirm = host.useConfirm()
  const cancel = host.useCancel()
  const [failure, setFailure] = useState<unknown>(null)
  const { name, Logo, context } = host

  if (pending.isPending) {
    return <LoadingState label={`Loading the ${name} sign-in to link…`} />
  }
  // No link (expired, cancelled elsewhere) or it couldn't load: always offer the way on.
  const continueButton = (
    <p className="mt-2">
      <Button
        variant="secondary"
        onClick={() => {
          onDone(false)
        }}
      >
        {user ? 'Continue' : 'Sign in without linking'}
      </Button>
    </p>
  )
  if (pending.isError) {
    return (
      <Alert tone="error" role="alert" title={`We couldn't load the ${name} sign-in`} className="mb-4">
        <p>{errorMessage(pending.error, context)}</p>
        {continueButton}
      </Alert>
    )
  }
  const link: PendingLink | null = pending.data
  if (!link) {
    return (
      <Alert tone="warning" role="status" title="Nothing to link" className="mb-4">
        <p>{messageFor('NOT_FOUND', context)}</p>
        {continueButton}
      </Alert>
    )
  }

  const runCancel = () => {
    setFailure(null)
    cancel.mutate(undefined, {
      onSuccess: () => {
        toast.info(`The ${name} account wasn’t linked.`)
        onDone(false)
      },
      onError: setFailure,
    })
  }

  const runConfirm = () => {
    setFailure(null)
    confirm.mutate(undefined, {
      onSuccess: () => {
        toast.success(`${name} account ${link.login} linked. You can now sign in with ${link.instanceName ?? name}.`)
        onDone(true)
      },
      onError: setFailure,
    })
  }

  const titleId = `${provider}-link-title`
  return (
    <section
      aria-labelledby={titleId}
      className="mb-4 flex flex-col gap-3 rounded-lg border border-l-4 border-primary/50 bg-primary/10 p-3 text-sm"
    >
      <h2 id={titleId} className="flex items-center gap-2 font-semibold">
        <Logo />
        {`Link your ${name} account`}
      </h2>
      <p>
        The {name} account <strong data-testid={`${provider}-link-login`}>{link.login}</strong>
        {link.instanceName ? ` on ${link.instanceName}` : ''} uses the email <strong>{link.email}</strong>, which belongs to an
        existing AIDE account.
      </p>
      {user ? (
        <p>
          You&apos;re signed in as <strong>{user.email}</strong>. Link the {name} account to it, so you can sign in with {name}{' '}
          from now on?
        </p>
      ) : (
        <p>
          To link them, sign in to that AIDE account below. AIDE never links accounts without your confirmation. This request
          expires in 10 minutes.
        </p>
      )}
      {failure !== null && (
        <Alert tone="error" role="alert" title={`The ${name} account wasn't linked`}>
          <p>{errorMessage(failure, context)}</p>
        </Alert>
      )}
      <div className="flex flex-wrap gap-2">
        {user && (
          <Button pending={confirm.isPending} pendingLabel="Linking…" onClick={runConfirm} disabled={cancel.isPending}>
            {`Link ${name} account`}
          </Button>
        )}
        <Button variant="secondary" pending={cancel.isPending} pendingLabel="Cancelling…" onClick={runCancel} disabled={confirm.isPending}>
          {user ? 'Don’t link' : 'Cancel linking'}
        </Button>
      </div>
    </section>
  )
}
