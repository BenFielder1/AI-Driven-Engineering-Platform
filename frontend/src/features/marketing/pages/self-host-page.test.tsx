import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'
import { selfHostPaths } from '../content/self-host-commands'

/** The element with `id`, or a failing test. */
function byId(id: string): HTMLElement {
  const el = document.getElementById(id)
  if (!el) {
    throw new Error(`no #${id}`)
  }
  return el
}

afterEach(() => {
  setTheme('light')
  vi.restoreAllMocks()
})

async function renderPage() {
  const result = await renderRoute('/self-host')
  await screen.findByRole('heading', { level: 1, name: 'Run Tevrin on your own infrastructure' })
  return result
}

describe('FR-216 Self-host', () => {
  it('FR216_preview_and_requirements: "Free while in preview. No billing." and Docker, PostgreSQL 18, an LLM key or Ollama', async () => {
    await renderPage()

    expect(screen.getByTestId('preview-note')).toHaveTextContent('Free while in preview. No billing.')
    expect(within(screen.getByTestId('requirements')).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Docker',
      'PostgreSQL 18',
      'An LLM provider key, or Ollama (not on Railway)',
    ])
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it('FR216_FR222_paths: Railway, Docker Desktop Kubernetes and the dev loop, with the shared commands and a docs link', async () => {
    await renderPage()

    for (const path of selfHostPaths) {
      const card = byId(path.id)
      expect(within(card).getByRole('heading', { level: 3 })).toHaveTextContent(path.title)
      const block = within(card).getByTestId('command-block')
      for (const c of path.commands) {
        expect(block).toHaveTextContent(c.command)
      }
      expect(within(card).getByRole('link', { name: new RegExp(`^${path.title} docs`) })).toHaveAttribute('href', path.docsHref)
    }
    expect(selfHostPaths.map((p) => p.id)).toEqual(['railway', 'kubernetes', 'dev-loop'])
  })

  it('FR216_configure: Git sign-in, GitHub App, providers and notifications', async () => {
    await renderPage()
    const section = within(byId('configure'))

    expect(section.getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual([
      'Git sign-in',
      'GitHub App',
      'LLM and platform providers',
      'Notifications',
    ])
  })

  it('FR216_faq: questions on upgrades and data open as accordion panels', async () => {
    const user = userEvent.setup()
    await renderPage()
    const button = screen.getByRole('button', { name: 'How do I upgrade?' })

    await user.click(button)

    expect(button).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('region', { name: 'How do I upgrade?' })).toHaveTextContent('migrations')
    expect(screen.getByRole('button', { name: 'Where is my data, and what should I back up?' })).toBeInTheDocument()
  })

  it('FR216_copy_feedback: each block copies its commands; a clipboard failure is announced too', async () => {
    const user = userEvent.setup()
    await renderPage()
    const card = within(byId('kubernetes'))

    await user.click(card.getByRole('button', { name: 'Copy the Docker Desktop Kubernetes commands' }))

    expect(await card.findByRole('status')).toHaveTextContent('Copied')
    expect(await navigator.clipboard.readText()).toBe(selfHostPaths[1]?.commands.map((c) => c.command).join('\n'))

    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'))
    const dev = within(byId('dev-loop'))
    await user.click(dev.getByRole('button', { name: 'Copy the Dev loop commands' }))

    expect(await dev.findByText(/Couldn.t copy/)).toBeInTheDocument()
  })

  it.each(THEMES.map((t) => [t]))('FR216_axe: /self-host has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderPage()

    expect(await axeViolations(container)).toEqual([])
  })
})
