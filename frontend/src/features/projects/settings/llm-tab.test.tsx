import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { projectsDb } from '../../../mocks/handlers/projects'
import { openOwnerSettings } from '../../../test/project-settings'

async function openLlm(hasApiKey = false) {
  const utils = await openOwnerSettings('llm', {
    llm: { provider: 'anthropic', defaultModel: 'claude-opus-5-5', modelsByAgent: {}, hasApiKey },
  })
  await screen.findByRole('heading', { name: 'LLM' })
  return utils
}

describe('Settings → LLM (FR-9)', () => {
  it('FR-9: providers without a system key are marked "not configured" and can\'t be picked without a project key', async () => {
    projectsDb.providers = [
      { provider: 'anthropic', configured: true, defaultModel: 'claude-opus-5-5' },
      { provider: 'openai', configured: false, defaultModel: 'gpt-6-astra' },
      { provider: 'ollama', configured: false, defaultModel: 'qwen2.5-coder' },
      { provider: 'fake', configured: true, defaultModel: 'fake-1' },
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
      { provider: 'anthropic', configured: true, defaultModel: 'claude-opus-5-5' },
      { provider: 'openai', configured: false, defaultModel: 'gpt-6-astra' },
      { provider: 'ollama', configured: true, defaultModel: 'qwen2.5-coder' },
      { provider: 'fake', configured: true, defaultModel: 'fake-1' },
    ]
    const { user } = await openLlm()

    await user.type(screen.getByLabelText('Project API key (optional)'), 'sk-test-key')
    const openai = within(screen.getByLabelText('Provider')).getByRole('option', { name: 'OpenAI (not configured)' })
    expect(openai).toBeEnabled()
    await user.selectOptions(screen.getByLabelText('Provider'), 'openai')

    expect(screen.getByText(/OpenAI isn.t configured on this server\. The project API key will be used\./)).toBeInTheDocument()
    expect(screen.getByLabelText('Default model')).toHaveValue('gpt-6-astra')
  })

  it('FR-22: choosing Ollama fills its system default model (qwen2.5-coder) and saves provider and model', async () => {
    const { user, project } = await openLlm()

    expect(screen.getByLabelText('Default model')).toHaveValue('claude-opus-5-5')
    expect(screen.getByLabelText('Default model')).toHaveAccessibleDescription(
      'The system default for Anthropic is claude-opus-5-5.',
    )
    await user.selectOptions(screen.getByLabelText('Provider'), 'ollama')
    expect(screen.getByLabelText('Default model')).toHaveValue('qwen2.5-coder')
    await user.type(screen.getByLabelText('Code generator'), 'qwen2.5-coder:32b')
    await user.click(screen.getByRole('button', { name: 'Save LLM settings' }))

    expect(await screen.findByText('LLM settings saved.')).toBeInTheDocument()
    expect(project.llm).toMatchObject({
      provider: 'ollama',
      defaultModel: 'qwen2.5-coder',
      modelsByAgent: { codeGenerator: 'qwen2.5-coder:32b' },
    })
  })
})
