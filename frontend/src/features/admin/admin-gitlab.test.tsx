import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { GITLAB_CALLBACK_URL, gitlabDb, seedGitLabInstance } from '../../mocks/handlers/gitlab'
import { renderRoute } from '../../test/render'

function signInAdmin() {
  return signInAs({ email: 'admin@example.com', name: 'Ada Admin', systemRole: 'admin' })
}

async function openPanel() {
  await renderRoute('/admin/integrations')
  const panel = await screen.findByRole('region', { name: 'GitLab' })
  await within(panel).findByRole('list', { name: 'GitLab instances' })
  return { panel, user: userEvent.setup() }
}

describe('Admin → Integrations → GitLab (FR-130)', () => {
  it('FR130_registration_help: shows the exact callback URL, the scopes and gitlab.com by default', async () => {
    signInAdmin()
    const { panel } = await openPanel()

    expect(within(panel).getByTestId('gitlab-callback-url')).toHaveTextContent(GITLAB_CALLBACK_URL)
    expect(within(panel).getByTestId('gitlab-scopes')).toHaveTextContent('api write_repository openid profile email')
    const row = within(panel).getByRole('listitem', { name: 'GitLab.com' })
    expect(row).toHaveTextContent('Default')
    expect(within(row).getByTestId('gitlab-oauth-missing')).toBeInTheDocument()
    // gitlab.com can be disabled, not removed.
    expect(within(row).queryByRole('button', { name: 'Remove GitLab.com' })).not.toBeInTheDocument()
  })

  it('FR130_add_instance: adds a self-managed instance with its OAuth application', async () => {
    signInAdmin()
    const { panel, user } = await openPanel()

    await user.click(within(panel).getByRole('button', { name: 'Add instance' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a GitLab instance' })
    await user.type(within(dialog).getByLabelText('Name'), 'Lab GitLab')
    await user.type(within(dialog).getByLabelText('Base URL'), 'http://gitlab.lab.example')
    await user.click(within(dialog).getByRole('button', { name: 'Add instance' }))
    expect(await within(dialog).findAllByText('Use https://, or tick "Allow insecure HTTP" for a lab host.')).not.toHaveLength(0)

    await user.click(within(dialog).getByLabelText('Allow insecure HTTP'))
    await user.type(within(dialog).getByLabelText('Application ID (optional)'), 'app-1')
    await user.type(within(dialog).getByLabelText('Secret (optional)'), 's3cret')
    await user.click(within(dialog).getByRole('button', { name: 'Add instance' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Add a GitLab instance' })).not.toBeInTheDocument()
    })
    expect(gitlabDb.createdInstances).toEqual([
      {
        name: 'Lab GitLab',
        baseUrl: 'http://gitlab.lab.example',
        allowInsecureHttp: true,
        enabled: true,
        clientId: 'app-1',
        clientSecret: 's3cret',
      },
    ])
    const row = await within(panel).findByRole('listitem', { name: 'Lab GitLab' })
    expect(row).toHaveTextContent('Allowed (lab host)')
    expect(within(row).getByTestId('gitlab-version')).toHaveTextContent('GitLab 17.11.2')
  })

  it('FR130_ssrf_guard: GIT_HOST_NOT_ALLOWED is shown on the base URL', async () => {
    signInAdmin()
    const { panel, user } = await openPanel()

    await user.click(within(panel).getByRole('button', { name: 'Add instance' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a GitLab instance' })
    await user.type(within(dialog).getByLabelText('Name'), 'Metadata')
    await user.type(within(dialog).getByLabelText('Base URL'), 'https://169.254.169.254')
    await user.click(within(dialog).getByRole('button', { name: 'Add instance' }))

    expect(await within(dialog).findAllByText("That address isn't allowed for a GitLab instance.")).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('Base URL')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR130_check_old_version: Check records the version and warns below GitLab 17.11', async () => {
    signInAdmin()
    seedGitLabInstance({ name: 'Old GitLab', baseUrl: 'https://old.example' })
    gitlabDb.nextVersion = '16.9.0'
    const { panel, user } = await openPanel()

    const row = within(panel).getByRole('listitem', { name: 'Old GitLab' })
    await user.click(within(row).getByRole('button', { name: 'Check Old GitLab' }))

    expect(await within(row).findByText('Older than the supported version')).toBeInTheDocument()
    expect(within(row).getByTestId('gitlab-version')).toHaveTextContent('GitLab 16.9.0')
  })

  it('FR130_disable: disabling asks for confirmation, then shows the instance as disabled', async () => {
    signInAdmin()
    seedGitLabInstance({ name: 'Acme GitLab', connectionCount: 2 })
    const { panel, user } = await openPanel()

    const row = within(panel).getByRole('listitem', { name: 'Acme GitLab' })
    await user.click(within(row).getByRole('button', { name: 'Disable Acme GitLab' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Disable "Acme GitLab"?' })
    expect(confirm).toHaveTextContent('Its 2 connections stop working')
    await user.click(within(confirm).getByRole('button', { name: 'Disable instance' }))

    await waitFor(() => {
      expect(within(row).getByTestId('gitlab-instance-enabled')).toHaveTextContent('Disabled')
    })
    expect(gitlabDb.updates.at(-1)?.body).toEqual({ enabled: false })
  })

  it('FR130_remove_in_use: a 409 CONFLICT explains why it can’t be removed', async () => {
    signInAdmin()
    seedGitLabInstance({ name: 'Busy GitLab', projectCount: 3 })
    const { panel, user } = await openPanel()

    await user.click(within(panel).getByRole('button', { name: 'Remove Busy GitLab' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Remove "Busy GitLab"?' })
    await user.click(within(confirm).getByRole('button', { name: 'Remove instance' }))

    const alert = await within(panel).findByRole('alert')
    expect(alert).toHaveTextContent('We couldn\'t remove "Busy GitLab"')
    expect(alert).toHaveTextContent(/projects use it/)
    expect(within(panel).getByRole('listitem', { name: 'Busy GitLab' })).toBeInTheDocument()
  })

  it('FR130_configured_instance: an instance from configuration is read-only except Enabled', async () => {
    signInAdmin()
    seedGitLabInstance({ name: 'Env GitLab', source: 'configuration' })
    const { panel } = await openPanel()

    const row = within(panel).getByRole('listitem', { name: 'Env GitLab' })
    expect(row).toHaveTextContent('Set in configuration (GitLab__Instances__*)')
    expect(within(row).queryByRole('button', { name: 'Edit Env GitLab' })).not.toBeInTheDocument()
    expect(within(row).queryByRole('button', { name: 'Remove Env GitLab' })).not.toBeInTheDocument()
    expect(within(row).getByRole('button', { name: 'Disable Env GitLab' })).toBeInTheDocument()
  })
})
