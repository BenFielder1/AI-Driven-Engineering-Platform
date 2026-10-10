import type { ReactNode } from 'react'

import { errorMessage, type ErrorContext } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { Alert } from './alert'
import { Button } from './button'

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <p role="status" aria-live="polite" className="py-6 text-sm text-muted">
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

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed border-field-border p-6 text-center">
      <p className="font-medium">{title}</p>
      {children && <div className="mt-1 text-sm text-muted">{children}</div>}
    </div>
  )
}

export function ForbiddenState({ children }: { children?: ReactNode }) {
  useDocumentTitle('No access')
  return (
    <section aria-labelledby="forbidden-title" className="py-6">
      <h1 id="forbidden-title" className="text-2xl font-semibold tracking-tight">
        You don&apos;t have access to this page
      </h1>
      <p className="mt-2 text-muted">{children ?? 'Ask an administrator if you think you should.'}</p>
    </section>
  )
}

export function PageHeader({ title, children }: { title: string; children?: ReactNode }) {
  useDocumentTitle(title)
  return (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-3">
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      {children}
    </header>
  )
}
