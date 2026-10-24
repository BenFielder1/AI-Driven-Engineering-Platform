'use client'

import '../lib/zod-config'

import { QueryClientProvider } from '@tanstack/react-query'
import { useEffect, useState, type ReactNode } from 'react'

import { refreshAntiforgeryToken } from '../api/client'
import { Toaster } from '../components/ui/sonner'
import { authKeys } from '../features/auth/auth-api'
import { NavigationEvents, useNavigate } from '../lib/navigation'
import { createQueryClient } from '../lib/query-client'
import { initTheme } from '../lib/theme'

let started = false
const SESSION_ENDED_EVENT = 'aide:session-ended'

/**
 * Client providers for every page (SPEC §7): TanStack Query (with the v2 global 401 handling), the theme, the toast
 * region and navigation settling. Live updates use LiveConnectionContext's default (a real SignalR connection).
 */
export function Providers({ children }: { children: ReactNode }) {
  const navigate = useNavigate()
  const [queryClient] = useState(() => {
    const client = createQueryClient({
      onUnauthenticated: () => {
        client.setQueryData(authKeys.me(), null)
        window.dispatchEvent(new Event(SESSION_ENDED_EVENT))
      },
    })
    return client
  })

  // Any 401 UNAUTHENTICATED (session ended): sign the UI out and go to /login?returnUrl=<here> (v2 main.tsx).
  useEffect(() => {
    function onSessionEnded() {
      const { pathname, search } = window.location
      if (pathname !== '/login' && pathname !== '/register') {
        void navigate({ to: '/login', search: { returnUrl: `${pathname}${search}` } })
      }
    }
    window.addEventListener(SESSION_ENDED_EVENT, onSessionEnded)
    return () => {
      window.removeEventListener(SESSION_ENDED_EVENT, onSessionEnded)
    }
  }, [navigate])

  useEffect(() => {
    if (started) {
      return
    }
    started = true
    initTheme()
    // NFR-SEC: obtain the XSRF-TOKEN cookie up front (the client also fetches it lazily before a mutation).
    void refreshAntiforgeryToken()
  }, [])

  return (
    <QueryClientProvider client={queryClient}>
      <NavigationEvents />
      {children}
      <Toaster />
    </QueryClientProvider>
  )
}
