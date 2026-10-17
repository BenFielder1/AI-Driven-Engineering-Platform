import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { signInAs } from '../../mocks/handlers/auth'
import { configureGitHubApp, connectGitHub, githubDb, INSTALL_URL, seedRepo } from '../../mocks/handlers/github'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

const APP_OPTION = 'GitHub (AIDE GitHub App)'

/** Signs in, optionally registers the App and connects GitHub, and walks the wizard to the Git step. */
async function openGitStep({ app = true, connect = true } = {}) {
  const me = signInAs({ systemRole: 'user' })
  if (app) {
    configureGitHubApp()
  }
  if (connect) {
    connectGitHub(me.id)
  }
  renderRoute('/projects/new')
  const user = userEvent.setup()
  await screen.findByRole('heading', { level: 2, name: /Step 1 of 7: Source/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 2 of 7: Project idea/ })
  await user.type(screen.getByLabelText('Project name'), 'Todo app')
  await user.type(screen.getByLabelText('Your idea'), 'A shared todo list.')
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 3 of 7: Repository/ })
  return { user }
}

const repoList = () => screen.findByRole('list', { name: 'Repositories' })

async function finish(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 4 of 7/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 5 of 7: Stack/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 6 of 7/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 7 of 7/ })
}

const createBody = () =>
  projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/projects')?.body as
    | Schemas['CreateProjectRequest']
    | undefined

