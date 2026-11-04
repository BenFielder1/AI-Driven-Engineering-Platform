import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedCredential } from '../../mocks/handlers/connections'
import { seedGitLabConnection } from '../../mocks/handlers/gitlab'
import { seedNotification } from '../../mocks/handlers/notifications'
import { problem } from '../../mocks/handlers/problem'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { currentHref, renderRoute } from '../../test/render'
import { CHECKLIST_DISMISSED_KEY } from './checklist-dismissed'

const checklistPanel = () => screen.findByTestId('setup-checklist')

describe('FR-207: Home', () => {
  it('FR207_home: greets the user, shows the checklist at 0/4 with item 1 highlighted, and the empty projects state', async () => {
    signInAs({ name: 'Ada Lovelace' })
    await renderRoute('/home')

    expect(await screen.findByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeInTheDocument()
    await waitFor(() => {
      expect(document.title).toBe('Home · Tevrin')
    })
    const panel = await checklistPanel()
    expect(await within(panel).findByTestId('checklist-progress')).toHaveTextContent('0/4 done')
    const items = within(panel).getAllByRole('listitem')
    expect(items.map((li) => li.querySelector('p')?.textContent)).toEqual([
      'Connect a Git host, next step',
      'Add an AI provider, not done',
      'Create or import a project, not done',
      'Connect a deployment platform (optional), not done',
    ])
    expect(items[0]).toHaveAttribute('data-highlighted', 'true')
    expect(within(panel).getByRole('link', { name: 'New project: Create or import a project' })).toHaveAttribute('href', '/projects/new')

    const recent = await screen.findByRole('region', { name: 'Recent projects' })
    expect(await within(recent).findByRole('link', { name: 'Create project' })).toHaveAttribute('href', '/projects/new')
    expect(within(recent).getByRole('link', { name: 'Import a repository' })).toHaveAttribute('href', '/projects/new?source=import')
  })

  it('FR207_home: Git, LLM and a project mark the checklist complete, "You\'re all set" can be dismissed and restored', async () => {
    const me = signInAs({ name: 'Ada' })
    seedGitLabConnection(me.id)
    seedCredential({ userId: me.id })
    seedProject({ name: 'Inventory tracker' })
    await renderRoute('/home')
    const user = userEvent.setup()

    const panel = await checklistPanel()
    expect(await within(panel).findByTestId('checklist-all-set')).toHaveTextContent("You're all set")
    expect(within(panel).getByTestId('checklist-progress')).toHaveTextContent('3/4 done')
    expect(within(panel).queryByRole('list')).not.toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Dismiss' }))
    expect(screen.queryByTestId('setup-checklist')).not.toBeInTheDocument()
    expect(localStorage.getItem(CHECKLIST_DISMISSED_KEY)).toBe('1')

    await user.click(screen.getByRole('button', { name: 'Show setup checklist' }))
    expect(await checklistPanel()).toBeInTheDocument()
    expect(localStorage.getItem(CHECKLIST_DISMISSED_KEY)).toBeNull()
  })

  it('FR207_home: a dismissed checklist stays hidden on the next visit', async () => {
    localStorage.setItem(CHECKLIST_DISMISSED_KEY, '1')
    signInAs({ name: 'Ada' })
    await renderRoute('/home')

    await screen.findByRole('heading', { level: 1, name: 'Welcome, Ada' })
    expect(screen.getByRole('button', { name: 'Show setup checklist' })).toBeInTheDocument()
    expect(screen.queryByTestId('setup-checklist')).not.toBeInTheDocument()
  })

  it('FR207_home: Recent projects lists up to 6 by last update with phase and status, and View all', async () => {
    signInAs()
    for (let i = 1; i <= 7; i++) {
      seedProject({ name: `Project ${String(i)}`, updatedAt: `2026-10-0${String(i)}T10:00:00Z` })
    }
    await renderRoute('/home')

    const recent = await screen.findByRole('region', { name: 'Recent projects' })
    const list = await within(recent).findByRole('list', { name: 'Recent projects' })
    const names = within(list)
      .getAllByRole('link')
      .map((a) => a.textContent)
    expect(names).toHaveLength(6)
    expect(names[0]).toBe('Project 7')
    expect(names).not.toContain('Project 1')
    expect(within(recent).getByRole('link', { name: 'View all projects' })).toHaveAttribute('href', '/projects')
  })

  it('FR207_home: Unread notifications shows up to 5 unread, newest first, and View all', async () => {
    signInAs()
    seedNotification({ title: 'Already read', readAt: '2026-10-01T10:00:00Z' })
    for (let i = 1; i <= 6; i++) {
      seedNotification({ title: `Unread ${String(i)}` })
    }
    const { router } = await renderRoute('/home')
    const user = userEvent.setup()

    const panel = await screen.findByRole('region', { name: 'Unread notifications' })
    const list = await within(panel).findByRole('list', { name: 'Unread notifications' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(5)
    expect(within(list).queryByText('Already read')).not.toBeInTheDocument()
    await user.click(within(panel).getByRole('link', { name: 'View all notifications' }))
    await waitFor(() => {
      expect(currentHref(router)).toBe('/notifications?unread=true')
    })
  })

  it('FR207_home: a failing panel shows its own error with Try again; the others still render', async () => {
    signInAs({ name: 'Ada' })
    server.use(http.get('*/api/me/notifications', () => problem(403, 'FORBIDDEN', 'Not allowed.')))
    await renderRoute('/home')

    const panel = await screen.findByRole('region', { name: 'Unread notifications' })
    expect(await within(panel).findByRole('button', { name: 'Try again' })).toBeInTheDocument()
    expect(await within(await screen.findByRole('region', { name: 'Recent projects' })).findByText('No projects yet')).toBeInTheDocument()
  })

  it.each(THEMES)('FR207_a11y: Home has a single h1 and no axe violations (%s theme)', async (theme) => {
    setTheme(theme)
    signInAs({ name: 'Ada' })
    const { container } = await renderRoute('/home')
    await within(await checklistPanel()).findByTestId('checklist-progress')
    await within(await screen.findByRole('region', { name: 'Recent projects' })).findByText('No projects yet')

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(await axeViolations(container)).toEqual([])
  })
})
