import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'
import { bentoTiles } from '../content/home'
import { productSections } from '../content/product'

afterEach(() => {
  setTheme('light')
})

async function renderProduct() {
  const result = await renderRoute('/product')
  await screen.findByRole('heading', { level: 1, name: 'Everything Tevrin does, from idea to release' })
  return result
}

/** One sentence per `.`/`?` followed by a space or the end (good enough for this copy, which has no abbreviations). */
const sentences = (text: string) => text.split(/[.?!](?:\s|$)/).filter((s) => s.trim() !== '').length

describe('FR-213 Product', () => {
  it('FR213_sections: one anchored section per feature area, in order, each with an h2', async () => {
    await renderProduct()
    const sections = screen.getAllByTestId('product-section')

    expect(sections.map((s) => s.id)).toEqual([
      'lifecycle-and-gates',
      'artifacts',
      'agents',
      'git',
      'issue-trackers',
      'stacks',
      'deployments',
      'notifications',
      'admin',
      'cli-and-api',
    ])
    sections.forEach((section, i) => {
      expect(within(section).getByRole('heading', { level: 2 })).toHaveTextContent(productSections[i]?.title ?? '')
    })
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it('FR213_content_shape: 2–4 sentences, 3–5 bullets and a preview or an icon per section', () => {
    for (const section of productSections) {
      const n = sentences(section.body.join(' '))
      expect(n, section.id).toBeGreaterThanOrEqual(2)
      expect(n, section.id).toBeLessThanOrEqual(4)
      expect(section.bullets.length, section.id).toBeGreaterThanOrEqual(3)
      expect(section.bullets.length, section.id).toBeLessThanOrEqual(5)
      expect(section.visual).toBeDefined()
    }
  })

  it('FR212_FR213_bento_anchors_exist: every Home bento link targets an element on /product', async () => {
    await renderProduct()

    for (const tile of bentoTiles) {
      expect(document.getElementById(tile.anchor), tile.anchor).not.toBeNull()
    }
  })

  it('FR213_on_this_page: the jump links match the section anchors', async () => {
    await renderProduct()
    const nav = within(screen.getByRole('navigation', { name: 'On this page' }))

    expect(nav.getAllByRole('link').map((a) => a.getAttribute('href'))).toEqual(productSections.map((s) => `#${s.id}`))
  })

  it('FR220_previews: previews are aria-hidden and inert with a visible caption', async () => {
    await renderProduct()
    const previews = screen.getAllByTestId('product-preview')

    expect(previews.length).toBeGreaterThanOrEqual(4)
    for (const preview of previews) {
      const drawing = preview.firstElementChild
      expect(drawing).toHaveAttribute('aria-hidden', 'true')
      expect(drawing).toHaveAttribute('inert')
      expect(within(preview).getByText(/^Preview:/)).toBeInTheDocument()
    }
  })

  it.each(THEMES.map((t) => [t]))('FR213_axe: /product has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderProduct()

    expect(await axeViolations(container)).toEqual([])
  })
})
