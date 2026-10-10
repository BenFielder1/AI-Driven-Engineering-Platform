import { Alert } from '../../components/ui/alert'
import { buttonClasses } from '../../components/ui/button-classes'
import { PageHeader } from '../../components/ui/states'
import { githubFailureMessage } from '../../lib/errors'
import { githubLinkHref, useAuthProviders, useCurrentUser } from '../auth/auth-api'
import { NotificationPreferencesSection } from '../notifications/notification-preferences-section'
import { ApiTokensSection } from './api-tokens-section'

const roleLabel = { admin: 'Admin', user: 'User' } as const

/** /account: profile and system role (FR-3), linked GitHub (FR-2), notification preferences (§2, FR-47), API tokens (FR-4). */
export function AccountPage({ githubError }: { githubError?: { reason?: string } }) {
  const user = useCurrentUser()
  const providers = useAuthProviders()

  if (!user) {
    return null
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title="Account" />

      {githubError && (
        <Alert tone="error" role="alert" title="GitHub linking failed">
          <p>{githubFailureMessage(githubError.reason)}</p>
        </Alert>
      )}

      <section aria-labelledby="profile-title" className="rounded-lg border border-border bg-surface-raised p-5">
        <h2 id="profile-title" className="mb-4 text-lg font-semibold">
          Profile
        </h2>
        <div className="flex items-start gap-4">
          {user.avatarUrl && <img src={user.avatarUrl} alt="" className="h-14 w-14 rounded-full" />}
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
            <dt className="text-muted">Name</dt>
            <dd className="break-words">{user.name}</dd>
            <dt className="text-muted">Email</dt>
            <dd className="break-all">{user.email}</dd>
            <dt className="text-muted">System role</dt>
            <dd>{roleLabel[user.systemRole]}</dd>
          </dl>
        </div>
      </section>

      <section aria-labelledby="github-title" className="rounded-lg border border-border bg-surface-raised p-5">
        <h2 id="github-title" className="mb-2 text-lg font-semibold">
          GitHub
        </h2>
        {user.githubLinked ? (
          <p className="text-sm">Your GitHub account is linked. You can sign in with GitHub.</p>
        ) : providers.data?.github ? (
          <>
            <p className="mb-3 text-sm text-muted">Link GitHub to sign in with it as this account.</p>
            <a href={githubLinkHref('/account')} className={buttonClasses('secondary')}>
              Link GitHub
            </a>
          </>
        ) : (
          <p className="text-sm text-muted">GitHub sign-in isn&apos;t configured on this server.</p>
        )}
      </section>

      <NotificationPreferencesSection />

      <ApiTokensSection />
    </div>
  )
}
