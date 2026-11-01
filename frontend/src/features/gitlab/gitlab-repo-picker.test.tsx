import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it, vi } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { gitlabDb, seedGitLabConnection, seedGitLabRepo } from '../../mocks/handlers/gitlab'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { renderWithProviders } from '../../test/render'
import { GitLabRepoPicker } from './gitlab-repo-picker'

/** The picker with "Create GitLab project" (FR-132), for one connected user. */
async function openCreate() {
  const me = signInAs()
  const connection = seedGitLabConnection(me.id)
  const onSelect = vi.fn()
  renderWithProviders(<GitLabRepoPicker connectionId={connection.id} searchId="search" selected="" onSelect={onSelect} />)
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: 'Create GitLab project' }))
  const dialog = await screen.findByRole('dialog', { name: 'Create GitLab project' })
  await within(dialog).findByLabelText('Namespace')
  return { user, dialog, connection, onSelect }
}

describe('GitLab Create project (FR-132)', () => {
  it('FR132_create_project: creates acme/todo-app (private) in group acme and selects it', async () => {
    const { user, dialog, connection, onSelect } = await openCreate()

    await user.selectOptions(within(dialog).getByLabelText('Namespace'), 'acme')
    expect(within(dialog).getByRole('option', { name: /locked \(group\) \(not allowed\)/ })).toBeDisabled()
    expect(within(dialog).getByRole('list', { name: "Namespaces you can't use" })).toHaveTextContent(
      'Only Maintainers can create projects in this group.',
    )
    await user.type(within(dialog).getByLabelText('Project name'), 'todo-app')
    await user.type(within(dialog).getByLabelText('Description (optional)'), 'Shared todos')
    await user.click(within(dialog).getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ fullPath: 'acme/todo-app', visibility: 'private' }))
    })
    expect(gitlabDb.createdRepos).toEqual([
      { connectionId: connection.id, owner: 'acme', name: 'todo-app', private: true, description: 'Shared todos' },
    ])
  })

  it('FR132_create_name_taken: REPO_NAME_TAKEN is shown on the name', async () => {
    const { user, dialog, connection } = await openCreate()
    seedGitLabRepo(connection.id, { path: 'todo-app', namespaceFullPath: 'ada', fullPath: 'ada/todo-app' })

    await user.type(within(dialog).getByLabelText('Project name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create project' }))

    expect(await within(dialog).findAllByText(/A project with this name or path already exists/)).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('Project name')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR132_create_rate_limited: the GitLab.com project-creation limit (429) is explained', async () => {
    const { user, dialog } = await openCreate()

    await user.type(within(dialog).getByLabelText('Project name'), 'limit')
    await user.click(within(dialog).getByRole('button', { name: 'Create project' }))

    expect(await within(dialog).findByText(/project-creation limit was reached/)).toBeInTheDocument()
  })

  it('FR132_create_forbidden: FORBIDDEN in a group explains it', async () => {
    server.use(
      http.post('*/api/me/git/gitlab/repos', () => problem(403, 'FORBIDDEN', "GitLab doesn't let the caller create projects there.")),
    )
    const { user, dialog } = await openCreate()

    await user.type(within(dialog).getByLabelText('Project name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create project' }))

    expect(await within(dialog).findByText(/GitLab doesn't let you create projects there/)).toBeInTheDocument()
  })
})
