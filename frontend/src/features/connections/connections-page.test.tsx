import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { bindCredential, connectionsDb, seedCredential } from '../../mocks/handlers/connections'
import { disableLlmProviders } from '../../mocks/handlers/llm'
import { projectsDb, seedProject } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'
import { WIZARD_DRAFT_KEY } from '../projects/wizard-draft'

function signInAda() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

async function openConnections(path = '/connections') {
  const utils = await renderRoute(path)
  await screen.findByRole('heading', { level: 1, name: 'Connections' })
  return { ...utils, user: userEvent.setup() }
}

const llmSection = () => screen.getByRole('region', { name: 'LLM providers' })

describe('/connections → LLM providers (FR-78)', () => {
  it('FR78_list_shows_last4_and_status: each key shows provider, label, ••••last4, status and bound projects; keyless providers are listed', async () => {
    const me = signInAda()
    const project = seedProject({ name: 'Payments' })
    bindCredential(project, seedCredential({ userId: me.id, label: 'Work key', apiKey: 'sk-ant-xxxxa1b2' }))
    seedCredential({ userId: me.id, provider: 'openai', label: 'Old key', apiKey: 'sk-oa-zz9f', status: 'invalid' })
    await openConnections()

    const list = await within(llmSection()).findByRole('list', { name: 'Your LLM keys' })
    const work = within(list).getByRole('listitem', { name: 'Anthropic key Work key' })
    expect(within(work).getByTestId('credential-last4')).toHaveTextContent('••••a1b2')
    expect(within(work).getByTestId('credential-status')).toHaveTextContent('Valid')
    expect(within(work).getByRole('link', { name: 'Payments' })).toHaveAttribute('href', `/projects/${project.id}/settings/llm`)
    const old = within(list).getByRole('listitem', { name: 'OpenAI key Old key' })
    expect(within(old).getByTestId('credential-status')).toHaveTextContent('Invalid')
    expect(within(old).getByText('Not used by any project')).toBeInTheDocument()
    // The key itself is never on the page.
    expect(document.body).not.toHaveTextContent('sk-ant-xxxxa1b2')

    const keyless = within(llmSection()).getByRole('list', { name: 'Providers without a key' })
    expect(within(keyless).getByText('Ollama')).toBeInTheDocument()
    expect(within(keyless).getByText('Fake (testing)')).toBeInTheDocument()

    // GitHub is covered by github-connection.test.tsx (FR-81), Vercel by vercel-connection.test.tsx (FR-87).
    expect(await within(screen.getByRole('region', { name: 'Vercel' })).findByText('Not connected')).toBeInTheDocument()
  })

  it('FR78_add_key_rejected: a key the provider rejects shows CREDENTIAL_INVALID copy, is cleared, and nothing is listed', async () => {
    signInAda()
    connectionsDb.rejectedKeys.add('sk-ant-bad1')
    const { user, queryClient } = await openConnections()
    expect(await within(llmSection()).findByText('No LLM keys yet')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Add key' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add an LLM key' })
    expect(within(dialog).getByLabelText('API key')).toHaveAttribute('type', 'password')
    await user.type(within(dialog).getByLabelText('Label'), 'Work key')
    await user.type(within(dialog).getByLabelText('API key'), 'sk-ant-bad1')
    await user.click(within(dialog).getByRole('button', { name: 'Add key' }))

    expect(
      await within(dialog).findByText(/The provider rejected this key, so it wasn't saved/, { selector: 'p, span' }),
    ).toBeInTheDocument()
    expect(within(dialog).getByLabelText('API key')).toHaveValue('')
    await waitFor(() => {
      expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain('sk-ant-bad1')
    })
    expect(connectionsDb.credentials).toHaveLength(0)
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(within(llmSection()).getByText('No LLM keys yet')).toBeInTheDocument()
  })

  it('FR78_add_key: a valid key is added and listed by its last 4 characters only', async () => {
    signInAda()
    const { user, queryClient } = await openConnections()
    await user.click(await screen.findByRole('button', { name: 'Add key' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add an LLM key' })
    await user.selectOptions(within(dialog).getByLabelText('Provider'), 'openai')
    await user.type(within(dialog).getByLabelText('Label'), 'Laptop')
    await user.type(within(dialog).getByLabelText('API key'), 'sk-proj-abcd9z9z')
    await user.click(within(dialog).getByRole('button', { name: 'Add key' }))

    expect(await screen.findByText('"Laptop" added and checked.')).toBeInTheDocument()
    const row = await within(llmSection()).findByRole('listitem', { name: 'OpenAI key Laptop' })
    expect(within(row).getByTestId('credential-last4')).toHaveTextContent('••••9z9z')
    expect(document.body).not.toHaveTextContent('sk-proj-abcd9z9z')
    // The plaintext key isn't kept in the MutationCache (variables) or the reopened form.
    await waitFor(() => {
      expect(queryClient.getMutationCache().getAll().map((m) => m.state.variables)).not.toContainEqual(
        expect.objectContaining({ apiKey: 'sk-proj-abcd9z9z' }),
      )
    })
    await user.click(screen.getByRole('button', { name: 'Add key' }))
    expect(within(await screen.findByRole('dialog', { name: 'Add an LLM key' })).getByLabelText('API key')).toHaveValue('')
  })

  it('FR78_upstream_error: an unreachable provider on Test shows UPSTREAM_ERROR copy', async () => {
    const me = signInAda()
    seedCredential({ userId: me.id, label: 'Work key' })
    connectionsDb.providerDown = true
    const { user } = await openConnections()
    await user.click(await screen.findByRole('button', { name: 'Test Work key' }))
    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't reach the provider to check the key.")
  })

  it('FR78_test_and_rename: Test re-checks the key and Rename changes the label', async () => {
    const me = signInAda()
    const c = seedCredential({ userId: me.id, label: 'Work key', apiKey: 'sk-ant-xxxxa1b2', status: 'unchecked', lastValidatedAt: null })
    connectionsDb.rejectedKeys.add(c.apiKey)
    const { user } = await openConnections()
    await user.click(await screen.findByRole('button', { name: 'Test Work key' }))
    expect(await screen.findByText(/"Work key" is invalid/)).toBeInTheDocument()
    await waitFor(() => {
      expect(within(llmSection()).getByTestId('credential-status')).toHaveTextContent('Invalid')
    })

    await user.click(screen.getByRole('button', { name: 'Rename Work key' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename "Work key"' })
    await user.clear(within(dialog).getByLabelText('Label'))
    await user.type(within(dialog).getByLabelText('Label'), 'Personal')
    await user.click(within(dialog).getByRole('button', { name: 'Save label' }))
    expect(await within(llmSection()).findByRole('listitem', { name: 'Anthropic key Personal' })).toBeInTheDocument()
  })

  it('FR78_delete_warns_about_bound_projects: the confirmation lists the projects that lose their binding', async () => {
    const me = signInAda()
    const payments = seedProject({ name: 'Payments' })
    const search = seedProject({ name: 'Search' })
    const c = seedCredential({ userId: me.id, label: 'Work key' })
    bindCredential(payments, c)
    bindCredential(search, c)
    const { user } = await openConnections()

    await user.click(await screen.findByRole('button', { name: 'Delete Work key' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Delete "Work key"?' })
    const warning = within(dialog).getByTestId('delete-bound-warning')
    expect(warning).toHaveTextContent('This key is bound to 2 projects')
    expect(within(warning).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Payments', 'Search'])
    await user.click(within(dialog).getByRole('button', { name: 'Delete key' }))

    expect(await screen.findByText('"Work key" deleted.')).toBeInTheDocument()
    expect(connectionsDb.credentials).toHaveLength(0)
    expect(projectsDb.projects.map((p) => p.llm.binding)).toEqual([null, null])
    expect(await within(llmSection()).findByText('No LLM keys yet')).toBeInTheDocument()
  })

  it('FR78: /account/connections redirects to /connections', async () => {
    signInAda()
    const { router } = await openConnections('/account/connections')
    expect(router.state.location.pathname).toBe('/connections')
  })

  it('FR78, FR79: CONNECTION_INVALID and LLM_BINDING_CLEARED have activity summaries', () => {
    expect(
      describeActivity({ type: 'CONNECTION_INVALID', summary: 'x', payload: { provider: 'openai', credentialId: 'c' } }),
    ).toBe('OpenAI connection marked invalid: the provider rejected its key during a run')
    expect(
      describeActivity({ type: 'LLM_BINDING_CLEARED', summary: 'x', payload: { provider: 'anthropic', reason: 'connection_deleted' } }),
    ).toBe('Anthropic connection unbound: the connection was deleted')
    expect(
      describeActivity({ type: 'LLM_BINDING_CLEARED', summary: 'x', payload: { provider: 'anthropic', reason: 'owner_role_lost' } }),
    ).toBe('Anthropic connection unbound: the Owner who bound it is no longer an Owner')
  })
})

describe('/connections: providers enabled on this server and the way back to the wizard (FR-157, FR-159)', () => {
  it('FR157_connections_list_only_enabled_providers: turned-off providers are neither listed nor offered for a key', async () => {
    disableLlmProviders('openai', 'ollama')
    signInAda()
    const { user } = await openConnections()

    const keyless = await within(llmSection()).findByRole('list', { name: 'Providers without a key' })
    expect(within(keyless).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      expect.stringContaining('Fake (testing)'),
    ])
    await user.click(screen.getByRole('button', { name: 'Add key' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add an LLM key' })
    const provider = within(dialog).getByLabelText('Provider')
    await waitFor(() => {
      expect(within(provider).getAllByRole('option').map((o) => o.textContent)).toEqual(['Anthropic'])
    })
  })

  it('FR159_connections_show_no_way_back_without_a_saved_wizard', async () => {
    signInAda()
    await openConnections()
    expect(screen.queryByRole('link', { name: 'Back to the new project' })).not.toBeInTheDocument()
  })

  it('FR159_connections_link_back_to_the_new_project_when_the_wizard_was_saved', async () => {
    sessionStorage.setItem(WIZARD_DRAFT_KEY, JSON.stringify({ values: { name: 'Inventory tracker' }, step: 5 }))
    signInAda()
    await openConnections()
    expect(await screen.findByRole('link', { name: 'Back to the new project' })).toHaveAttribute('href', '/projects/new')
  })
})
