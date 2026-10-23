import { Link } from '../../lib/navigation'
import { ExternalLink } from 'lucide-react'

import { Alert } from '../../components/ui/alert'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessage, isApiError, type ErrorContext } from '../../lib/errors'
import { connectGitHubHref } from './github-api'

/** A link that leaves AIDE for GitHub (new tab, announced as such). */
export function GitHubLink({
  href,
  children,
  variant = 'secondary',
  testId,
}: {
  href: string
  children: string
  variant?: 'secondary' | 'ghost' | 'link'
  testId?: string
}) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noreferrer noopener"
      data-testid={testId}
      className={variant === 'link' ? 'inline-flex items-center gap-1 text-primary underline underline-offset-2' : buttonClasses(variant)}
    >
      {children}
      <ExternalLink aria-hidden className="size-3.5" />
      <span className="sr-only"> (opens GitHub in a new tab)</span>
    </a>
  )
}

/**
 * A failed GitHub call (FR-81, FR-83, FR-86) with the way out: GITHUB_APP_NOT_INSTALLED links to the App's install
 * page (ProblemDetails `installUrl`), GITHUB_NOT_CONNECTED to Connect GitHub.
 */
export function GitHubErrorAlert({
  error,
  title,
  context,
  installTarget,
  returnUrl,
  connectionsLink = false,
}: {
  error: unknown
  title: string
  context?: ErrorContext
  /** Who to install AIDE on ("acme"), for the link text. */
  installTarget?: string
  /** Where Connect GitHub should come back to. */
  returnUrl?: string
  /** GITHUB_NOT_CONNECTED links to the Connections page instead of starting Connect GitHub here. */
  connectionsLink?: boolean
}) {
  return (
    <Alert tone="error" role="alert" title={title}>
      <p>{errorMessage(error, context)}</p>
      {isApiError(error, 'GITHUB_APP_NOT_INSTALLED') && error.installUrl && (
        <p className="mt-2">
          <GitHubLink href={error.installUrl} variant="link">
            {`Install AIDE on ${installTarget ?? 'GitHub'}`}
          </GitHubLink>
        </p>
      )}
      {isApiError(error, 'GITHUB_NOT_CONNECTED') && connectionsLink && (
        <p className="mt-2">
          <Link to="/connections" className="text-primary underline underline-offset-2">
            Go to Connections
          </Link>
        </p>
      )}
      {isApiError(error, 'GITHUB_NOT_CONNECTED') && !connectionsLink && (
        <p className="mt-2">
          <a href={connectGitHubHref(returnUrl ?? '/connections')} className="text-primary underline underline-offset-2">
            Connect GitHub
          </a>
        </p>
      )}
    </Alert>
  )
}
