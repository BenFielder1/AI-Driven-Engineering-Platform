import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { authDb, seedToken, signInAs } from '../../mocks/handlers/auth'
import { renderRoute } from '../../test/render'

const PLAIN = 'aide_pat_' + 'x'.repeat(43)

describe('AccountPage', () => {
  it('FR-3: shows the profile with the system role', async () => {
    signInAs({ name: 'Ada Lovelace', email: 'ada@example.com', systemRole: 'admin' })
    renderRoute('/account')

    const profile = await screen.findByRole('region', { name: 'Profile' })
    expect(within(profile).getByText('Ada Lovelace')).toBeInTheDocument()
    expect(within(profile).getByText('ada@example.com')).toBeInTheDocument()
    expect(within(profile).getByText('Admin')).toBeInTheDocument()
  })

  it('FR-2: offers "Link GitHub" only when GitHub is enabled and not yet linked', async () => {
    authDb.githubMode = 'oauth'
    signInAs({ githubLinked: false })
    renderRoute('/account')

    const link = await screen.findByRole('link', { name: 'Link GitHub' })
    expect(link).toHaveAttribute('href', '/api/auth/github/link?returnUrl=%2Faccount')
  })

  it('FR-2: shows the linked state instead of the link button', async () => {
    authDb.githubMode = 'oauth'
    signInAs({ githubLinked: true })
    renderRoute('/account')

    expect(await screen.findByText(/Your GitHub account is linked/)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Link GitHub' })).not.toBeInTheDocument()
  })

  it('FR-2: hides "Link GitHub" when GitHub is not configured', async () => {
    authDb.githubMode = 'none'
    signInAs()
    renderRoute('/account')

    expect(await screen.findByText(/GitHub sign-in isn't configured/)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Link GitHub' })).not.toBeInTheDocument()
  })

  it('FR-4: shows an empty state when there are no tokens', async () => {
    signInAs()
    renderRoute('/account')

    expect(await screen.findByText('No tokens yet')).toBeInTheDocument()
  })

  it('FR-4: creating a token shows the plain token once, with a copy button', async () => {
    signInAs()
    renderRoute('/account')
    const user = userEvent.setup()
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })

    await user.type(await screen.findByLabelText('Name'), 'Laptop CLI')
    await user.click(screen.getByRole('button', { name: 'Create token' }))

    expect(await screen.findByLabelText('Your new token')).toHaveValue(PLAIN)
    expect(screen.getByText("Copy it now. You won't be able to see it again.")).toBeInTheDocument()
    const list = await screen.findByRole('list', { name: 'API tokens' })
    expect(within(list).getByText('Laptop CLI')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Copy token' }))
    expect(writeText).toHaveBeenCalledWith(PLAIN)
    expect(await screen.findByText('Copied to the clipboard.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Done' }))
    expect(screen.queryByLabelText('Your new token')).not.toBeInTheDocument()
    expect(screen.queryByText(PLAIN)).not.toBeInTheDocument()
  })

  it('FR-4: requires a token name', async () => {
    signInAs()
    renderRoute('/account')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Create token' }))

    expect(await screen.findAllByText('Give the token a name, such as "Laptop CLI".')).toHaveLength(2)
    expect(authDb.tokens).toHaveLength(0)
  })

  it('FR-4: revoking asks for confirmation; Cancel keeps the token', async () => {
    signInAs()
    seedToken({ name: 'Old laptop' })
    renderRoute('/account')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Revoke Old laptop' }))
    const dialog = screen.getByRole('alertdialog', { name: 'Revoke "Old laptop"?' })
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus()

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(authDb.tokens[0]?.revokedAt).toBeNull()
  })

  it('FR-4: confirming revokes the token and the list shows it as revoked', async () => {
    signInAs()
    seedToken({ name: 'Old laptop' })
    renderRoute('/account')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Revoke Old laptop' }))
    await user.click(screen.getByRole('button', { name: 'Revoke token' }))

    await waitFor(() => {
      expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    })
    expect(authDb.tokens[0]?.revokedAt).not.toBeNull()
    expect(await screen.findByText('Revoked')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Revoke Old laptop' })).not.toBeInTheDocument()
  })
})
