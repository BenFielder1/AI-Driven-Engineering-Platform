import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { projectsDb } from '../../../mocks/handlers/projects'
import { openOwnerSettings } from '../../../test/project-settings'

async function openLlm(hasApiKey = false) {
  const utils = await openOwnerSettings('llm', {
    llm: { provider: 'anthropic', defaultModel: 'claude-sonnet-4-5', modelsByAgent: {}, hasApiKey },
  })
  await screen.findByRole('heading', { name: 'LLM' })
  return utils
}

describe('Settings → LLM (FR-9)', () => {
  it('FR-9: providers without a system key are marked "not configured" and can\'t be picked without a project key', async () => {
    projectsDb.providers = [
      { provider: 'anthropic', configured: true, defaultModel: 'claude-sonnet-4-5' },
      { provider: 'openai', configured: false, defaultModel: 'gpt-4.1' },
      { provider: 'ollama', configured: false, defaultModel: 'llama3.1' },
      { provider: 'fake', configured: true, defaultModel: 'fake-model' },
    ]
    await openLlm()

    const select = screen.getByLabelText('Provider')
    const options = within(select).getAllByRole('option')
    expect(options.map((o) => o.textContent)).toEqual([
      'Anthropic',
      'OpenAI (not configured)',
      'Ollama (not configured)',
      'Fake (testing)',
    ])
    expect(within(select).getByRole('option', { name: 'OpenAI (not configured)' })).toBeDisabled()
    expect(within(select).getByRole('option', { name: 'Anthropic' })).toBeEnabled()
    expect(select).toHaveAccessibleDescription(expect.stringContaining('not configured') as string)
  })

  it('FR-9: entering a project API key makes an unconfigured provider selectable', async () => {
    projectsDb.providers = [
      { provider: 'anthropic', configured: true, defaultModel: 'claude-sonnet-4-5' },
      { provider: 'openai', configured: false, defaultModel: 'gpt-4.1' },
      { provider: 'ollama', configured: true, defaultModel: 'llama3.1' },
      { provider: 'fake', configured: true, defaultModel: 'fake-model' },
    ]
    const { user } = await openLlm()

    await user.type(screen.getByLabelText('Project API key (optional)'), 'sk-test-key')
    const openai = within(screen.getByLabelText('Provider')).getByRole('option', { name: 'OpenAI (not configured)' })
    expect(openai).toBeEnabled()
    await user.selectOptions(screen.getByLabelText('Provider'), 'openai')

    expect(screen.getByText(/OpenAI isn.t configured on this server\. The project API key will be used\./)).toBeInTheDocument()
    expect(screen.getByLabelText('Default model')).toHaveValue('gpt-4.1')
  })
})
