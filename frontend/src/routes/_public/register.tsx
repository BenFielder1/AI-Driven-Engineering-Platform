import { createFileRoute } from '@tanstack/react-router'

import { RegisterPage } from '../../features/auth/register-page'

export const Route = createFileRoute('/_public/register')({
  validateSearch: (search: Record<string, unknown>): { returnUrl?: string } => ({
    returnUrl: typeof search.returnUrl === 'string' && search.returnUrl ? search.returnUrl : undefined,
  }),
  component: RegisterRoute,
})

function RegisterRoute() {
  const { returnUrl } = Route.useSearch()
  return <RegisterPage returnUrl={returnUrl} />
}
