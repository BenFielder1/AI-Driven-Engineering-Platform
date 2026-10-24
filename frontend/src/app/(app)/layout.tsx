'use client'

import { useQuery } from '@tanstack/react-query'
import { useEffect, useState, type ReactNode } from 'react'

import { LoadingState } from '../../components/ui/states'
import { meQueryOptions, useCurrentUser, type User } from '../../features/auth/auth-api'
import { loginRedirectHref } from '../../features/auth/auth-redirects'
import { AppShell } from '../../features/shell/app-shell'
import { RouteErrorView } from '../../features/shell/route-states'
import { useLocation, useNavigate } from '../../lib/navigation'

/**
 * Authenticated layout (v2 `_app`, FR-97): signed-out users go to /login?returnUrl=<path+search>. The check runs
 * whenever a URL is entered, against the cached `me`. `me` refetches when invalidated (e.g. FR-3 self-demotion closes
 * the admin area), as in v2; the API stays the authority.
 */
export default function AppLayout({ children }: { children: ReactNode }) {
  const me = useQuery(meQueryOptions)
  const location = useLocation()
  const navigate = useNavigate()
  const [decision, setDecision] = useState<{ href: string; user: User | null } | null>(null)
  if (me.isSuccess && decision?.href !== location.href) {
    setDecision({ href: location.href, user: me.data })
  }
  const signedOut = decision?.href === location.href && decision.user === null
  const redirectTo = signedOut ? loginRedirectHref(location.href) : null

  useEffect(() => {
    if (redirectTo !== null) {
      void navigate({ href: redirectTo, replace: true })
    }
  }, [redirectTo, navigate])

  // Live value from the cache (e.g. after a profile change); falls back to the guard's value.
  const user = useCurrentUser() ?? decision?.user ?? null

  if (me.isError) {
    return <RouteErrorView error={me.error} onRetry={() => void me.refetch()} />
  }
  if (!user || signedOut) {
    return <LoadingState />
  }
  return <AppShell user={user}>{children}</AppShell>
}
