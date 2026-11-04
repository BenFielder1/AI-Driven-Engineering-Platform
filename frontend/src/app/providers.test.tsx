import { useQueryClient } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { useEffect } from 'react'
import { toast } from 'sonner'
import { describe, expect, it, vi } from 'vitest'

import { authKeys } from '../features/auth/auth-api'
import { toastAcrossNavigation } from '../lib/toast-handoff'
import type * as ProvidersModule from './providers'

// src/test/setup.ts mocks this module as a pass-through for the layout tests; these tests need the real providers.
const { Providers } = await vi.importActual<typeof ProvidersModule>('./providers')

const signedIn = { id: 'u-1', name: 'Ada', email: 'ada@example.com' }

function WriteMe() {
  const client = useQueryClient()
  useEffect(() => {
    client.setQueryData(authKeys.me(), signedIn)
  }, [client])
  return null
}

function ReadMe() {
  const me = useQueryClient().getQueryData<typeof signedIn>(authKeys.me())
  return <p>{me ? `cached ${me.name}` : 'no user'}</p>
}

describe('Providers across the (public) and (app) groups (FR-131, FR-134, FR-210)', () => {
  it('shows a toast raised just before switching groups in the next Providers mount, once', async () => {
    const message = 'GitHub account octo linked. You can now sign in with GitHub.'
    const first = render(<Providers>public page</Providers>)
    toastAcrossNavigation('success', message)
    // The (public) layout unmounts before its Toaster has rendered the toast; the (app) layout mounts its own.
    first.unmount()

    render(<Providers>app page</Providers>)
    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(screen.getAllByText(message)).toHaveLength(1)
    toast.dismiss()
  })

  it('shares one QueryClient, so the me cache survives remounting Providers', () => {
    const first = render(
      <Providers>
        <WriteMe />
      </Providers>,
    )
    first.unmount()

    render(
      <Providers>
        <ReadMe />
      </Providers>,
    )
    expect(screen.getByText('cached Ada')).toBeInTheDocument()
  })
})
