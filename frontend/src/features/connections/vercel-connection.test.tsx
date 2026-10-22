import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedDeploymentSettings, seedVercelConnection, vercelDb } from '../../mocks/handlers/deployments'
import { projectsDb, seedProject } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'

function signInAda() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

async function openConnections() {
  const utils = renderRoute('/connections')
  await screen.findByRole('heading', { level: 1, name: 'Connections' })
  return { ...utils, user: userEvent.setup() }
}

const vercelSection = () => screen.getByRole('region', { name: 'Vercel' })

describe('/connections → Vercel (FR-87)', () => {
  it('FR87_add_team_token: a valid team token is checked, saved, and listed with the team slug and Valid, never the token', async () => {
    signInAda()
    const { user, queryClient } = await openConnections()
    expect(await within(vercelSection()).findByText('Not connected')).toBeInTheDocument()

    await user.click(within(vercelSection()).getByRole('button', { name: 'Add token' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a Vercel token' })
    expect(within(dialog).getByText(/team- or account-scoped token, ideally with an expiry/)).toBeInTheDocument()
    const tokenInput = within(dialog).getByLabelText('Access token')
    expect(tokenInput).toHaveAttribute('type', 'password')
    await user.type(within(dialog).getByLabelText('Label'), 'Acme team')
    await user.type(tokenInput, 'team_secret9z8y')
    await user.click(within(dialog).getByRole('button', { name: 'Add token' }))

    const list = await within(vercelSection()).findByRole('list', { name: 'Your Vercel tokens' })
    const row = within(list).getByRole('listitem', { name: 'Vercel token Acme team' })
    expect(within(row).getByTestId('vercel-scope')).toHaveTextContent('Team acme · account ada')
    expect(within(row).getByTestId('vercel-scope')).toHaveTextContent(/Expires/)
    expect(within(row).getByTestId('credential-status')).toHaveTextContent('Valid')
    expect(within(row).getByTestId('vercel-last4')).toHaveTextContent('••••9z8y')
    expect(document.body).not.toHaveTextContent('team_secret9z8y')
    await waitFor(() => {
      expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain('team_secret9z8y')
    })
    expect(vercelDb.connections).toHaveLength(1)
  })

  it('FR87_token_rejected: a token Vercel rejects shows the copy, is cleared from the field and memory, and nothing is saved', async () => {
    signInAda()
    vercelDb.rejectedTokens.add('vcp_bad0')
    const { user, queryClient } = await openConnections()
    await user.click(await within(vercelSection()).findByRole('button', { name: 'Add token' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a Vercel token' })
    await user.type(within(dialog).getByLabelText('Label'), 'Old token')
    await user.type(within(dialog).getByLabelText('Access token'), 'vcp_bad0')
    await user.click(within(dialog).getByRole('button', { name: 'Add token' }))

    expect(await within(dialog).findByText(/Vercel rejected this token, so it wasn't saved/, { selector: 'p, span' })).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Access token')).toHaveValue('')
    await waitFor(() => {
      expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain('vcp_bad0')
    })
    expect(vercelDb.connections).toHaveLength(0)
  })

  it('FR87_token_required: an empty token is caught before anything is sent', async () => {
    signInAda()
    const { user } = await openConnections()
    await user.click(await within(vercelSection()).findByRole('button', { name: 'Add token' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a Vercel token' })
    await user.type(within(dialog).getByLabelText('Label'), 'Mine')
    await user.click(within(dialog).getByRole('button', { name: 'Add token' }))
    expect((await within(dialog).findAllByText('Paste the Vercel access token.')).length).toBeGreaterThan(0)
    expect(projectsDb.requests.filter((r) => r.path.endsWith('/connections/vercel'))).toHaveLength(0)
  })

  it('FR87_test_marks_invalid: Test re-checks the token and shows Invalid when Vercel rejects it', async () => {
    const me = signInAda()
    const c = seedVercelConnection({ userId: me.id, label: 'Personal', token: 'vcp_xxxx1234' })
    vercelDb.rejectedTokens.add(c.token)
    const { user } = await openConnections()
    const row = await within(vercelSection()).findByRole('listitem', { name: 'Vercel token Personal' })
    expect(within(row).getByTestId('vercel-scope')).toHaveTextContent('Personal account (ada)')
    await user.click(within(row).getByRole('button', { name: 'Test Personal' }))
    expect(await screen.findByText('"Personal" is invalid: Vercel rejected it.')).toBeInTheDocument()
    await waitFor(() => {
      expect(within(row).getByTestId('credential-status')).toHaveTextContent('Invalid')
    })
  })

  it('FR87_delete_warns_bound_projects: deleting a bound token names the projects that lose their binding', async () => {
    const me = signInAda()
    const c = seedVercelConnection({ userId: me.id, label: 'Acme team', token: 'vcp_aaaa5678' })
    const project = seedProject({ name: 'Storefront' })
    seedDeploymentSettings(project, { connection: c })
    const { user } = await openConnections()
    const row = await within(vercelSection()).findByRole('listitem', { name: 'Vercel token Acme team' })
    expect(within(row).getByRole('link', { name: 'Storefront' })).toHaveAttribute('href', `/projects/${project.id}/settings/deployment`)

    await user.click(within(row).getByRole('button', { name: 'Delete Acme team' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete "Acme team"?' })
    expect(within(confirm).getByTestId('vercel-delete-bound-warning')).toHaveTextContent('Storefront')
    await user.click(within(confirm).getByRole('button', { name: 'Delete token' }))

    expect(await within(vercelSection()).findByText('Not connected')).toBeInTheDocument()
    expect(project.deployment?.binding).toBeNull()
  })

  it('FR87_activity: a Vercel CONNECTION_INVALID event reads as Vercel rejecting the token', () => {
    expect(
      describeActivity({ type: 'CONNECTION_INVALID', payload: { provider: 'vercel', connectionId: 'c1' }, summary: 'fallback' }),
    ).toBe('Vercel connection marked invalid: Vercel rejected its token')
  })
})
