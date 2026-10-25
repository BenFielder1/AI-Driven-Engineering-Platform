import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { errorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { seedCredential } from '../../mocks/handlers/connections'
import { disableLlmProviders, llmDb, llmProvidersFor } from '../../mocks/handlers/llm'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { WIZARD_DRAFT_KEY } from './wizard-draft'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })
const createBody = () =>
  projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/projects')?.body as
    | Schemas['CreateProjectRequest']
    | undefined

/** Signs in (optionally seeding connections first), then walks Source → Idea → Repository (Local) → … → Configuration. */
async function toConfiguration(setup?: (userId: string) => void) {
  const me = signInAs({ systemRole: 'user' })
  setup?.(me.id)
  const utils = await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await next(user)
  await stepHeading(/Step 2 of 8: Project idea/)
  await user.type(screen.getByLabelText('Project name'), 'Inventory tracker')
  await user.type(screen.getByLabelText('Your idea'), 'Track stock across warehouses.')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  await user.click(screen.getByRole('radio', { name: 'Local repository' }))
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  const section = await screen.findByRole('region', { name: 'AI model' })
  await within(section).findByRole('group', { name: 'Provider' })
  return { ...utils, user, section, me }
}

/** Configuration → Deployment → Review & create → Create project. */
async function createFromConfiguration(user: User) {
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
  await next(user)
  await stepHeading(/Step 8 of 8: Review & create/)
  await user.click(screen.getByRole('button', { name: 'Create project' }))
}

