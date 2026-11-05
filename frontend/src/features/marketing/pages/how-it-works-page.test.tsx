import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'

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
})

async function renderPage() {
  const result = await renderRoute('/how-it-works')
  await screen.findByRole('heading', { level: 1, name: 'How Tevrin takes an idea to a release' })
  return result
}

describe('FR-214 How it works', () => {
  it('FR214_sections: phases, approval modes, review cycles, versions and rollback, each an anchored h2', async () => {
    await renderPage()

    for (const [id, title] of [
      ['phases', 'Phase by phase'],
      ['approval-modes', 'Approval modes'],
      ['review-cycles', 'Review cycles'],
      ['versions', 'Release versions and checkpoints'],
      ['rollback', 'Rollback and invalidation'],
    ] as const) {
      expect(within(byId(id)).getByRole('heading', { level: 2 })).toHaveTextContent(title)
    }
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it('FR214_phases: each phase (with optional Analysis) states its input, agent, artifact and gate', async () => {
    await renderPage()
    const cards = screen.getAllByTestId('phase-card')

    expect(cards.map((c) => c.id)).toEqual(['analysis', 'refinement', 'specification', 'planning', 'execution', 'review', 'release'])
    for (const card of cards) {
      expect(within(card).getAllByRole('term').map((t) => t.textContent)).toEqual(['Input', 'Agent', 'Artifact', 'Gate'])
    }
  })

  it('FR214_approval_modes: strict, advisory and autonomous, with the gates each adds', async () => {
    await renderPage()
    const table = within(screen.getByRole('table', { name: 'Which gates each approval mode adds' }))
    const rows = table.getAllByRole('row').slice(1)

    expect(rows.map((r) => within(r).getByRole('rowheader').firstChild?.textContent)).toEqual(['Strict', 'Advisory', 'Autonomous'])
    expect(rows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([
      ['Gate', 'Gate', 'Gate'],
      ['Gate', 'No gate', 'Gate'],
      ['No gate', 'No gate', 'Gate'],
    ])
  })

  it('FR214_S8_modes_table_375: the table scrolls inside a focusable, labelled region that also contains its sr-only text', async () => {
    await renderPage()
    const scroller = screen.getByRole('region', { name: 'Which gates each approval mode adds' })

    expect(scroller).toBe(screen.getByTestId('modes-table-scroller'))
    expect(scroller).toHaveAttribute('tabindex', '0')
    // overflow-x-auto scrolls the table; `relative` is the containing block of the absolutely positioned sr-only cells,
    // which otherwise escape the scroller and widen the page at 375 px.
    expect(scroller).toHaveClass('overflow-x-auto', 'relative')
    expect(within(scroller).getByRole('table')).toBeInTheDocument()
  })

  it('FR214_diagram: an aria-hidden SVG with a text equivalent; its animation is motion-safe only', async () => {
    await renderPage()
    const figure = screen.getByTestId('lifecycle-diagram')
    const svgs = Array.from(figure.querySelectorAll('svg'))

    // A horizontal rail on wide screens and a vertical one on narrow screens; CSS shows exactly one.
    expect(svgs.map((svg) => svg.dataset.layout)).toEqual(['horizontal', 'vertical'])
    expect(svgs[0]).toHaveClass('hidden', 'lg:block')
    expect(svgs[1]).toHaveClass('lg:hidden')
    for (const svg of svgs) {
      expect(svg).toHaveAttribute('aria-hidden', 'true')
      // Six phases and five gates in each.
      expect(svg.querySelectorAll('[data-animated]')).toHaveLength(11)
    }
    expect(within(figure).getByText(/The Tevrin lifecycle: Refinement, Specification, Planning, Execution, Review, Release/)).toBeInTheDocument()
    const animated = Array.from(figure.querySelectorAll('[data-animated]'))
    for (const el of animated) {
      const classes = (el.getAttribute('class') ?? '').split(/\s+/)
      expect(classes.filter((c) => c.includes('animate-'))).toEqual(['motion-safe:animate-pulse'])
    }
  })

  it.each(THEMES.map((t) => [t]))('FR214_axe: /how-it-works has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderPage()

    expect(await axeViolations(container)).toEqual([])
  })
})
