import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { errorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { platformDb, seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { projectsDb } from '../../mocks/handlers/projects'
import { seedTrackerStatus } from '../../mocks/handlers/trackers'
import { renderRoute } from '../../test/render'

async function openConnections() {
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  await renderRoute('/connections')
  await screen.findByRole('heading', { level: 1, name: 'Connections' })
  return { me, user: userEvent.setup() }
}

const section = () => screen.getByRole('region', { name: 'Issue trackers' })
const posts = () => projectsDb.requests.filter((r) => r.method === 'POST' && r.path.endsWith('/connections/platforms'))

describe('/connections → Issue trackers (FR-165–FR-168)', () => {
  it('FR165_lists_the_four_trackers_and_keeps_them_out_of_the_deployment_sections', async () => {
    const { me } = await openConnections()
    expect(me.id).toBeTruthy()
    for (const name of ['Jira', 'Linear', 'GitHub Issues', 'GitLab Issues']) {
      expect(await within(section()).findByRole('region', { name })).toBeInTheDocument()
    }
    expect(within(screen.getByRole('region', { name: 'Deployment platforms' })).queryByRole('region', { name: 'Jira' })).not.toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Databases' })).queryByRole('region', { name: 'Linear' })).not.toBeInTheDocument()
  })

  it('FR165_jira_connections_never_appear_in_the_deployment_sections', async () => {
    const me = signInAs({ systemRole: 'user' })
    seedPlatformConnection({ userId: me.id, provider: 'jira', label: 'Acme Jira', metadata: { site: 'acme.atlassian.net' }, scopes: ['read'] })
    await renderRoute('/connections')
    const row = await within(await screen.findByRole('region', { name: 'Issue trackers' })).findByRole('listitem', { name: 'Jira connection Acme Jira' })
    expect(within(row).getByTestId('platform-account')).toHaveTextContent('Site acme.atlassian.net · account ada')
    expect(within(row).getByTestId('platform-scopes')).toHaveTextContent('read')
    expect(within(screen.getByRole('region', { name: 'Deployment platforms' })).queryByText('Acme Jira')).not.toBeInTheDocument()
  })

  it('FR165_jira_dialog_says_what_to_create_and_sends_site_email_expiry_and_field', async () => {
    const { user } = await openConnections()
    await user.click(await screen.findByRole('button', { name: 'Add Jira connection' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a Jira connection' })
    const permissions = within(dialog).getByRole('list', { name: 'Jira permissions to grant' })
    expect(within(permissions).getByText('read:jira-work')).toBeInTheDocument()
    expect(within(dialog).getByRole('link', { name: /Create a token on id\.atlassian\.com/ })).toHaveAttribute('rel', 'noreferrer noopener')

    await user.type(within(dialog).getByLabelText('Label'), 'Acme Jira')
    await user.type(within(dialog).getByLabelText('Jira site'), 'https://acme.atlassian.net/')
    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'atl_token1234')
    await user.type(within(dialog).getByLabelText('Expiry date (optional)'), '2027-03-01')
    await user.type(within(dialog).getByLabelText('Acceptance criteria field (optional)'), 'customfield_10042')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    await waitFor(() => {
      expect(platformDb.connections).toHaveLength(1)
    })
    expect(posts()[0]?.body).toMatchObject({
      provider: 'jira',
      site: 'acme.atlassian.net',
      email: 'ada@example.com',
      expiresAt: '2027-03-01T00:00:00.000Z',
      acceptanceCriteriaField: 'customfield_10042',
    })
  })

  it('FR165_a_site_outside_atlassian_net_is_refused_with_the_spec_copy_and_nothing_sent', async () => {
    const { user } = await openConnections()
    await user.click(await screen.findByRole('button', { name: 'Add Jira connection' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a Jira connection' })
    await user.type(within(dialog).getByLabelText('Label'), 'Evil')
    await user.type(within(dialog).getByLabelText('Jira site'), '169.254.169.254')
    await user.type(within(dialog).getByLabelText('Atlassian account email'), 'ada@example.com')
    await user.type(within(dialog).getByLabelText('API token'), 'atl_token1234')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    expect(await within(dialog).findAllByText(errorMessages.TRACKER_SITE_NOT_ALLOWED)).not.toHaveLength(0)
    expect(posts()).toHaveLength(0)
  })

  it('FR165_edit_picks_the_acceptance_criteria_field_from_jira_and_can_clear_the_expiry', async () => {
    const me = signInAs({ systemRole: 'user' })
    const c = seedPlatformConnection({ userId: me.id, provider: 'jira', label: 'Acme Jira', expiresAt: '2027-03-01T00:00:00Z', metadata: { site: 'acme.atlassian.net' } })
    await renderRoute('/connections')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Edit Acme Jira' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit "Acme Jira"' })
    await user.selectOptions(await within(dialog).findByLabelText('Acceptance criteria field (optional)'), 'customfield_10042')
    await user.clear(within(dialog).getByLabelText('Expiry date (optional)'))
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(c.metadata.acceptanceCriteriaField).toBe('customfield_10042')
    })
    expect(c.expiresAt).toBeNull()
  })

  it('FR165_shows_the_expiry_warning_as_text', async () => {
    const me = signInAs({ systemRole: 'user' })
    seedPlatformConnection({ userId: me.id, provider: 'linear', label: 'Key', expiresAt: '2099-01-01T00:00:00Z', expiryWarning: true })
    await renderRoute('/connections')
    expect(await screen.findByTestId('platform-expiry-warning')).toHaveTextContent('expires soon')
  })

  it('FR166_linear_dialog_recommends_read_only_and_a_rejected_key_saves_nothing', async () => {
    const { user } = await openConnections()
    platformDb.rejectedTokens.add('lin_badkey')
    await user.click(await screen.findByRole('button', { name: 'Add Linear connection' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a Linear connection' })
    expect(within(dialog).getByTestId('platform-token-guidance')).toHaveTextContent('Read-only is recommended'.toLowerCase())
    await user.type(within(dialog).getByLabelText('Label'), 'Key')
    await user.type(within(dialog).getByLabelText('API key'), 'lin_badkey')
    await user.click(within(dialog).getByRole('button', { name: 'Add connection' }))

    expect(await within(dialog).findAllByText(/Linear rejected this key/)).not.toHaveLength(0)
    expect(platformDb.connections).toHaveLength(0)
  })

  it('FR167_github_issues_names_the_missing_permission_with_the_app_settings_link', async () => {
    seedTrackerStatus('github', { ready: false, reason: 'issues_permission_missing', settingsUrl: 'https://github.com/apps/aide' })
    await openConnections()
    const github = await within(section()).findByRole('region', { name: 'GitHub Issues' })
    expect(await within(github).findByText("Issues permission missing: update the App's permissions in GitHub, then accept them on each installation")).toBeInTheDocument()
    expect(within(github).getByRole('link', { name: /Open the GitHub App settings/ })).toHaveAttribute('href', 'https://github.com/apps/aide')
    expect(within(github).getByText('Not ready')).toBeInTheDocument()
  })

  it('FR168_gitlab_issues_is_ready_with_no_further_setup', async () => {
    seedTrackerStatus('gitlab', { ready: true, reason: null, connections: [{ id: 'g1', label: 'gitlab.com', status: 'valid', accountName: 'ada', detail: null }] })
    await openConnections()
    const gitlab = await within(section()).findByRole('region', { name: 'GitLab Issues' })
    expect(within(gitlab).getByText('Ready')).toBeInTheDocument()
    expect(within(gitlab).getByText(/no further setup/)).toHaveTextContent('ada')
  })
})