describe('New project wizard: AI model section (FR-157, FR-158, FR-159)', () => {
  it('FR159_unusable_providers_are_disabled_with_add_a_connection_and_never_preselected', async () => {
    const { section } = await toConfiguration()

    const anthropic = within(section).getByRole('radio', { name: 'Anthropic' })
    expect(anthropic).toBeDisabled()
    expect(anthropic).not.toBeChecked()
    expect(anthropic).toHaveAccessibleDescription('Needs a connection: add your Anthropic API key in Connections to use it.')
    expect(within(section).getByRole('radio', { name: 'OpenAI' })).toBeDisabled()
    const links = within(section).getAllByRole('link', { name: 'Add a connection' })
    expect(links).toHaveLength(2)
    expect(links[0]).toHaveAttribute('href', '/connections')
    expect(links[0]).toHaveAccessibleDescription('Needs a connection: add your Anthropic API key in Connections to use it.')

    // The server's default (Anthropic) isn't usable, so it marks the first usable one (keyless Ollama) isDefault.
    const ollama = within(section).getByRole('radio', { name: 'Ollama' })
    expect(ollama).toBeEnabled()
    await waitFor(() => {
      expect(ollama).toBeChecked()
    })
    expect(ollama).toHaveAccessibleDescription('No key needed.')
    expect(within(section).queryByLabelText(/connection$/)).not.toBeInTheDocument()
    expect(within(section).getByLabelText('Default model')).toHaveValue('qwen2.5-coder')
  })

  it('FR159_recommended_usable_provider_is_preselected_with_first_connection_and_payload_has_llm', async () => {
    let newest = ''
    const { user, section } = await toConfiguration((userId) => {
      seedCredential({ userId, provider: 'anthropic', label: 'Old key', apiKey: 'sk-ant-old-1111' })
      newest = seedCredential({ userId, provider: 'anthropic', label: 'Work key', apiKey: 'sk-ant-new-a1b2' }).id
    })

    const anthropic = within(section).getByRole('radio', { name: 'Anthropic' })
    await waitFor(() => {
      expect(anthropic).toBeChecked()
    })
    expect(anthropic).toHaveAccessibleDescription('Uses one of your 2 connections.')
    // The connection list is newest first; the first one is preselected.
    const connection = within(section).getByLabelText('Anthropic connection')
    expect(connection).toHaveValue(newest)
    expect(within(connection).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Work key (••••a1b2)',
      'Old key (••••1111)',
    ])
    const model = within(section).getByLabelText('Default model')
    expect(model).toHaveValue('claude-opus-5-5')
    expect(within(model).getByRole('option', { name: 'Claude Opus 5.5 · max output 128,000 tokens (recommended)' })).toBeInTheDocument()
    expect(within(model).getByRole('option', { name: 'Claude Haiku 4.5 · max output 64,000 tokens' })).toBeInTheDocument()
    expect(within(model).getByRole('option', { name: 'Other model id…' })).toBeInTheDocument()

    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    const config = screen.getByRole('region', { name: 'Configuration' })
    expect(within(config).getByText('AI provider').nextElementSibling).toHaveTextContent('Anthropic')
    expect(within(config).getByText('Connection').nextElementSibling).toHaveTextContent('Work key (••••a1b2)')
    expect(within(config).getByText('Default model').nextElementSibling).toHaveTextContent('Claude Opus 5.5 (claude-opus-5-5)')

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Inventory tracker' })).toBeInTheDocument()
    expect(createBody()?.llm).toEqual({ provider: 'anthropic', defaultModel: 'claude-opus-5-5', credentialId: newest })
    expect(projectsDb.projects[0]?.llm).toMatchObject({
      provider: 'anthropic',
      defaultModel: 'claude-opus-5-5',
      binding: { credentialId: newest },
    })
  })

  it('FR159_ci_style_list_preselects_the_isDefault_provider_fake', async () => {
    // CI: Llm__DefaultProvider=fake, no system keys, all four providers enabled.
    llmDb.defaultProvider = 'fake'
    const { section, user } = await toConfiguration()

    const fake = within(section).getByRole('radio', { name: 'Fake (testing)' })
    await waitFor(() => {
      expect(fake).toBeChecked()
    })
    expect(within(section).getByRole('radio', { name: 'Ollama' })).not.toBeChecked()
    expect(within(section).getByLabelText('Default model')).toHaveValue('fake-1')

    await createFromConfiguration(user)
    expect(await screen.findByRole('heading', { level: 1, name: 'Inventory tracker' })).toBeInTheDocument()
    expect(createBody()?.llm).toEqual({ provider: 'fake', defaultModel: 'fake-1' })
  })

  it('FR159_no_isDefault_provider_means_no_preselection_and_a_required_choice', async () => {
    let signedInId = ''
    server.use(
      http.get('*/api/llm/providers', () => {
        const list = llmProvidersFor(signedInId).map((p) => ({ ...p, isDefault: false }))
        return HttpResponse.json(list)
      }),
    )
    const { section, user } = await toConfiguration((id) => {
      signedInId = id
    })

    for (const radio of within(section).getAllByRole('radio')) {
      expect(radio).not.toBeChecked()
    }
    expect(within(section).getByRole('radio', { name: 'Ollama' })).toBeEnabled()
    await next(user)
    expect(
      await screen.findAllByText('Choose an AI provider you can use, or add a connection for one.'),
    ).not.toHaveLength(0)
    await user.click(within(section).getByRole('radio', { name: 'Ollama' }))
    expect(within(section).getByLabelText('Default model')).toHaveValue('qwen2.5-coder')
  })

  it('FR159_model_picker_other_model_id_and_advanced_per_agent_overrides', async () => {
    const { user, section } = await toConfiguration()
    await user.click(within(section).getByRole('radio', { name: 'Fake (testing)' }))
    expect(within(section).getByLabelText('Default model')).toHaveValue('fake-1')

    // "Other model id…" with nothing typed fails validation.
    await user.selectOptions(within(section).getByLabelText('Default model'), 'Other model id…')
    const other = within(section).getByLabelText('Default model: model id')
    await user.clear(other)
    await next(user)
    expect(await screen.findAllByText('Choose the default model, or enter its id.')).not.toHaveLength(0)
    expect(screen.getByRole('heading', { level: 2, name: /Step 6 of 8/ })).toBeInTheDocument()
    await user.type(within(section).getByLabelText('Default model: model id'), 'fake-2-preview')

    // Per-agent overrides sit in a collapsed "Advanced" area.
    const advanced = within(section).getByText('Advanced: model per agent (optional)')
    expect(advanced.closest('details')).not.toHaveAttribute('open')
    await user.click(advanced)
    const codeGen = within(section).getByLabelText('Code generator')
    expect(codeGen).toHaveValue('')
    await user.selectOptions(codeGen, 'Fake (offline) · max output 128,000 tokens')

    await createFromConfiguration(user)
    expect(await screen.findByRole('heading', { level: 1, name: 'Inventory tracker' })).toBeInTheDocument()
    expect(createBody()?.llm).toEqual({
      provider: 'fake',
      defaultModel: 'fake-2-preview',
      modelsByAgent: { codeGenerator: 'fake-1' },
    })
  })

  it('FR157_FR159_only_enabled_providers_are_listed_and_none_is_preselected_when_none_is_usable', async () => {
    disableLlmProviders('ollama', 'fake')
    const { user, section } = await toConfiguration()

    expect(within(section).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual(['anthropic', 'openai'])
    for (const radio of within(section).getAllByRole('radio')) {
      expect(radio).toBeDisabled()
      expect(radio).not.toBeChecked()
    }
    expect(within(section).getByText('Add a connection to choose a provider')).toBeInTheDocument()
    expect(within(section).queryByLabelText('Default model')).not.toBeInTheDocument()

    await next(user)
    expect(
      await screen.findAllByText('Choose an AI provider you can use, or add a connection for one.'),
    ).not.toHaveLength(0)
    expect(screen.getByRole('heading', { level: 2, name: /Step 6 of 8/ })).toBeInTheDocument()
  })

  it('FR159_add_a_connection_keeps_the_wizard_in_session_storage_and_restores_it_on_return', async () => {
    const { user, section, router, me } = await toConfiguration()
    await user.click(screen.getByRole('radio', { name: 'Strict' }))
    await user.type(screen.getByLabelText('Token budget (optional)'), '50000')

    await user.click(within(section).getByRole('link', { name: 'Add a connection', description: /Anthropic/ }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/connections')
    })
    const saved = JSON.parse(sessionStorage.getItem(WIZARD_DRAFT_KEY) ?? '{}') as { values?: Record<string, unknown>; step?: number }
    expect(saved.step).toBe(5)
    expect(saved.values).toMatchObject({ name: 'Inventory tracker', approvalMode: 'strict', llmProvider: 'anthropic' })

    // The user adds a key on Connections, then goes back.
    const credential = seedCredential({ userId: me.id, provider: 'anthropic', label: 'Work key', apiKey: 'sk-ant-new-a1b2' })
    await user.click(await screen.findByRole('link', { name: 'Back to the new project' }))

    await stepHeading(/Step 6 of 8: Configuration/)
    expect(sessionStorage.getItem(WIZARD_DRAFT_KEY)).toBeNull()
    expect(screen.getByRole('radio', { name: 'Strict' })).toBeChecked()
    expect(screen.getByLabelText('Token budget (optional)')).toHaveValue(50000)
    const restored = await screen.findByRole('region', { name: 'AI model' })
    const anthropic = await within(restored).findByRole('radio', { name: 'Anthropic' })
    await waitFor(() => {
      expect(anthropic).toBeChecked()
    })
    expect(within(restored).getByLabelText('Anthropic connection')).toHaveValue(credential.id)
    expect(within(restored).getByLabelText('Default model')).toHaveValue('claude-opus-5-5')

    await user.click(screen.getByRole('button', { name: 'Back' }))
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await user.click(screen.getByRole('button', { name: 'Back' }))
    expect(await screen.findByLabelText('Project name')).toHaveValue('Inventory tracker')
  })

  it('FR159_server_LLM_PROVIDER_UNAVAILABLE_returns_to_configuration_with_the_copy', async () => {
    server.use(
      http.post('*/api/projects', () => problem(400, 'LLM_PROVIDER_UNAVAILABLE', 'The provider is turned off.')),
    )
    const { user } = await toConfiguration()
    await createFromConfiguration(user)

    expect(await stepHeading(/Step 6 of 8: Configuration/)).toBeInTheDocument()
    expect(await screen.findAllByText(errorMessages.LLM_PROVIDER_UNAVAILABLE)).not.toHaveLength(0)
  })

  it('FR159_provider_error_target_is_the_first_usable_radio_or_the_legend_when_none_is_usable', async () => {
    const { section } = await toConfiguration()
    // Anthropic and OpenAI need a connection: the target is Ollama, the first usable radio.
    expect(document.getElementById('project-llm-provider')).toBe(within(section).getByRole('radio', { name: 'Ollama' }))
  })

  it('FR159_provider_error_links_to_the_legend_when_no_provider_is_usable', async () => {
    disableLlmProviders('ollama', 'fake')
    const { user, section } = await toConfiguration()
    await next(user)
    const link = await screen.findByRole('link', { name: 'Choose an AI provider you can use, or add a connection for one.' })
    expect(link).toHaveAttribute('href', '#project-llm-provider')
    const target = document.getElementById('project-llm-provider')
    expect(target?.tagName).toBe('LEGEND')
    expect(target).toHaveAttribute('tabindex', '-1')
    expect(within(section).getByRole('group', { name: 'Provider' })).toContainElement(target)
  })

  it('FR159_credential_not_owner_with_an_llm_field_key_goes_to_the_connection', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(403, 'CREDENTIAL_NOT_OWNER', 'Not yours.', { errors: { 'llm.credentialId': ['Not yours.'] } }),
      ),
    )
    const { user } = await toConfiguration((userId) => {
      seedCredential({ userId, provider: 'anthropic', label: 'Work key', apiKey: 'sk-ant-new-a1b2' })
    })
    await createFromConfiguration(user)
    expect(await stepHeading(/Step 6 of 8: Configuration/)).toBeInTheDocument()
    expect(screen.getByLabelText('Anthropic connection')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR159_credential_not_owner_with_a_deployment_field_key_goes_to_deployment', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(403, 'CREDENTIAL_NOT_OWNER', 'Not yours.', { errors: { 'deployment.connectionId': ['Not yours.'] } }),
      ),
    )
    const { user } = await toConfiguration((userId) => {
      seedCredential({ userId, provider: 'anthropic', label: 'Work key', apiKey: 'sk-ant-new-a1b2' })
    })
    await createFromConfiguration(user)
    expect(await stepHeading(/Step 7 of 8: Deployment/)).toBeInTheDocument()
  })

  it('FR159_server_per_agent_model_errors_land_on_that_agent_field', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(400, 'VALIDATION', 'Invalid', { errors: { 'llm.modelsByAgent.codeGenerator': ['Unknown model id.'] } }),
      ),
    )
    const { user } = await toConfiguration()
    await createFromConfiguration(user)
    expect(await stepHeading(/Step 6 of 8: Configuration/)).toBeInTheDocument()
    const section = await screen.findByRole('region', { name: 'AI model' })
    await within(section).findByLabelText('Code generator')
    expect(within(section).getByText('Advanced: model per agent (optional)').closest('details')).toHaveAttribute('open')
    expect(screen.getByLabelText('Code generator')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('Idea refinement')).not.toHaveAttribute('aria-invalid')
  })
})
