'use client'

import '../lib/zod-config'

import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
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
 * The browser keeps one QueryClient for the session, shared by the `(app)` and `(public)` layouts, so the cache (e.g.
 * `me` after signing in) survives moving between the two groups. Each server render gets its own.
 */
let browserQueryClient: QueryClient | undefined

function makeQueryClient(): QueryClient {
  const client = createQueryClient({
    onUnauthenticated: () => {
      client.setQueryData(authKeys.me(), null)
      window.dispatchEvent(new Event(SESSION_ENDED_EVENT))
    },
  })
  return client
}

function getQueryClient(): QueryClient {
  if (typeof window === 'undefined') {
    return makeQueryClient()
  }
  browserQueryClient ??= makeQueryClient()
  return browserQueryClient
}

/**
 * Client providers for the app and auth pages (SPEC §7): TanStack Query (with the 0.2.0 global 401 handling), the theme,
 * the toast region and navigation settling. Live updates use LiveConnectionContext's default (a real SignalR
 * connection). The `(app)` and `(public)` layouts render it; the root layout doesn't, so marketing routes don't load
 * any of this (SPEC §8 first-load JS budget for `/`). Tests supply their own (src/test/render.tsx).
 */
export function Providers({ children }: { children: ReactNode }) {
  const navigate = useNavigate()
  const [queryClient] = useState(getQueryClient)

  // Any 401 UNAUTHENTICATED (session ended): sign the UI out and go to /login?returnUrl=<here> (0.2.0 main.tsx).
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
