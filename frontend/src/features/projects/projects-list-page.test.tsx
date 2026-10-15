import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb, seedProject } from '../../mocks/handlers/projects'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { server } from '../../mocks/server'
import { currentHref, renderRoute } from '../../test/render'

function signInAsUser() {
  seedUser({ email: 'admin@example.com', name: 'Admin' }) // the first user is the Admin
  return signInAs({ email: 'ada@example.com', systemRole: 'user' })
}

describe('Projects list (FR-6)', () => {
  it('FR-6: shows only the projects the user is a member of, with phase, status, versions and role', async () => {
    const other = seedUser({ email: 'grace@example.com', name: 'Grace' })
    signInAsUser()
    seedProject({ name: 'Mine', currentPhase: 'planning', status: 'active', checkpoint: 3 })
    seedProject({ name: 'Not mine', members: [{ userId: other.id, role: 'owner' }] })
    renderRoute('/projects')

    const list = await screen.findByRole('list', { name: 'Projects' })
    const card = within(list).getByRole('listitem') // throws unless exactly one
    expect(within(card).getByRole('link', { name: 'Mine' })).toBeInTheDocument()
    expect(within(card).getByText('Planning')).toBeInTheDocument()
    expect(within(card).getByText('Active')).toBeInTheDocument()
    expect(within(card).getByText('Not released')).toBeInTheDocument()
    expect(within(card).getByText(/In development/)).toHaveTextContent('In development 0.1.0')
    expect(within(card).getByText('Owner')).toBeInTheDocument()
    expect(screen.queryByText('Not mine')).not.toBeInTheDocument()
    expect(screen.getByText('Showing 1–1 of 1 project')).toBeInTheDocument()
  })

  it('FR62_list_row_shows_released_and_in_development_badges', async () => {
    signInAsUser()
    const p = seedProject({ name: 'Shop', currentPhase: 'refinement', status: 'active' })
    seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
    const done = seedProject({ name: 'Done', currentPhase: 'release', status: 'released' })
    seedReleaseVersions(done, { released: '2.0.0' })
    renderRoute('/projects')

    const list = await screen.findByRole('list', { name: 'Projects' })
    const row = (name: string) => within(list).getByRole('link', { name }).closest('li') as HTMLElement
    const shop = row('Shop')
    expect(within(shop).getByText('Refinement')).toBeInTheDocument()
    expect(within(shop).getByText('Active')).toBeInTheDocument()
    const shopBadges = within(shop).getByTestId('version-badges')
    expect(shopBadges).toHaveTextContent('Released 1.1.0')
    expect(shopBadges).toHaveTextContent('In development 1.2.0')
    const doneBadges = within(row('Done')).getByTestId('version-badges')
    expect(doneBadges).toHaveTextContent('Released 2.0.0')
    expect(doneBadges).not.toHaveTextContent('In development')
  })

  it('FR62_filter_has_a_version_in_development', async () => {
    signInAsUser()
    const dev = seedProject({ name: 'Developing' })
    seedReleaseVersions(dev, { released: '1.1.0', development: '1.2.0' })
    const done = seedProject({ name: 'Finished', status: 'released' })
    seedReleaseVersions(done, { released: '1.0.0' })
    const { router } = renderRoute('/projects')
    const user = userEvent.setup()
    await screen.findByRole('link', { name: 'Finished' })

    await user.selectOptions(screen.getByLabelText('Has a version in development'), 'true')
    await waitFor(() => {
      expect(currentHref(router)).toBe('/projects?hasDevelopmentVersion=true')
    })
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Finished' })).not.toBeInTheDocument()
    })
    expect(screen.getByRole('link', { name: 'Developing' })).toBeInTheDocument()
    expect(projectsDb.projects).toHaveLength(2)

    await user.selectOptions(screen.getByLabelText('Has a version in development'), 'false')
    expect(await screen.findByRole('link', { name: 'Finished' })).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Developing' })).not.toBeInTheDocument()
    })
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

  it('FR53_projects_list: a KPI row counts active, in review and released projects; awaiting gates is not reported yet', async () => {
    signInAsUser()
    seedProject({ name: 'Alpha', status: 'active' })
    seedProject({ name: 'Beta', currentPhase: 'review', status: 'reviewing' })
    seedProject({ name: 'Gamma', currentPhase: 'release', status: 'released' })
    renderRoute('/projects')

    const totals = await screen.findByRole('region', { name: 'Project totals' })
    const tile = (label: string) => within(totals).getByText(label).nextElementSibling
    await waitFor(() => {
      expect(tile('Active')).toHaveTextContent('2')
    })
    expect(tile('In review')).toHaveTextContent('1')
    expect(tile('Released')).toHaveTextContent('1')
    expect(tile('Awaiting gates')).toHaveTextContent('—')
  })

  it('FR53_projects_list: from md up the projects are a sortable table; the header sort is kept in the URL', async () => {
    const original = globalThis.matchMedia
    globalThis.matchMedia = (query: string) => {
      const list = original(query)
      Object.defineProperty(list, 'matches', { value: query.includes('48rem') })
      return list
    }
    try {
      signInAsUser()
      seedProject({ name: 'Alpha' })
      const { router } = renderRoute('/projects')
      const user = userEvent.setup()

      const table = await screen.findByRole('table', { name: 'Projects' })
      expect(within(table).getByRole('link', { name: 'Alpha' })).toBeInTheDocument()
      expect(screen.queryByRole('list', { name: 'Projects' })).not.toBeInTheDocument()
      expect(within(table).getByRole('columnheader', { name: /Updated/ })).toHaveAttribute('aria-sort', 'descending')

      await user.click(within(table).getByRole('button', { name: 'Project' }))
      await waitFor(() => {
        expect(router.state.location.search).toEqual({ sortBy: 'name', sortOrder: 'asc' })
      })
    } finally {
      globalThis.matchMedia = original
    }
  })
})
