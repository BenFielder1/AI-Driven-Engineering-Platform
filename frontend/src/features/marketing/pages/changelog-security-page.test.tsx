import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { DOCS_URL } from '../../../lib/site'
import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'
import { changelog } from '../content/changelog'
import { securityFacts } from '../content/security'

/** The `i`th element, or a failing test. */
function nth(list: readonly HTMLElement[], i: number): HTMLElement {
  const el = list[i]
  if (!el) {
    throw new Error(`no element ${String(i)}`)
  }
  return el
}

afterEach(() => {
  setTheme('light')
})

describe('FR-219 Changelog', () => {
  it('FR219_order_and_count: v1–v6, newest first, with the PR merge dates and v6 in preview', () => {
    expect(changelog.map((e) => e.version)).toEqual(['v6', 'v5', 'v4', 'v3', 'v2', 'v1'])
    // Merge dates of PRs #28, #27, #23, #13 and #1 (gh pr view <n> --json mergedAt).
    expect(changelog.map((e) => e.date)).toEqual([null, '2026-11-04', '2026-11-01', '2026-10-24', '2026-10-22', '2026-10-10'])
    for (const entry of changelog) {
      expect(entry.highlights.length, entry.version).toBeGreaterThanOrEqual(3)
      expect(entry.highlights.length, entry.version).toBeLessThanOrEqual(6)
      expect(entry.asAide, entry.version).toBe(entry.version !== 'v6')
      expect(entry.specHref).toBe(`${DOCS_URL}history/${entry.version}/`)
    }
  })

  it('FR219_page: one entry per version with date, title, "(as AIDE)" for v1–v5 and a link to its docs page', async () => {
    await renderRoute('/changelog')
    await screen.findByRole('heading', { level: 1, name: 'Changelog' })
    const entries = screen.getAllByTestId('changelog-entry')

    expect(entries).toHaveLength(6)
    expect(within(nth(entries, 0)).getByText('In preview')).toBeInTheDocument()
    expect(within(nth(entries, 0)).getByRole('heading', { level: 2 })).toHaveTextContent(/^Tevrin$/)
    expect(within(nth(entries, 1)).getByRole('heading', { level: 2 })).toHaveTextContent('(as AIDE)')
    expect(within(nth(entries, 1)).getByText('4 Nov 2026')).toHaveAttribute('datetime', '2026-11-04')
    expect(within(nth(entries, 5)).getByRole('link', { name: /^v1 spec and notes/ })).toHaveAttribute('href', `${DOCS_URL}history/v1/`)
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it.each(THEMES.map((t) => [t]))('FR219_axe: /changelog has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderRoute('/changelog')
    await screen.findByRole('heading', { level: 1 })

    expect(await axeViolations(container)).toEqual([])
  })
})

describe('FR-217 Security', () => {
  it('FR217_facts: the spec’s statements, each with a docs link', async () => {
    await renderRoute('/security')
    await screen.findByRole('heading', { level: 1, name: 'Security at Tevrin' })
    const facts = screen.getAllByTestId('security-fact')

    expect(facts.map((f) => f.id)).toEqual([
      'secrets',
      'personal-connections',
      'github-tokens',
      'repo-tools',
      'artifacts',
      'audit',
      'headers',
      'no-third-party',
      'tests',
    ])
    for (const fact of facts) {
      const link = within(fact).getByRole('link')
      expect(link.getAttribute('href')).toMatch(new RegExp(`^${DOCS_URL}`))
      expect(link).toHaveAttribute('target', '_blank')
    }
    expect(securityFacts).toHaveLength(facts.length)
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it.each(THEMES.map((t) => [t]))('FR217_axe: /security has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderRoute('/security')
    await screen.findByRole('heading', { level: 1 })

    expect(await axeViolations(container)).toEqual([])
  })
})
