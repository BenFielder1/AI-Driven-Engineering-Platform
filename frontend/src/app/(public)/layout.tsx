'use client'

import { useQuery } from '@tanstack/react-query'
import { useEffect, useState, type ReactNode } from 'react'

import { LoadingState } from '../../components/ui/states'
import { meQueryOptions } from '../../features/auth/auth-api'
import { publicRedirectTarget } from '../../features/auth/auth-redirects'
import { PublicShell } from '../../features/shell/app-shell'
import { RouteErrorView } from '../../features/shell/route-states'
import { useLocation, useNavigate } from '../../lib/navigation'

/**
 * Layout for /login and /register (v2 `_public`). Signed-in users are sent on (to `returnUrl`, or /projects). Like the
 * v2 guard, the check runs when a URL is entered, not when signing in on the page (the page then navigates itself).
 */
export default function PublicLayout({ children }: { children: ReactNode }) {
  const me = useQuery(meQueryOptions)
  const location = useLocation()
  const navigate = useNavigate()
  const [decision, setDecision] = useState<{ href: string; target: string | null } | null>(null)
  if (me.isSuccess && decision?.href !== location.href) {
    setDecision({ href: location.href, target: me.data ? publicRedirectTarget(location.search) : null })
  }
  const target = decision?.href === location.href ? decision.target : null

  useEffect(() => {
    if (target !== null) {
      void navigate({ href: target, replace: true })
    }
  }, [target, navigate])

  if (me.isError) {
    return <RouteErrorView error={me.error} onRetry={() => void me.refetch()} />
  }
  if (decision?.href !== location.href || target !== null) {
    return <LoadingState />
  }
  return <PublicShell>{children}</PublicShell>
}
