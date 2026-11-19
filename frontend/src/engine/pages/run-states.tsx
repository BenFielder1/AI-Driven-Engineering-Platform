import type { ReactNode } from 'react'

import { EmptyState as PageEmptyState } from '../../components/shared/empty-state'
import { Panel } from '../../components/shared/panel'
import { ErrorState } from '../../components/ui/states'
import { isApiError } from '../../lib/errors'

/** A section card with an h3 (the project layout holds the h1, the engine page the h2). */
export function Section({ id, title, children, actions }: { id: string; title: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <Panel className="min-w-0 p-4" role="region" aria-labelledby={id}>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h3 id={id} className="text-base">
          {title}
        </h3>
        {actions}
      </div>
      {children}
    </Panel>
  )
}

/** A run (or a project's runs) that isn't there, or that the user can't see (404 NOT_FOUND hides which). */
export function RunNotFound({ action, headingLevel = 3 }: { action?: ReactNode; headingLevel?: 3 | 4 }) {
  return (
    <PageEmptyState
      variant="no_data"
      headingLevel={headingLevel}
      title="Run not found"
      description="It may have been removed, or you may not have access to its project."
      action={action}
    />
  )
}

/** The load error of a page: not found for 404, else the shared error state with a retry. */
export function LoadFailure({ error, onRetry, title, action }: { error: unknown; onRetry: () => void; title: string; action?: ReactNode }) {
  if (isApiError(error, 'NOT_FOUND')) {
    return <RunNotFound action={action} />
  }
  return <ErrorState error={error} onRetry={onRetry} title={title} />
}
