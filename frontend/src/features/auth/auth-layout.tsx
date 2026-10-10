import type { ReactNode } from 'react'

import { useDocumentTitle } from '../../lib/use-document-title'
import { useAuthProviders } from './auth-api'

/** Centred card used by /login and /register. */
export function AuthCard({ title, children }: { title: string; children: ReactNode }) {
  useDocumentTitle(title)
  return (
    <div className="mx-auto w-full max-w-md px-4 py-10 sm:py-16">
      <div className="rounded-lg border border-border bg-surface-raised p-6 shadow-sm">
        <h1 className="mb-6 text-2xl font-semibold tracking-tight">{title}</h1>
        {children}
      </div>
    </div>
  )
}

/** "Continue with GitHub" (FR-2): a full-page link to the API's OAuth start; hidden unless GitHub is configured. */
export function GitHubButton({ href }: { href: string }) {
  const providers = useAuthProviders()
  if (!providers.data?.github) {
    return null
  }
  return (
    <>
      <div className="my-5 flex items-center gap-3 text-sm text-muted" aria-hidden="true">
        <span className="h-px flex-1 bg-border" />
        or
        <span className="h-px flex-1 bg-border" />
      </div>
      <a
        href={href}
        className="inline-flex min-h-10 w-full items-center justify-center gap-2 rounded-md border border-field-border bg-surface-raised px-4 py-2 text-sm font-medium text-fg hover:bg-surface"
      >
        <svg aria-hidden="true" viewBox="0 0 16 16" className="h-4 w-4" fill="currentColor">
          <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z" />
        </svg>
        Continue with GitHub
      </a>
    </>
  )
}
