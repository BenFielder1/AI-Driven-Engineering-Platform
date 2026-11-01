import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { gitlabDb, seedGitLabConnection, seedGitLabRepo } from '../../mocks/handlers/gitlab'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { WIZARD_DRAFT_KEY } from './wizard-draft'

/** Signs in, optionally connects GitLab (with `repos` projects), and walks the wizard to the Repository step. */
async function openGitLabStep({ connect = true, repos = 0 } = {}) {
  const me = signInAs({ systemRole: 'user' })
  const connection = connect ? seedGitLabConnection(me.id) : null
  for (let i = 0; i < repos; i++) {
    if (connection) {
      seedGitLabRepo(connection.id, { path: `app-${String(i + 1).padStart(2, '0')}` })
    }
  }
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await screen.findByRole('heading', { level: 2, name: /Step 1 of 8: Source/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 2 of 8: Project idea/ })
  await user.type(screen.getByLabelText('Project name'), 'Todo app')
  await user.type(screen.getByLabelText('Your idea'), 'A shared todo list.')
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 3 of 8: Repository/ })
  await user.click(await screen.findByRole('radio', { name: 'GitLab' }))
  return { user, connection }
}

describe('Wizard → Repository on GitLab (FR-132)', () => {
  it('FR132_not_connected: links to Connections and keeps the wizard', async () => {
    const { user } = await openGitLabStep({ connect: false })

    const link = await screen.findByRole('link', { name: 'Connect GitLab under Connections' })
    await user.click(link)

    expect(sessionStorage.getItem(WIZARD_DRAFT_KEY)).toContain('"gitMode":"gitlab"')
  })

  it('FR132_picker_search_and_paging: lists projects with cursor paging and searches by name', async () => {
    const { user, connection } = await openGitLabStep({ repos: 32 })

    const list = await screen.findByRole('list', { name: 'GitLab projects' })
    expect(within(list).getAllByRole('radio')).toHaveLength(30)
    await user.click(screen.getByRole('button', { name: 'Load more projects' }))
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'GitLab projects' })).getAllByRole('radio')).toHaveLength(32)
    })
    expect(gitlabDb.repoQueries.some((q) => q.cursor === '30' && q.connectionId === connection?.id)).toBe(true)

    await user.type(screen.getByLabelText('Search GitLab projects'), 'app-07')
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'GitLab projects' })).getAllByRole('radio')).toHaveLength(1)
    })
    await user.click(screen.getByRole('radio', { name: 'acme/app-07' }))
    expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/app-07')
    expect(screen.getByLabelText('Default branch')).toHaveValue('main')
  })

  it('FR132_git_not_connected: GIT_NOT_CONNECTED from the picker links to Connections', async () => {
    server.use(
      http.get('*/api/me/git/gitlab/repos', () => problem(409, 'GIT_NOT_CONNECTED', 'The GitLab connection is invalid.')),
    )
    await openGitLabStep()

    expect(await screen.findByText(/This GitLab connection no longer works/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to Connections' })).toHaveAttribute('href', '/connections')
  })

  it('FR132_rate_limited: UPSTREAM_RATE_LIMITED from the picker is explained', async () => {
    gitlabDb.rateLimited = true
    await openGitLabStep()
    expect(await screen.findByText('GitLab is rate limiting requests. Try again in a few minutes.')).toBeInTheDocument()
  })

  it('FR132_summary_link_target: without a connection, the error summary links to an element that exists', async () => {
    const { user } = await openGitLabStep({ connect: false })
    await screen.findByRole('link', { name: 'Connect GitLab under Connections' })

    await user.click(screen.getByRole('button', { name: 'Next' }))

    const link = await screen.findByRole('link', { name: 'Connect GitLab, or choose another option.' })
    expect(link).toHaveAttribute('href', '#project-gitlab-connection')
    expect(document.getElementById('project-gitlab-connection')).not.toBeNull()
  })
})
