import { Alert } from '../../components/ui/alert'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessages, isApiError, platformNames, withPlatform } from '../../lib/errors'
import { safeUrl } from './deployment-format'

/** Vercel's GitHub App install page, when neither the error nor the caller carries Vercel's own link. */
export const VERCEL_GITHUB_INSTALL_FALLBACK = 'https://github.com/apps/vercel/installations/new'

/**
 * FR-144 (D-V4-4: was VERCEL_GIT_NOT_CONNECTED): the platform's Git integration can't see the repository. Shows the
 * copy (or `message`) and links to the platform's install or connect page: the ProblemDetails `installUrl`, else
 * `installUrl` (a setup job's `fixUrl`), else Vercel's App page for Vercel.
 */
export function PlatformGitAlert({
  error,
  platform,
  installUrl,
  message,
  title,
}: {
  error?: unknown
  platform: string
  installUrl?: string | null
  message?: string
  title?: string
}) {
  const name = platformNames[platform] ?? 'the platform'
  const fromError = isApiError(error) ? error.installUrl : null
  const href = safeUrl(fromError) ?? safeUrl(installUrl) ?? (platform === 'vercel' ? VERCEL_GITHUB_INSTALL_FALLBACK : null)
  return (
    <Alert tone="error" role="alert" title={title ?? withPlatform("{platform} can't see the repository", platform)}>
      <p>{message ?? withPlatform(errorMessages.PLATFORM_GIT_NOT_CONNECTED, platform)}</p>
      {href && (
        <p className="mt-2">
          <a href={href} target="_blank" rel="noreferrer noopener" className={buttonClasses('secondary')}>
            Connect the repository in {name}
            <span className="sr-only"> (opens in a new tab)</span>
          </a>
        </p>
      )}
    </Alert>
  )
}
