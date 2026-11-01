import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it, vi } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { bitbucketDb, seedBitbucketConnection, seedBitbucketRepo } from '../../mocks/handlers/bitbucket'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { renderWithProviders } from '../../test/render'
import { BitbucketRepoPicker } from './bitbucket-repo-picker'

function renderPicker({ repos = 0 } = {}) {
  const me = signInAs()
  const connection = seedBitbucketConnection(me.id)
  for (let i = 0; i < repos; i++) {
    seedBitbucketRepo(connection.id, { slug: `app-${String(i + 1).padStart(2, '0')}` })
  }
  const onSelect = vi.fn()
  renderWithProviders(<BitbucketRepoPicker connectionId={connection.id} searchId="search" selected="" onSelect={onSelect} />)
  return { user: userEvent.setup(), connection, onSelect }
}

/** The picker with "Create Bitbucket repository" (FR-135), for one connected user. */
async function openCreate() {
  const picker = renderPicker()
  await picker.user.click(await screen.findByRole('button', { name: 'Create Bitbucket repository' }))
  const dialog = await screen.findByRole('dialog', { name: 'Create Bitbucket repository' })
  await within(dialog).findByLabelText('Workspace')
  return { ...picker, dialog }
}

describe('Bitbucket repo picker (FR-135)', () => {
  it('FR135_picker_paging_and_search: lists 30 per page with the opaque cursor and searches by name', async () => {
    const { user, connection, onSelect } = renderPicker({ repos: 32 })

    const list = await screen.findByRole('list', { name: 'Bitbucket repositories' })
    expect(within(list).getAllByRole('radio')).toHaveLength(30)
    await user.click(screen.getByRole('button', { name: 'Load more repositories' }))
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'Bitbucket repositories' })).getAllByRole('radio')).toHaveLength(32)
    })
    expect(bitbucketDb.repoQueries.some((q) => q.cursor === 'opaque-30' && q.connectionId === connection.id)).toBe(true)

    await user.type(screen.getByLabelText('Search Bitbucket repositories'), 'app-07')
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'Bitbucket repositories' })).getAllByRole('radio')).toHaveLength(1)
    })
    expect(bitbucketDb.repoQueries.some((q) => q.search === 'app-07')).toBe(true)
    await user.click(screen.getByRole('radio', { name: 'acme/app-07' }))
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ fullName: 'acme/app-07', mainBranch: 'main' }))
  })

  it('FR135_rate_limited: UPSTREAM_RATE_LIMITED names Bitbucket and the Retry-After wait', async () => {
    bitbucketDb.rateLimitedFor = 30
    renderPicker()
    expect(await screen.findByText('Bitbucket is rate limiting requests. Try again in 30 seconds.')).toBeInTheDocument()
  })

  it('FR135_git_not_connected: GIT_NOT_CONNECTED links to Connections', async () => {
    server.use(
      http.get('*/api/me/git/bitbucket/repos', () => problem(409, 'GIT_NOT_CONNECTED', 'The Bitbucket connection is invalid.')),
    )
    renderPicker()
    expect(await screen.findByText(/This Bitbucket connection no longer works/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to Connections' })).toHaveAttribute('href', '/connections')
  })

  it('FR135_no_create_when_off: allowCreate=false hides Create, so no orphan repository can be made', async () => {
    const me = signInAs()
    const connection = seedBitbucketConnection(me.id)
    renderWithProviders(
      <BitbucketRepoPicker connectionId={connection.id} searchId="search" selected="" onSelect={vi.fn()} allowCreate={false} />,
    )
    expect(await screen.findByText('You can’t write to any Bitbucket repository yet.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create Bitbucket repository' })).not.toBeInTheDocument()
  })
})

describe('Bitbucket Create repository (FR-135)', () => {
  it('FR135_create_repo: creates acme/todo-app in project TODO (private) and selects it', async () => {
    const { user, dialog, connection, onSelect } = await openCreate()

    expect(within(dialog).getByLabelText('Workspace')).toHaveValue('acme')
    expect(within(dialog).getByLabelText('Project')).toHaveValue('TODO')
    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    expect(dialog).toHaveTextContent('Its address: acme/todo-app')
    await user.type(within(dialog).getByLabelText('Description (optional)'), 'Shared todos')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledWith(
        expect.objectContaining({ fullName: 'acme/todo-app', projectKey: 'TODO', isPrivate: true }),
      )
    })
    expect(bitbucketDb.createdRepos).toEqual([
      {
        connectionId: connection.id,
        owner: 'acme',
        project: 'TODO',
        name: 'todo-app',
        private: true,
        description: 'Shared todos',
      },
    ])
  })

  it('FR135_project_picker: the project list follows the workspace; one without projects can not create', async () => {
    const { user, dialog } = await openCreate()

    await user.selectOptions(within(dialog).getByLabelText('Project'), 'WEB')
    expect(within(dialog).getByLabelText('Project')).toHaveValue('WEB')

    await user.selectOptions(within(dialog).getByLabelText('Workspace'), 'empty-ws')
    expect(within(dialog).getByLabelText('Project')).toBeDisabled()
    expect(dialog).toHaveTextContent('This workspace has no projects you can use.')
    expect(within(dialog).getByRole('button', { name: 'Create repository' })).toBeDisabled()
  })

  it('FR135_owners_truncated: notes the 50-workspace cap, and a truncated project list accepts a typed key', async () => {
    const me = signInAs()
    const connection = seedBitbucketConnection(me.id)
    bitbucketDb.ownersTruncated = true
    const acme = bitbucketDb.owners[connection.id]?.[0]
    if (acme) {
      acme.projectsTruncated = true
      acme.projects.push({ key: 'BIG', name: 'Big project', isPrivate: true })
    }
    const onSelect = vi.fn()
    renderWithProviders(<BitbucketRepoPicker connectionId={connection.id} searchId="search" selected="" onSelect={onSelect} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create Bitbucket repository' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create Bitbucket repository' })

    expect(await within(dialog).findByTestId('bitbucket-workspaces-truncated')).toHaveTextContent('first 50 workspaces')
    await user.type(within(dialog).getByLabelText("Project key (if it isn't listed)"), 'BIG')
    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ fullName: 'acme/todo-app', projectKey: 'BIG' }))
    })
    expect(bitbucketDb.createdRepos[0]?.project).toBe('BIG')
  })

  it('FR135_create_name_taken: REPO_NAME_TAKEN is shown on the name', async () => {
    const { user, dialog, connection } = await openCreate()
    seedBitbucketRepo(connection.id, { slug: 'todo-app' })

    await user.type(within(dialog).getByLabelText('Repository name'), 'Todo App')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    expect(await within(dialog).findAllByText(/A repository with this name already exists in that workspace/)).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('Repository name')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR135_create_forbidden: FORBIDDEN explains it', async () => {
    server.use(
      http.post('*/api/me/git/bitbucket/repos', () =>
        problem(403, 'FORBIDDEN', "Bitbucket doesn't let the caller create repositories there."),
      ),
    )
    const { user, dialog } = await openCreate()

    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    expect(await within(dialog).findByText(/Bitbucket doesn't let you create repositories there/)).toBeInTheDocument()
  })

  it('FR135_readme_push_failed: UPSTREAM_ERROR says the repository may exist but be empty', async () => {
    server.use(
      http.post('*/api/me/git/bitbucket/repos', () => problem(502, 'UPSTREAM_ERROR', 'The README push failed.')),
    )
    const { user, dialog } = await openCreate()

    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    expect(await within(dialog).findByText(/couldn't push its first commit/)).toBeInTheDocument()
  })
})
