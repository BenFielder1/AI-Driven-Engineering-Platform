import { ExternalLink } from 'lucide-react'

import { Alert } from '../../components/ui/alert'
import { buttonClasses } from '../../components/ui/button-classes'
import { errorMessage, isApiError, type ErrorContext } from '../../lib/errors'
import { Link } from '../../lib/navigation'

/** A link that leaves AIDE for a GitLab instance (new tab, announced as such). */
export function GitLabLink({
  href,
  children,
  variant = 'link',
}: {
  href: string
  children: string
  variant?: 'secondary' | 'link'
}) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noreferrer noopener"
      className={variant === 'link' ? 'inline-flex items-center gap-1 text-primary underline underline-offset-2' : buttonClasses(variant)}
    >
      {children}
      <ExternalLink aria-hidden className="size-3.5" />
      <span className="sr-only"> (opens GitLab in a new tab)</span>
    </a>
  )
}

/** A failed GitLab picker call (FR-132) with the way out: GIT_NOT_CONNECTED links to Connections. */
export function GitLabErrorAlert({ error, title, context }: { error: unknown; title: string; context?: ErrorContext }) {
  return (
    <Alert tone="error" role="alert" title={title}>
      <p>{errorMessage(error, context)}</p>
      {isApiError(error, 'GIT_NOT_CONNECTED') && (
        <p className="mt-2">
          <Link to="/connections" className="text-primary underline underline-offset-2">
            Go to Connections
          </Link>
        </p>
      )}
    </Alert>
  )
}
