import { Inbox, Loader2 } from 'lucide-react'
import type { ReactNode } from 'react'

import { EmptyState as SharedEmptyState } from '../shared/empty-state'
import { PageHeader as SharedPageHeader } from '../shared/page-header'
import { errorMessage, type ErrorContext } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { cn } from '../../lib/utils'
import { Alert } from './alert'
import { Button } from './button'

/** An inline loading line (for sections; whole layouts use the Skeleton* placeholders). */
export function LoadingState({ label = 'Loading…', className }: { label?: string; className?: string }) {
  return (
    <p role="status" aria-live="polite" className={cn('flex items-center gap-2 py-6 text-sm text-muted-foreground', className)}>
      <Loader2 aria-hidden className="size-4 animate-spin" />
      {label}
    </p>
  )
}

export function ErrorState({ error, onRetry, title = "Something went wrong", context }: {
  error: unknown
  onRetry?: () => void
  title?: string
  /** Selects context-specific copy from `contextualErrorMessages`. */
  context?: ErrorContext
}) {
  return (
    <Alert tone="error" role="alert" title={title}>
      <p>{errorMessage(error, context)}</p>
      {onRetry && (
        <Button variant="secondary" className="mt-2" onClick={onRetry}>
          Try again
        </Button>
      )}
    </Alert>
  )
}

/** A compact "nothing here yet" block for sections inside a panel (pages use the shared EmptyState). */
export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div data-testid="empty-state-inline" className="flex flex-col items-center gap-2 rounded-lg border border-dashed border-border px-6 py-8 text-center">
      <span className="flex size-10 items-center justify-center rounded-full bg-muted text-muted-foreground">
        <Inbox className="size-5" aria-hidden />
      </span>
      <p className="font-medium">{title}</p>
      {children && <div className="text-sm text-muted-foreground">{children}</div>}
    </div>
  )
}

/** A whole page the user can't see (403): the forbidden EmptyState with the page's h1. */
export function ForbiddenState({ children }: { children?: ReactNode }) {
  useDocumentTitle('No access')
  return (
    <SharedEmptyState
      variant="forbidden"
      headingLevel={1}
      title="You don't have access to this page"
      description={children ?? 'Ask an administrator if you think you should.'}
      className="my-6"
    />
  )
}

/** Page title (h1) with actions on the right: the shared PageHeader. */
export function PageHeader({ title, description, children }: { title: string; description?: ReactNode; children?: ReactNode }) {
  return <SharedPageHeader title={title} description={description} actions={children} className="mb-6" />
}
