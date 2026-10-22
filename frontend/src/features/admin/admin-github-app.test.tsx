import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { configureGitHubApp, githubDb, INSTALL_URL } from '../../mocks/handlers/github'
import { renderRoute } from '../../test/render'

function signInAdmin() {
  return signInAs({ email: 'admin@example.com', name: 'Ada Admin', systemRole: 'admin' })
}

async function openPanel(path = '/admin/integrations') {
  renderRoute(path)
  const panel = await screen.findByRole('region', { name: 'GitHub App' })
  return { panel, user: userEvent.setup() }
}

/** Captures native form submissions (GitHub's manifest flow is a real POST that leaves AIDE). */
function captureSubmit() {
  const submitted: HTMLFormElement[] = []
  const listener = (e: SubmitEvent) => {
    if (e.target instanceof HTMLFormElement && e.target.method === 'post' && !e.defaultPrevented) {
      submitted.push(e.target)
      e.preventDefault()
    }
  }
  document.addEventListener('submit', listener)
  cleanups.push(() => {
    document.removeEventListener('submit', listener)
  })
  return submitted
}

const cleanups: (() => void)[] = []
afterEach(() => {
  cleanups.splice(0).forEach((c) => {
    c()
  })
})

describe('Admin → Integrations → GitHub App (FR-80)', () => {
  it('FR80_register_posts_the_manifest_form: an organisation owner, a manifest preview, then a real POST to GitHub', async () => {
    signInAdmin()
    const { panel, user } = await openPanel()
    expect(await within(panel).findByTestId('github-app-status')).toHaveTextContent('Not registered')

    await user.click(within(panel).getByRole('radio', { name: 'An organisation' }))
    await user.click(within(panel).getByRole('button', { name: 'Prepare registration' }))
    expect(await within(panel).findAllByText('Enter the organisation’s GitHub login, such as "acme".')).not.toHaveLength(0)
    await user.type(within(panel).getByLabelText('Organisation'), 'acme')
    await user.click(within(panel).getByRole('button', { name: 'Prepare registration' }))

    const preview = await within(panel).findByRole('region', { name: 'Manifest preview' })
    expect(githubDb.manifestRequests).toEqual([{ ownerType: 'organization', org: 'acme' }])
    expect(preview).toHaveTextContent('AIDE (localhost)')
    expect(preview).toHaveTextContent('Off (AIDE polls GitHub instead)')
    const permissions = within(preview).getByRole('list', { name: 'Requested permissions' })
    expect(within(permissions).getByText('Administration').closest('li')).toHaveTextContent('Read and write')
    expect(preview).toHaveTextContent(/Unticking Administration disables "Create repository"/)

    const submitted = captureSubmit()
    await user.click(within(panel).getByRole('button', { name: 'Register on GitHub' }))

    expect(submitted).toHaveLength(1)
    const form = submitted[0]
    expect(form?.getAttribute('action')).toBe('https://github.com/organizations/acme/settings/apps/new?state=state-abc123')
    const manifest = form ? new FormData(form).get('manifest') : null
    if (typeof manifest !== 'string') {
      throw new Error('The form has no manifest field.')
    }
    expect(JSON.parse(manifest)).toMatchObject({
      name: 'AIDE (localhost)',
      default_permissions: { administration: 'write', contents: 'write' },
    })
    // GitHub refuses a localhost webhook URL, and any hook_attributes without one: the App has no webhook.
    expect(JSON.parse(manifest)).not.toHaveProperty('hook_attributes')
  })

  it('FR80_configured: shows the status, source, slug, owner, permissions and install link', async () => {
    signInAdmin()
    configureGitHubApp({ source: 'configuration', canCreateRepositories: false, permissions: { contents: 'write', metadata: 'read' } })
    const { panel } = await openPanel()

    expect(await within(panel).findByTestId('github-app-status')).toHaveTextContent('Configured')
    expect(panel).toHaveTextContent('Set in secrets.env (GitHub__App__* configuration)')
    expect(within(panel).getByRole('link', { name: /aide-localhost/ })).toHaveAttribute('href', 'https://github.com/apps/aide-localhost')
    expect(panel).toHaveTextContent('acme (organisation)')
    const list = within(panel).getByRole('list', { name: 'GitHub App permissions' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(2)
    expect(within(panel).getByRole('link', { name: /Install the App/ })).toHaveAttribute('href', INSTALL_URL)
    expect(panel).toHaveTextContent(/"Create repository" is unavailable/)
    expect(within(panel).queryByRole('button', { name: 'Prepare registration' })).not.toBeInTheDocument()
  })

  it('FR80_callback: ?github=configured confirms; ?error=GITHUB_AUTH_FAILED explains', async () => {
    signInAdmin()
    configureGitHubApp()
    const { panel } = await openPanel('/admin/integrations?github=configured')
    expect(await within(panel).findByText('GitHub App registered')).toBeInTheDocument()
  })

  it('FR80_callback_failed: ?error=GITHUB_AUTH_FAILED shows the registration failure', async () => {
    signInAdmin()
    const { panel } = await openPanel('/admin/integrations?error=GITHUB_AUTH_FAILED')
    expect(await within(panel).findByText('GitHub App registration failed')).toBeInTheDocument()
    expect(panel).toHaveTextContent(/GitHub didn't finish registering the App/)
  })

  it('FR80_conflict: a 409 CONFLICT (registered meanwhile) shows the registered App', async () => {
    signInAdmin()
    const { panel, user } = await openPanel()
    await within(panel).findByRole('button', { name: 'Prepare registration' })
    configureGitHubApp()

    await user.click(within(panel).getByRole('button', { name: 'Prepare registration' }))

    await waitFor(() => {
      expect(within(panel).getByTestId('github-app-status')).toHaveTextContent('Configured')
    })
  })
})
