import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { LoadingState } from '../../components/ui/states'
import { errorMessage, messageFor } from '../../lib/errors'
import { useConfirmGitLabLink, useCancelGitLabLink, useGitLabPendingLink } from '../gitlab/gitlab-api'
import { GitLabLogo } from '../gitlab/gitlab-logo'
import type { User } from './auth-api'

/**
 * FR-131, D-V4-18 (`/login?link=gitlab`): GitLab's verified email belongs to an existing AIDE account that isn't linked
 * to this GitLab identity. AIDE never links silently: the user signs in to that account (the form below), then confirms
 * here, or cancels. `onDone` is called after confirming or cancelling, with whether the link was made.
 */
export function GitLabLinkPanel({ user, onDone }: { user: User | null; onDone: (linked: boolean) => void }) {
  const pending = useGitLabPendingLink()
  const confirm = useConfirmGitLabLink()
  const cancel = useCancelGitLabLink()
  const [failure, setFailure] = useState<unknown>(null)

  if (pending.isPending) {
    return <LoadingState label="Loading the GitLab sign-in to link…" />
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
      <Alert tone="error" role="alert" title="We couldn't load the GitLab sign-in" className="mb-4">
        <p>{errorMessage(pending.error, 'gitlabLink')}</p>
        {continueButton}
      </Alert>
    )
  }
  const link = pending.data
  if (!link) {
    return (
      <Alert tone="warning" role="status" title="Nothing to link" className="mb-4">
        <p>{messageFor('NOT_FOUND', 'gitlabLink')}</p>
        {continueButton}
      </Alert>
    )
  }

  const runCancel = () => {
    setFailure(null)
    cancel.mutate(undefined, {
      onSuccess: () => {
        toast.info('The GitLab account wasn’t linked.')
        onDone(false)
      },
      onError: setFailure,
    })
  }

  const runConfirm = () => {
    setFailure(null)
    confirm.mutate(undefined, {
      onSuccess: () => {
        toast.success(`GitLab account ${link.login} linked. You can now sign in with ${link.instanceName}.`)
        onDone(true)
      },
      onError: setFailure,
    })
  }

  return (
    <section
      aria-labelledby="gitlab-link-title"
      className="mb-4 flex flex-col gap-3 rounded-lg border border-l-4 border-primary/50 bg-primary/10 p-3 text-sm"
    >
      <h2 id="gitlab-link-title" className="flex items-center gap-2 font-semibold">
        <GitLabLogo />
        Link your GitLab account
      </h2>
      <p>
        The GitLab account <strong data-testid="gitlab-link-login">{link.login}</strong> on {link.instanceName} uses the email{' '}
        <strong>{link.email}</strong>, which belongs to an existing AIDE account.
      </p>
      {user ? (
        <p>
          You&apos;re signed in as <strong>{user.email}</strong>. Link the GitLab account to it, so you can sign in with GitLab
          from now on?
        </p>
      ) : (
        <p>
          To link them, sign in to that AIDE account below. AIDE never links accounts without your confirmation. This request
          expires in 10 minutes.
        </p>
      )}
      {failure !== null && (
        <Alert tone="error" role="alert" title="The GitLab account wasn't linked">
          <p>{errorMessage(failure, 'gitlabLink')}</p>
        </Alert>
      )}
      <div className="flex flex-wrap gap-2">
        {user && (
          <Button pending={confirm.isPending} pendingLabel="Linking…" onClick={runConfirm} disabled={cancel.isPending}>
            Link GitLab account
          </Button>
        )}
        <Button variant="secondary" pending={cancel.isPending} pendingLabel="Cancelling…" onClick={runCancel} disabled={confirm.isPending}>
          {user ? 'Don’t link' : 'Cancel linking'}
        </Button>
      </div>
    </section>
  )
}
