import { screen, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { signInAs, seedUser } from '../../../mocks/handlers/auth'
import { errorMessages } from '../../../lib/errors'
import { bindCredential, seedCredential } from '../../../mocks/handlers/connections'
import { disableLlmProviders } from '../../../mocks/handlers/llm'
import { problem } from '../../../mocks/handlers/problem'
import { projectsDb, seedProject } from '../../../mocks/handlers/projects'
import { server } from '../../../mocks/server'
import { openSettings, requestsTo, signInAsOwner } from '../../../test/project-settings'
import type { ProjectRole } from '../../../lib/permissions'

/** Signs in Ada as the Owner of a new project, runs `seed` with her id (before anything loads), opens Settings → LLM. */
async function openLlmAsOwner(seed?: (me: string) => void) {
  const me = signInAsOwner()
  seed?.(me.id)
  const utils = await openSettings('llm', seedProject())
  await screen.findByRole('heading', { name: 'LLM' })
  return utils
}

/** Grace (Owner) has bound her Anthropic key ••••a1b2; Ada signs in with `role`. */
async function openLlmAs(role: Exclude<ProjectRole, 'owner'>) {
  const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
  const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  const project = seedProject({
    members: [
      { userId: grace.id, role: 'owner' },
      { userId: ada.id, role },
    ],
  })
  bindCredential(project, seedCredential({ userId: grace.id, label: 'Grace key', apiKey: 'sk-ant-xxxxa1b2' }))
  const utils = await openSettings('llm', project)
  await screen.findByRole('heading', { name: 'LLM' })
  return utils
}

describe('Settings → LLM (FR-9, FR-22, FR-79)', () => {
  it('FR79_owner_binds_own_key: the picker lists the Owner\'s own keys for the provider, and saving binds one', async () => {
    const { user, project } = await openLlmAsOwner((me) => {
      seedCredential({ userId: me, provider: 'anthropic', label: 'Work key', apiKey: 'sk-ant-zzzza1b2' })
      seedCredential({ userId: me, provider: 'openai', label: 'OpenAI key', apiKey: 'sk-oa-9999' })
      const other = seedUser({ email: 'grace@example.com', name: 'Grace Hopper' })
      seedCredential({ userId: other.id, provider: 'anthropic', label: 'Not mine', apiKey: 'sk-ant-0000' })
    })

    // Unbound: the warning names the failure mode.
    expect(screen.getByText(/PROVIDER_NOT_CONFIGURED/)).toBeInTheDocument()
    const picker = await screen.findByLabelText('Anthropic connection')
    expect(within(picker).getAllByRole('option').map((o) => o.textContent)).toEqual(['No connection', 'Work key (••••a1b2)'])
    expect(screen.getByRole('link', { name: 'Add a key in Connections' })).toHaveAttribute('href', '/connections')

    await user.selectOptions(picker, 'Work key (••••a1b2)')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))

    expect(await screen.findByText('LLM settings saved.')).toBeInTheDocument()
    expect(project.llm.binding).toMatchObject({ label: 'Work key', last4: 'a1b2', boundBy: { name: 'Ada Lovelace' } })
    expect(requestsTo('PATCH', '/settings/llm').at(-1)?.body).toMatchObject({ credentialId: project.llm.binding?.credentialId })
    expect(screen.getByTestId('llm-binding')).toHaveTextContent('Anthropic · connected by Ada Lovelace (••••a1b2)')
    expect(screen.queryByText(/PROVIDER_NOT_CONFIGURED/)).not.toBeInTheDocument()
    // No v1 per-project key field any more.
    expect(screen.queryByLabelText(/API key/)).not.toBeInTheDocument()
  })

  it('FR79_owner_unbinds: Unbind clears the binding and shows the unbound warning', async () => {
    const me = signInAsOwner().id
    const project = seedProject()
    bindCredential(project, seedCredential({ userId: me, label: 'Work key', apiKey: 'sk-ant-xxxxa1b2' }))
    const { user } = await openSettings('llm', project)

    expect(await screen.findByTestId('llm-binding')).toHaveTextContent('Anthropic · connected by Ada Lovelace (••••a1b2)')
    await user.click(screen.getByRole('button', { name: 'Unbind' }))

    expect(await screen.findByText('Connection unbound.')).toBeInTheDocument()
    expect(project.llm.binding).toBeNull()
    expect(requestsTo('PATCH', '/settings/llm').at(-1)?.body).toEqual({ credentialId: null })
    expect(screen.queryByTestId('llm-binding')).not.toBeInTheDocument()
    expect(screen.getByText(/PROVIDER_NOT_CONFIGURED/)).toBeInTheDocument()
  })

  it('FR79_credential_not_owner: the API refusing another user\'s connection is shown on the picker', async () => {
    const { user } = await openLlmAsOwner((me) => {
      seedCredential({ userId: me, label: 'Work key', apiKey: 'sk-ant-zzzza1b2' })
    })
    server.use(
      http.patch('*/api/projects/:id/settings/llm', () =>
        problem(403, 'CREDENTIAL_NOT_OWNER', 'You can only bind your own connection.'),
      ),
    )
    await user.selectOptions(await screen.findByLabelText('Anthropic connection'), 'Work key (••••a1b2)')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))

    expect(await screen.findByText('You can only bind one of your own connections. Add your own key under Connections.', { selector: 'p, span' })).toBeInTheDocument()
  })

  it('FR79_contributor_sees_read_only_connected_by: other members see who connected the key, and no controls', async () => {
    await openLlmAs('contributor')

    expect(screen.getByTestId('llm-binding')).toHaveTextContent('Anthropic · connected by Grace Hopper (••••a1b2)')
    expect(screen.getByTestId('credential-status')).toHaveTextContent('Valid')
    expect(screen.getByText('Only project Owners can change the LLM settings or the bound connection.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save LLM settings' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Unbind' })).not.toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('FR79_viewer_sees_no_controls: a Viewer gets the read-only view with no buttons or fields', async () => {
    await openLlmAs('viewer')

    const panel = screen.getByRole('region', { name: 'LLM' })
    expect(within(panel).queryAllByRole('button')).toHaveLength(0)
    expect(within(panel).queryAllByRole('combobox')).toHaveLength(0)
    expect(within(panel).queryAllByRole('textbox')).toHaveLength(0)
    expect(within(panel).getByText('Anthropic')).toBeInTheDocument()
  })

  it('FR79_unbound_read_only_warns_runs_will_fail', async () => {
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: grace.id, role: 'owner' },
        { userId: ada.id, role: 'viewer' },
      ],
    })
    await openSettings('llm', project)
    expect(await screen.findByText(/agent runs fail \(PROVIDER_NOT_CONFIGURED\) unless an admin allows system keys/)).toBeInTheDocument()
  })

  it('FR79_system_key_allowed: with no binding and an allowed system key, the page says runs use it', async () => {
    projectsDb.providers = projectsDb.providers.map((p) => (p.provider === 'anthropic' ? { ...p, configured: true } : p))
    await openLlmAsOwner()
    expect(screen.getByText("No connection is bound, so runs use the server's Anthropic system key.")).toBeInTheDocument()
    expect(screen.queryByText(/PROVIDER_NOT_CONFIGURED/)).not.toBeInTheDocument()
  })

  it('FR-22: choosing Ollama fills its system default model (qwen2.5-coder) and saves provider and model', async () => {
    const { user, project } = await openLlmAsOwner()

    expect(await screen.findByLabelText('Default model')).toHaveValue('claude-opus-5-5')
    expect(screen.getByLabelText('Default model')).toHaveAccessibleDescription(
      'The system default for Anthropic is claude-opus-5-5.',
    )
    await user.selectOptions(screen.getByLabelText('Provider'), 'ollama')
    expect(screen.getByLabelText('Default model')).toHaveValue('qwen2.5-coder')
    expect(screen.getByText('Ollama needs no key.')).toBeInTheDocument()
    // FR-159: an id outside the catalog goes through "Other model id…".
    await user.selectOptions(screen.getByLabelText('Code generator'), 'Other model id…')
    await user.type(screen.getByLabelText('Code generator: model id'), 'qwen2.5-coder:32b')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))

    expect(await screen.findByText('LLM settings saved.')).toBeInTheDocument()
    expect(project.llm).toMatchObject({
      provider: 'ollama',
      defaultModel: 'qwen2.5-coder',
      modelsByAgent: { codeGenerator: 'qwen2.5-coder:32b' },
    })
    expect(requestsTo('PATCH', '/settings/llm').at(-1)?.body).not.toHaveProperty('credentialId')
  })

  it('FR159_settings_default_model_uses_the_catalog_picker_with_max_output_tokens', async () => {
    const { user, project } = await openLlmAsOwner()

    const model = await screen.findByLabelText('Default model')
    expect(model).toHaveValue('claude-opus-5-5')
    expect(within(model).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Claude Opus 5.5 · max output 128,000 tokens (recommended)',
      'Claude Sonnet 5.5 · max output 128,000 tokens',
      'Claude Haiku 4.5 · max output 64,000 tokens',
      'Other model id…',
    ])
    const spec = screen.getByLabelText('Spec generator')
    expect(spec).toHaveValue('')
    expect(within(spec).getAllByRole('option')[0]).toHaveTextContent('Use the default model')

    await user.selectOptions(model, 'Claude Haiku 4.5 · max output 64,000 tokens')
    await user.selectOptions(spec, 'Claude Sonnet 5.5 · max output 128,000 tokens')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))

    expect(await screen.findByText('LLM settings saved.')).toBeInTheDocument()
    expect(project.llm).toMatchObject({ defaultModel: 'claude-haiku-4-5', modelsByAgent: { specGenerator: 'claude-sonnet-5-5' } })
  })

  it('FR159_settings_other_model_id_requires_an_id', async () => {
    const { user } = await openLlmAsOwner()
    await user.selectOptions(await screen.findByLabelText('Default model'), 'Other model id…')
    const typed = screen.getByLabelText('Default model: model id')
    await user.clear(typed)
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))
    expect(await screen.findAllByText('Enter the default model id.')).not.toHaveLength(0)
    expect(requestsTo('PATCH', '/settings/llm')).toHaveLength(0)
  })

  it('FR157_settings_provider_turned_off: the page says so and requires another provider before saving', async () => {
    // A project set to Fake before the server turned Fake (and Ollama) off, as on Railway.
    signInAsOwner()
    disableLlmProviders('ollama', 'fake')
    const project = seedProject()
    project.llm.provider = 'fake'
    project.llm.defaultModel = 'fake-1'
    const { user } = await openSettings('llm', project)
    await screen.findByRole('heading', { name: 'LLM' })

    expect(screen.getByText(/This provider is turned off on this server\. Choose another provider\./)).toBeInTheDocument()
    const provider = screen.getByLabelText('Provider')
    expect(provider).toHaveValue('')
    expect(within(provider).getAllByRole('option').map((o) => o.getAttribute('value'))).toEqual(['', 'anthropic', 'openai'])

    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))
    expect(
      await screen.findAllByText('Choose another provider: the current one is turned off on this server.'),
    ).not.toHaveLength(0)
    expect(requestsTo('PATCH', '/settings/llm')).toHaveLength(0)

    await user.selectOptions(provider, 'anthropic')
    expect(await screen.findByLabelText('Default model')).toHaveValue('claude-opus-5-5')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))
    expect(await screen.findByText('LLM settings saved.')).toBeInTheDocument()
    expect(project.llm).toMatchObject({ provider: 'anthropic', defaultModel: 'claude-opus-5-5' })
  })

  it('FR157_settings_read_only_members_see_the_turned_off_notice', async () => {
    disableLlmProviders('ollama', 'fake')
    const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({ members: [{ userId: grace.id, role: 'owner' }, { userId: ada.id, role: 'viewer' }] })
    project.llm.provider = 'fake'
    await openSettings('llm', project)
    await screen.findByRole('heading', { name: 'LLM' })
    expect(screen.getByText(/This provider is turned off on this server\. Choose another provider\./)).toBeInTheDocument()
  })

  it('FR157_settings_server_LLM_PROVIDER_UNAVAILABLE_is_shown_on_the_provider', async () => {
    server.use(
      http.patch('*/api/projects/:id/settings/llm', () => problem(400, 'LLM_PROVIDER_UNAVAILABLE', 'Turned off.')),
    )
    const { user } = await openLlmAsOwner()
    await screen.findByLabelText('Default model')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))
    expect(await screen.findAllByText(errorMessages.LLM_PROVIDER_UNAVAILABLE)).not.toHaveLength(0)
  })
})
