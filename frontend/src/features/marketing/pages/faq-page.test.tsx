import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'

import { DOCS_URL } from '../../../lib/site'
import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'
import { faqs } from '../content/faq'
import { GITHUB_URL } from '../content/site-nav'

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

async function renderPage() {
  const result = await renderRoute('/faq')
  await screen.findByRole('heading', { level: 1, name: 'Frequently asked questions' })
  return result
}

const REQUIRED = [
  'What is Tevrin?',
  'Who is it for?',
  'How do approval gates work?',
  'Can I undo what an agent did?',
  'Which AI models can I use, and where do my keys live?',
  'Does my code leave my infrastructure?',
  'Does it work with an existing repository?',
  'Which Git hosts, trackers and platforms are supported?',
  'Is it free?',
  'How do I self-host it?',
  'Is there a CLI or API?',
  'Was it called AIDE?',
]

describe('FR-218 FAQ', () => {
  it('FR218_questions: at least the twelve questions from the spec, each a button in a heading', async () => {
    await renderPage()

    const questions = screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)
    for (const q of REQUIRED) {
      expect(questions).toContain(q)
    }
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it('FR218_accordion: button with aria-expanded and aria-controls; the region is hidden until opened', async () => {
    const user = userEvent.setup()
    await renderPage()
    const button = screen.getByRole('button', { name: 'Is it free?' })
    const panel = document.getElementById(button.getAttribute('aria-controls') ?? '')

    expect(button).toHaveAttribute('aria-expanded', 'false')
    expect(panel).not.toBeVisible()

    await user.click(button)

    expect(button).toHaveAttribute('aria-expanded', 'true')
    const region = screen.getByRole('region', { name: 'Is it free?' })
    expect(region).toBe(panel)
    expect(region).toHaveTextContent(/free while in preview, and there is no billing/)
    expect(region).toHaveTextContent('source is available on GitHub')
  })

  it('FR218_keyboard: on a focused question, Enter and Space toggle it, arrows, Home and End move between questions', async () => {
    const user = userEvent.setup()
    await renderPage()
    const buttons = within(screen.getByTestId('faq-accordion')).getAllByRole('button')
    const first = nth(buttons, 0)
    const second = nth(buttons, 1)
    const last = nth(buttons, buttons.length - 1)

    first.focus()
    await user.keyboard('{Enter}')
    expect(first).toHaveAttribute('aria-expanded', 'true')
    await user.keyboard(' ')
    expect(first).toHaveAttribute('aria-expanded', 'false')

    await user.keyboard('{ArrowDown}')
    expect(second).toHaveFocus()
    await user.keyboard('{End}')
    expect(last).toHaveFocus()
    await user.keyboard('{ArrowDown}')
    expect(first).toHaveFocus()
    await user.keyboard('{ArrowUp}')
    expect(last).toHaveFocus()
    await user.keyboard('{Home}')
    expect(first).toHaveFocus()
  })

  it('FR218_still_have_questions: links to the docs and GitHub issues', async () => {
    await renderPage()
    const more = within(screen.getByTestId('faq-more'))

    expect(more.getByRole('heading', { name: 'Still have questions?' })).toBeInTheDocument()
    expect(more.getByRole('link', { name: /^Read the docs/ })).toHaveAttribute('href', DOCS_URL)
    expect(more.getByRole('link', { name: /^Ask on GitHub issues/ })).toHaveAttribute('href', `${GITHUB_URL}/issues`)
  })

  it('FR218_Q3_wording: the licence answer says "source available on GitHub", and no answer says "open source"', () => {
    const all = faqs.map((f) => [f.question, ...f.answer].join(' ')).join(' ')

    expect(all).not.toMatch(/open[\s-]?source/i)
    expect(faqs.find((f) => f.id === 'free')?.answer.join(' ')).toMatch(/source is available on GitHub/)
  })

  it.each(THEMES.map((t) => [t]))('FR218_axe: /faq has no axe violations, closed and open (%s)', async (theme) => {
    setTheme(theme)
    const user = userEvent.setup()
    const { container } = await renderPage()

    expect(await axeViolations(container)).toEqual([])
    await user.click(screen.getByRole('button', { name: 'What is Tevrin?' }))
    expect(await axeViolations(container)).toEqual([])
  })
})