describe('Wizard → Git repository with the GitHub App (FR-81, FR-86)', () => {
  it('FR86_app_option_hidden_without_a_registered_app', async () => {
    await openGitStep({ app: false, connect: false })
    expect(screen.queryByRole('radio', { name: APP_OPTION })).not.toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Repository URL and access token' })).toBeChecked()
  })

  it('FR86_not_connected: the App option offers Connect GitHub, and PAT stays the default', async () => {
    const { user } = await openGitStep({ connect: false })
    expect(screen.getByRole('radio', { name: 'Repository URL and access token' })).toBeChecked()

    await user.click(screen.getByRole('radio', { name: APP_OPTION }))

    expect(await screen.findByRole('link', { name: 'Connect GitHub' })).toHaveAttribute(
      'href',
      '/api/me/connections/github/connect?returnUrl=%2Fprojects%2Fnew',
    )
    await user.click(screen.getByRole('button', { name: 'Next' }))
    expect(await screen.findAllByText('Pick a repository, or create one.')).not.toHaveLength(0)
  })

  it('FR86_picker_search_and_select: the App is the default; search filters, picking prefills the branch and is sent as mode/installationId/repoFullName', async () => {
    seedRepo({ owner: 'ada', name: 'todo-app' })
    seedRepo({ owner: 'acme', name: 'billing', defaultBranch: 'trunk', installationId: 9002, private: false })
    const { user } = await openGitStep()

    await waitFor(() => {
      expect(screen.getByRole('radio', { name: APP_OPTION })).toBeChecked()
    })
    const list = await repoList()
    expect(within(list).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual(['ada/todo-app', 'acme/billing'])

    await user.type(screen.getByLabelText('Search repositories'), 'bill')
    await waitFor(() => {
      expect(within(screen.getByRole('list', { name: 'Repositories' })).getAllByRole('radio')).toHaveLength(1)
    })
    await user.click(screen.getByRole('radio', { name: 'acme/billing' }))

    expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/billing')
    expect(screen.getByLabelText('Default branch')).toHaveValue('trunk')
    expect(screen.queryByText('This repository has code. AIDE will build on it')).not.toBeInTheDocument()

    await finish(user)
    const git = screen.getByRole('region', { name: 'Repository' })
    expect(within(git).getByText('acme/billing')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Todo app' })).toBeInTheDocument()
    expect(createBody()?.git).toEqual({
      mode: 'github_app',
      installationId: 9002,
      repoFullName: 'acme/billing',
      defaultBranch: 'trunk',
    })
    expect(createBody()?.git).not.toHaveProperty('provider')
  })

  it('FR86_non_empty_repo_warning: a repo with code in "start from scratch" warns that AIDE will build on it', async () => {
    const repo = seedRepo({ owner: 'ada', name: 'legacy', empty: null })
    githubDb.refs[repo.fullName] = { defaultBranch: 'main', empty: false, releases: [], tags: [] }
    const { user } = await openGitStep()

    await user.click(within(await repoList()).getByRole('radio', { name: 'ada/legacy' }))

    expect(await screen.findByText('This repository has code. AIDE will build on it')).toBeInTheDocument()
    await finish(user)
    expect(within(screen.getByRole('region', { name: 'Repository' })).getByText(/AIDE builds on it/)).toBeInTheDocument()
  })

  it('FR81_create_repo_selects_it: Create repository makes a private repo and the wizard selects it', async () => {
    const { user } = await openGitStep()
    await screen.findByText(/AIDE can’t access any of your repositories yet/)

    await user.click(screen.getByRole('button', { name: 'Create repository' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create repository' })
    await waitFor(() => {
      expect(within(dialog).getByLabelText('Owner')).toHaveValue('ada')
    })
    await user.selectOptions(within(dialog).getByLabelText('Owner'), 'acme')
    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    await user.type(within(dialog).getByLabelText('Description (optional)'), 'Shared todos')
    expect(within(dialog).getByRole('radio', { name: 'Private' })).toBeChecked()
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Create repository' })).not.toBeInTheDocument()
    })
    expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/todo-app')
    expect(githubDb.repos.find((r) => r.fullName === 'acme/todo-app')).toMatchObject({ private: true, installationId: 9002 })
    const post = projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/me/github/repos')
    expect(post?.body).toEqual({ owner: 'acme', name: 'todo-app', private: true, description: 'Shared todos' })
    // Still on the Git step: the dialog's submit didn't advance the wizard.
    expect(screen.getByRole('heading', { level: 2, name: /Step 3 of 7/ })).toBeInTheDocument()
    expect(await within(await repoList()).findByRole('radio', { name: 'acme/todo-app' })).toBeChecked()
  })

  it('FR81_repo_name_taken: REPO_NAME_TAKEN shows the copy on the name field', async () => {
    seedRepo({ owner: 'ada', name: 'todo-app' })
    const { user } = await openGitStep()

    await user.click(await screen.findByRole('button', { name: 'Create repository' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create repository' })
    await waitFor(() => {
      expect(within(dialog).getByLabelText('Owner')).toHaveValue('ada')
    })
    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    const message = 'A repository with this name already exists for that owner. Choose another name.'
    expect(await within(dialog).findAllByText(message)).not.toHaveLength(0)
    expect(within(dialog).getByLabelText('Repository name')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR81_owner_without_app: an owner without the App is disabled with the reason and an install link', async () => {
    const { user } = await openGitStep()
    githubDb.owners = [
      { login: 'ada', type: 'user', installationId: 9001, appInstalled: true, canCreate: true },
      { login: 'globex', type: 'organization', installationId: null, appInstalled: false, canCreate: false },
    ]

    await user.click(await screen.findByRole('button', { name: 'Create repository' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create repository' })
    const option = await within(dialog).findByRole('option', { name: /globex/ })
    expect(option).toBeDisabled()
    expect(option).toHaveTextContent('AIDE isn’t installed here')
    expect(within(dialog).getByRole('link', { name: /Install AIDE on globex/ })).toHaveAttribute('href', INSTALL_URL)
  })

  it('FR81_app_not_installed: the picker shows the copy and the install link', async () => {
    server.use(
      http.get('*/api/me/github/repos', () =>
        problem(409, 'GITHUB_APP_NOT_INSTALLED', 'Not installed.', { installUrl: INSTALL_URL }),
      ),
    )
    await openGitStep()

    expect(await screen.findByText(/The AIDE GitHub App isn't installed there/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Install AIDE on GitHub/ })).toHaveAttribute('href', INSTALL_URL)
  })

  it('FR81_rate_limited: GITHUB_RATE_LIMITED says when to retry', async () => {
    githubDb.rateLimitedFor = 30
    await openGitStep()

    expect(await screen.findByText("GitHub's rate limit was reached. Try again in 30 seconds.")).toBeInTheDocument()
  })

  it('FR86_read_only_repo_disabled: a repo without push access is disabled with the reason', async () => {
    seedRepo({ owner: 'acme', name: 'readonly', canPush: false })
    await openGitStep()

    const radio = within(await repoList()).getByRole('radio', { name: 'acme/readonly' })
    expect(radio).toBeDisabled()
    expect(radio).toHaveAccessibleDescription(expect.stringContaining('You need write access to this repository') as string)
  })

  it('FR86_create_forbidden: a 403 on create explains that push access is needed', async () => {
    seedRepo({ owner: 'ada', name: 'todo-app' })
    server.use(http.post('*/api/projects', () => problem(403, 'FORBIDDEN', 'No push access.')))
    const { user } = await openGitStep()
    await user.click(within(await repoList()).getByRole('radio', { name: 'ada/todo-app' }))
    await finish(user)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await screen.findByText(/You need write access to this repository on GitHub to create a project on it/)).toBeInTheDocument()
  })
})
