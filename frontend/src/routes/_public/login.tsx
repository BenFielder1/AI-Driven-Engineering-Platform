import { createFileRoute } from '@tanstack/react-router'

import { LoginPage } from '../../features/auth/login-page'

export interface LoginSearch {
  returnUrl?: string
  error?: string
  reason?: string
}

const str = (v: unknown) => (typeof v === 'string' && v ? v : undefined)

export const Route = createFileRoute('/_public/login')({
  validateSearch: (search: Record<string, unknown>): LoginSearch => ({
    returnUrl: str(search.returnUrl),
    error: str(search.error),
    reason: str(search.reason),
  }),
  component: LoginRoute,
})

function LoginRoute() {
  const { returnUrl, error, reason } = Route.useSearch()
  return (
    <LoginPage
      returnUrl={returnUrl}
      githubError={error === 'GITHUB_AUTH_FAILED' ? { reason } : undefined}
    />
  )
}
