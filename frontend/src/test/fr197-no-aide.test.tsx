import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { signInAs } from '../mocks/handlers/auth'
import { seedProject } from '../mocks/handlers/projects'
import { renderRoute } from './render'

/**
 * FR-197: every top-level page names the product Tevrin. The old name ("AIDE") appears nowhere in the rendered DOM text,
 * aria-labels, alt text, titles or `document.title`. Internal identifiers (`aide.*` storage keys, cookies, `aide_pat_`)
 * aren't copy and stay (§4.29, FR-201).
 */

vi.mock('next/headers', () => ({
  headers: () => Promise.resolve(new Headers({ host: 'localhost:3000' })),
}))

const OLD_NAME = /AIDE/

const ATTRIBUTES = ['aria-label', 'aria-description', 'aria-roledescription', 'alt', 'title', 'placeholder']

/** Every place a person (or assistive technology) reads the product name. */
function visibleCopy(): string[] {
  const copy = [document.title, document.body.textContent]
  for (const element of Array.from(document.body.querySelectorAll('*'))) {
    for (const name of ATTRIBUTES) {
      const value = element.getAttribute(name)
      if (value) {
        copy.push(`${element.tagName.toLowerCase()}[${name}="${value}"]`)
      }
    }
  }
  return copy
}

function expectNoOldName() {
  const offending = visibleCopy().filter((text) => text.includes('AIDE'))
  expect(offending).toEqual([])
}

/** Waits for the page heading, then for the loading placeholders to settle. */
async function settle() {
  await screen.findByRole('heading', { level: 1 })
  await waitFor(() => {
    expect(screen.queryAllByText(/^Loading\b/)).toHaveLength(0)
  })
}

function expectTevrinTitle() {
  expect(document.title).not.toMatch(OLD_NAME)
  if (document.title !== '') {
    expect(document.title).toMatch(/ · Tevrin$/)
  }
}

beforeEach(() => {
  document.title = ''
})

describe('FR-197 no AIDE in the rendered app', () => {
  it.each(['/login', '/register'])('FR197_public_page %s says Tevrin', async (path) => {
    await renderRoute(path)
    await settle()

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Tevrin')
    expectTevrinTitle()
    expectNoOldName()
  })

  it.each([
    '/projects',
    '/projects/new',
    '/account',
    '/account/connections',
    '/connections',
    '/notifications',
    '/admin',
    '/admin/users',
    '/admin/runs',
    '/admin/events',
    '/admin/dead-letters',
    '/admin/integrations',
    '/approvals/unknown-token',
  ])('FR197_app_page %s has no AIDE', async (path) => {
    signInAs({ systemRole: 'admin' })
    await renderRoute(path)
    await settle()

    expectTevrinTitle()
    expectNoOldName()
  })

  it.each([
    '',
    '/activity',
    '/analysis',
    '/artifacts',
    '/refinement',
    '/specification',
    '/planning',
    '/execution',
    '/review',
    '/release',
    '/deployments',
    '/migrations',
    '/runs',
    '/settings',
    '/settings/git',
  ])('FR197_project_page /projects/:id%s has no AIDE', async (suffix) => {
    signInAs({ systemRole: 'admin' })
    const project = seedProject()
    await renderRoute(`/projects/${project.id}${suffix}`)
    await settle()

    expectTevrinTitle()
    expectNoOldName()
  })

  it('FR197_shell: the sidebar lockup, footer and navigation drawer say Tevrin', async () => {
    signInAs()
    await renderRoute('/projects')
    await settle()

    expect(screen.getByRole('link', { name: 'Tevrin home' })).toBeInTheDocument()
    expect(screen.getByRole('contentinfo')).toHaveTextContent('Tevrin')

    await userEvent.setup().click(screen.getByRole('button', { name: 'Open navigation' }))
    const drawer = await screen.findByRole('dialog')
    expect(within(drawer).getByText('Pages in Tevrin')).toBeInTheDocument()
    expectNoOldName()
  })

  it('FR197_metadata: the root title is Tevrin', async () => {
    const { generateMetadata } = await import('../app/layout')
    const metadata = await generateMetadata()

    expect(metadata.title).toBe('Tevrin')
    expect(JSON.stringify(metadata)).not.toMatch(OLD_NAME)
  })
})
