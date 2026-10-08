import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { currentHref, renderRoute } from '../../test/render'

function signInAsUser() {
  seedUser({ email: 'admin@example.com', name: 'Admin' }) // the first user is the Admin
  return signInAs({ email: 'ada@example.com', systemRole: 'user' })
}

describe('Projects list (FR-6)', () => {
  it('FR-6: shows only the projects the user is a member of, with phase, status, version and role', async () => {
    const other = seedUser({ email: 'grace@example.com', name: 'Grace' })
    signInAsUser()
    seedProject({ name: 'Mine', currentPhase: 'planning', status: 'active', projectVersion: 3 })
    seedProject({ name: 'Not mine', members: [{ userId: other.id, role: 'owner' }] })
    renderRoute('/projects')

    const list = await screen.findByRole('list', { name: 'Projects' })
    const card = within(list).getByRole('listitem') // throws unless exactly one
    expect(within(card).getByRole('link', { name: 'Mine' })).toBeInTheDocument()
    expect(within(card).getByText('Planning')).toBeInTheDocument()
    expect(within(card).getByText('Active')).toBeInTheDocument()
    expect(within(card).getByText('3')).toBeInTheDocument()
    expect(within(card).getByText('Owner')).toBeInTheDocument()
    expect(screen.queryByText('Not mine')).not.toBeInTheDocument()
    expect(screen.getByText('Showing 1–1 of 1 project')).toBeInTheDocument()
  })

  it('FR-6: shows an empty state with a "New project" call to action', async () => {
    signInAsUser()
    renderRoute('/projects')

    expect(await screen.findByText('No projects yet')).toBeInTheDocument()
    const links = screen.getAllByRole('link', { name: 'New project' })
    expect(links.length).toBeGreaterThanOrEqual(2)
    expect(links[0]).toHaveAttribute('href', '/projects/new')
  })

  it('FR-6: phase, status and sort filters are kept in the URL search params', async () => {
    signInAsUser()
    seedProject({ name: 'Alpha', currentPhase: 'planning' })
    const { router } = renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('link', { name: 'Alpha' })

    await user.selectOptions(screen.getByLabelText('Phase'), 'planning')
    await waitFor(() => {
      expect(currentHref(router)).toBe('/projects?phase=planning')
    })

    await user.selectOptions(screen.getByLabelText('Status'), 'active')
    await user.selectOptions(screen.getByLabelText('Sort by'), 'name')
    await user.selectOptions(screen.getByLabelText('Order'), 'asc')
    await waitFor(() => {
      expect(router.state.location.search).toEqual({
        phase: 'planning',
        status: 'active',
        sortBy: 'name',
        sortOrder: 'asc',
      })
    })
    expect(await screen.findByRole('link', { name: 'Alpha' })).toBeInTheDocument()
  })

  it('FR-6: search is debounced into the URL and filters by name', async () => {
    signInAsUser()
    seedProject({ name: 'Alpha' })
    seedProject({ name: 'Beta' })
    const { router } = renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('link', { name: 'Alpha' })

    await user.type(screen.getByRole('searchbox', { name: 'Search by name' }), 'bet')

    await waitFor(() => {
      expect(currentHref(router)).toBe('/projects?search=bet')
    })
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Alpha' })).not.toBeInTheDocument()
    })
    expect(screen.getByRole('link', { name: 'Beta' })).toBeInTheDocument()
  })

  it('FR-6: filters from the URL apply on load, and "Clear filters" resets them', async () => {
    signInAsUser()
    seedProject({ name: 'Alpha', status: 'active' })
    const { router } = renderRoute('/projects?status=released')
    const user = userEvent.setup()

    expect(await screen.findByText('No projects match your filters')).toBeInTheDocument()
    expect(screen.getByLabelText('Status')).toHaveValue('released')

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))

    expect(await screen.findByRole('link', { name: 'Alpha' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/projects')
  })

  it('FR-6: pages through results with the page in the URL', async () => {
    signInAsUser()
    for (let i = 1; i <= 21; i += 1) {
      seedProject({ name: `Project ${String(i).padStart(2, '0')}` })
    }
    const { router } = renderRoute('/projects?sortBy=name&sortOrder=asc')
    const user = userEvent.setup()
    await screen.findByRole('link', { name: 'Project 01' })
    expect(screen.getByText('Page 1 of 2')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Next page' }))

    expect(await screen.findByRole('link', { name: 'Project 21' })).toBeInTheDocument()
    expect(router.state.location.search).toMatchObject({ page: 2 })
    expect(screen.getByText('Showing 21–21 of 21 projects')).toBeInTheDocument()
  })

  it('FR-6: shows an error state with a retry when the list fails to load', async () => {
    signInAsUser()
    server.use(http.get('*/api/projects', () => problem(500, 'VALIDATION', 'boom')))
    renderRoute('/projects')

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
