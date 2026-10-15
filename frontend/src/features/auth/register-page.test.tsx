import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { authDb, seedUser } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { authKeys } from './auth-api'

async function fillAndSubmit(values: { email: string; name: string; password: string }) {
  const user = userEvent.setup()
  await user.type(await screen.findByLabelText('Email'), values.email)
  await user.type(screen.getByLabelText('Name'), values.name)
  await user.type(screen.getByLabelText(/^Password/), values.password)
  await user.click(screen.getByRole('button', { name: 'Create account' }))
}

describe('RegisterPage', () => {
  it('FR-1: registering signs the user in and shows them in the shell', async () => {
    const { queryClient, router } = renderRoute('/register')

    await fillAndSubmit({ email: 'a@x.com', name: 'Ada', password: 'password1' })

    expect(await screen.findByRole('heading', { level: 1, name: 'Projects' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/projects')
    expect(queryClient.getQueryData(authKeys.me())).toMatchObject({ email: 'a@x.com', name: 'Ada' })
    expect(authDb.users).toHaveLength(1)
  })

  it('FR-3: the first registered user is an Admin and sees the Admin nav', async () => {
    renderRoute('/register')

    await fillAndSubmit({ email: 'first@x.com', name: 'First', password: 'password1' })

    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(nav).getByRole('list', { name: 'Admin' })).toBeInTheDocument()
  })

  it('FR-1: EMAIL_TAKEN marks the email field and keeps the user on the page', async () => {
    seedUser({ email: 'a@x.com' })
    renderRoute('/register')

    await fillAndSubmit({ email: 'a@x.com', name: 'Ada', password: 'password1' })

    const copy = 'An account with this email already exists. Sign in instead.'
    expect(await screen.findAllByText(copy)).toHaveLength(2) // field + summary
    expect(screen.getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('heading', { level: 1, name: 'Create your AIDE account' })).toBeInTheDocument()
  })

  it('FR-1: enforces the password policy (8+ characters, a letter and a digit) client-side', async () => {
    renderRoute('/register')

    await fillAndSubmit({ email: 'a@x.com', name: 'Ada', password: 'abcdefgh' })

    expect(await screen.findAllByText('Password must contain at least one digit.')).toHaveLength(2)
    expect(authDb.users).toHaveLength(0)
  })

  it('FR-1: maps server VALIDATION errors onto the matching fields', async () => {
    server.use(
      http.post('*/api/auth/register', () =>
        problem(400, 'VALIDATION', 'One or more fields are invalid.', {
          errors: { password: ['Passwords must have at least one non alphanumeric character.'] },
        }),
      ),
    )
    renderRoute('/register')

    await fillAndSubmit({ email: 'a@x.com', name: 'Ada', password: 'password1' })

    expect(await screen.findAllByText('Passwords must have at least one non alphanumeric character.')).toHaveLength(2)
    expect(screen.getByLabelText(/^Password/)).toHaveAttribute('aria-invalid', 'true')
  })
})
