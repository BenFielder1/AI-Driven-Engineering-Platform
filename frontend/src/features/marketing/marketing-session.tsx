'use client'

import { createContext, use, useEffect, useState, type ReactNode } from 'react'

import { api } from '../../api/client'

const SignedInContext = createContext(false)

/**
 * FR-210, FR-211: whether the visitor is signed in, for the marketing header buttons and the `/` redirect. It never
 * blocks rendering: `false` (visitor) until GET /api/auth/me answers, and on the server. Any failure (401, network,
 * 5xx) counts as signed out, which only changes which buttons show.
 *
 * Deliberately not TanStack Query: marketing routes don't load the app's query layer, error copy table or toasts, which
 * keeps `/` inside the first-load JS budget (SPEC §8). The check runs each time the marketing shell mounts, so coming
 * back from the app after signing out shows the visitor buttons.
 */
export function MarketingSessionProvider({ children }: { children: ReactNode }) {
  const [signedIn, setSignedIn] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    api.GET('/api/auth/me', { signal: controller.signal }).then(
      ({ data }) => {
        setSignedIn(data != null)
      },
      () => {
        // Aborted on unmount, or the network failed: stay a visitor.
      },
    )
    return () => {
      controller.abort()
    }
  }, [])
  return <SignedInContext value={signedIn}>{children}</SignedInContext>
}

/** True once the session check found a signed-in user (inside MarketingSessionProvider). */
export function useMarketingSignedIn(): boolean {
  return use(SignedInContext)
}
