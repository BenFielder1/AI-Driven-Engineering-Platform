import { Alert } from '../../components/ui/alert'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessages, isApiError } from '../../lib/errors'
import { safeUrl } from './deployment-format'

/** Vercel's GitHub App install page, when neither the error nor the settings carry Vercel's own link. */
export const VERCEL_GITHUB_INSTALL_FALLBACK = 'https://github.com/apps/vercel/installations/new'

/**
 * FR-88: Vercel's GitHub App can't access the repository (409 VERCEL_GIT_NOT_CONNECTED, or `gitConnected: false` on
 * the settings). Links to Vercel's install page: the ProblemDetails `installUrl`, else the settings' `installUrl`.
 */
export function VercelGitAlert({
  error,
  installUrl,
  title = "Vercel can't access the repository",
}: {
  error?: unknown
  installUrl?: string | null
  title?: string
}) {
  const fromError = isApiError(error) ? error.installUrl : null
  const href = safeUrl(fromError) ?? safeUrl(installUrl) ?? VERCEL_GITHUB_INSTALL_FALLBACK
  return (
    <Alert tone="error" role="alert" title={title}>
      <p>{errorMessages.VERCEL_GIT_NOT_CONNECTED}</p>
      <p className="mt-2">
        <a href={href} target="_blank" rel="noreferrer noopener" className={buttonClasses('secondary')}>
          Install Vercel&apos;s GitHub App
          <span className="sr-only"> (opens in a new tab)</span>
        </a>
      </p>
    </Alert>
  )
}
