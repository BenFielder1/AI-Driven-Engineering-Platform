import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'

import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'
import { changelog } from '../content/changelog'
import { faqs } from '../content/faq'
import { bentoTiles } from '../content/home'
import { productSections } from '../content/product'
import { selfHostPaths } from '../content/self-host-commands'

/** Home's self-host block shows the Docker Desktop Kubernetes install (a path that installs something). */
function kubernetesCommands() {
  return selfHostPaths.find((p) => p.id === 'kubernetes')?.commands ?? []
}

afterEach(() => {
  setTheme('light')
})

async function renderHome() {
  const result = await renderRoute('/')
  await screen.findByRole('heading', { level: 1 })
  return result
}

const HERO = 'Ship software with AI agents, under human control.'

describe('FR-212 Home', () => {
  it('FR212_sections_in_order: hero, works with, lifecycle, why, bento, self-host, latest changes, FAQ, closing CTA', async () => {
    await renderHome()
    const ids = ['home-hero', 'works-with', 'home-lifecycle', 'home-why', 'home-bento', 'home-self-host', 'home-changelog', 'home-faq', 'home-cta']
    const sections = ids.map((id) => screen.getByTestId(id))
    for (let i = 1; i < sections.length; i++) {
      const prev = sections[i - 1]
      const next = sections[i]
      expect(prev && next && prev.compareDocumentPosition(next) & Node.DOCUMENT_POSITION_FOLLOWING, ids[i]).toBeTruthy()
    }
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it('FR212_hero: the h1, a subhead naming the phases, gates, artifacts and rollback, Get started and Self-host', async () => {
    await renderHome()
    const hero = within(screen.getByTestId('home-hero'))

    expect(hero.getByRole('heading', { level: 1, name: HERO })).toBeInTheDocument()
    const subhead = screen.getByTestId('home-hero').querySelector('p')?.textContent ?? ''
    for (const word of ['refinement', 'specification', 'planning', 'execution', 'review', 'release', 'approval gates', 'versioned artifacts', 'rollback']) {
      expect(subhead).toContain(word)
    }
    expect(hero.getByRole('link', { name: 'Get started' })).toHaveAttribute('href', '/register')
    expect(hero.getByRole('link', { name: 'Self-host' })).toHaveAttribute('href', '/self-host')
  })

  it('FR212_FR220_hero_preview: the project preview is aria-hidden with a visible caption and shows the rail, a waiting gate and a diff', async () => {
    await renderHome()
    const preview = within(screen.getByTestId('home-hero')).getByTestId('product-preview')
    const drawing = preview.querySelector('[aria-hidden="true"]')

    expect(drawing).not.toBeNull()
    expect(drawing).toHaveAttribute('inert')
    expect(drawing?.querySelectorAll('a, button, input, [tabindex]')).toHaveLength(0)
    expect(drawing).toHaveTextContent('Specification')
    expect(drawing).toHaveTextContent('Awaiting approval')
    expect(drawing?.querySelector('[data-change="added"]')).not.toBeNull()
    expect(within(preview).getByText(/^Preview: a project in Specification/)).toBeVisible()
  })

  it('FR212_works_with: the 14 names, labelled "Works with"', async () => {
    await renderHome()
    const strip = within(screen.getByTestId('works-with'))

    expect(strip.getByRole('heading', { name: 'Works with' })).toBeInTheDocument()
    expect(strip.getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'GitHub',
      'GitLab',
      'Bitbucket',
      'Jira',
      'Linear',
      'Railway',
      'Render',
      'Vercel',
      'Netlify',
      'Cloudflare',
      'Supabase',
      'Anthropic',
      'OpenAI',
      'Ollama',
    ])
  })

  it('FR212_lifecycle: six numbered phases with a gate between each, and the optional Analysis', async () => {
    await renderHome()
    const section = within(screen.getByTestId('home-lifecycle'))

    expect(section.getAllByTestId('lifecycle-gate')).toHaveLength(5)
    for (const phase of ['Refinement', 'Specification', 'Planning', 'Execution', 'Review', 'Release']) {
      expect(section.getByText(phase)).toBeInTheDocument()
    }
    expect(section.getByText(/^Optional first step: Analysis/)).toBeInTheDocument()
  })

  it('FR212_why: three cards, Governed, Versioned, Reversible', async () => {
    await renderHome()

    expect(within(screen.getByTestId('home-why')).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual([
      'Governed',
      'Versioned',
      'Reversible',
    ])
  })

  it('FR212_bento_anchors: six tiles, each linking to a Product section that exists', async () => {
    await renderHome()
    const tiles = within(screen.getByTestId('home-bento')).getAllByTestId('bento-tile')
    const anchors = new Set(productSections.map((s) => s.id))

    expect(tiles).toHaveLength(6)
    tiles.forEach((tile, i) => {
      const href = tile.getAttribute('href') ?? ''
      expect(href).toBe(`/product#${bentoTiles[i]?.anchor ?? ''}`)
      expect(anchors.has(href.split('#')[1] ?? '')).toBe(true)
    })
  })

  it('FR212_self_host: requirements and a copyable command block from the shared source, linking to /self-host', async () => {
    await renderHome()
    const section = within(screen.getByTestId('home-self-host'))

    expect(section.getByText('PostgreSQL 18')).toBeInTheDocument()
    const block = section.getByTestId('command-block')
    for (const c of kubernetesCommands()) {
      expect(block).toHaveTextContent(c.command)
    }
    expect(section.getByRole('button', { name: /^Copy the/ })).toBeInTheDocument()
    expect(section.getByRole('link', { name: /All three ways to self-host/ })).toHaveAttribute('href', '/self-host')
  })

  it('FR212_latest_changes: the two newest Changelog entries', async () => {
    await renderHome()
    const section = within(screen.getByTestId('home-changelog'))

    expect(section.getAllByTestId('changelog-entry')).toHaveLength(2)
    expect(section.getAllByRole('heading', { level: 3 }).map((h) => h.id)).toEqual(changelog.slice(0, 2).map((e) => `changelog-${e.version}`))
    expect(section.getByRole('link', { name: /Full changelog/ })).toHaveAttribute('href', '/changelog')
  })

  it('FR212_faq: the first five questions, linking to /faq', async () => {
    await renderHome()
    const section = within(screen.getByTestId('home-faq'))

    expect(section.getAllByRole('button').map((b) => b.textContent)).toEqual(faqs.slice(0, 5).map((f) => f.question))
    expect(section.getByRole('link', { name: /All questions/ })).toHaveAttribute('href', '/faq')
  })

  it('FR212_closing_cta: repeats Get started and Self-host', async () => {
    await renderHome()
    const cta = within(screen.getByTestId('home-cta'))

    expect(cta.getByRole('link', { name: 'Get started' })).toHaveAttribute('href', '/register')
    expect(cta.getByRole('link', { name: 'Self-host' })).toHaveAttribute('href', '/self-host')
  })

  it('FR216_copy: the copy button writes the commands to the clipboard and announces it', async () => {
    const user = userEvent.setup()
    await renderHome()
    const section = within(screen.getByTestId('home-self-host'))

    await user.click(section.getByRole('button', { name: /^Copy the/ }))

    expect(await section.findByRole('status')).toHaveTextContent('Copied')
    expect(await navigator.clipboard.readText()).toBe(kubernetesCommands().map((c) => c.command).join('\n'))
  })

  it.each(THEMES.map((t) => [t]))('FR212_axe: / has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderHome()

    expect(await axeViolations(container)).toEqual([])
  })
})
