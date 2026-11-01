import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { contextualErrorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { seedBitbucketConnection, seedBitbucketRefs, seedBitbucketRepo } from '../../mocks/handlers/bitbucket'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })

const createBody = () =>
  projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/projects')?.body as
    | Schemas['CreateProjectRequest']
    | undefined

const detectBodies = () =>
  projectsDb.requests
    .filter((r) => r.method === 'POST' && r.path === '/api/stack/detect')
    .map((r) => r.body as Schemas['DetectStackRequest'])

/** Signs in with a Bitbucket connection (repositories `slugs` in acme) and walks to the Repository step, Bitbucket chosen. */
async function openRepository({ importing = false, slugs = [] as string[] } = {}) {
  const me = signInAs({ name: 'Ada Lovelace', systemRole: 'user' })
  const connection = seedBitbucketConnection(me.id)
  const repos = slugs.map((slug) => seedBitbucketRepo(connection.id, { slug }))
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  if (importing) {
    await user.click(screen.getByRole('radio', { name: 'Import an existing repository' }))
  }
  await next(user)
  if (importing) {
    await stepHeading(/Step 2 of 8: Request/)
    await user.type(screen.getByLabelText('Project name'), 'Todo app')
    await user.type(screen.getByLabelText('What should this version deliver?'), 'Add sharing')
  } else {
    await stepHeading(/Step 2 of 8: Project idea/)
    await user.type(screen.getByLabelText('Project name'), 'Todo app')
    await user.type(screen.getByLabelText('Your idea'), 'A shared todo list.')
  }
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  await user.click(await screen.findByRole('radio', { name: 'Bitbucket' }))
  return { user, connection, repos }
}

/** Version → Stack → Configuration → Deployment → Review & create. */
async function versionToReview(user: User) {
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
  await next(user)
  await stepHeading(/Step 8 of 8: Review & create/)
}

function first<T>(items: readonly T[]): T {
  const [item] = items
  if (item === undefined) {
    throw new Error('nothing seeded')
  }
  return item
}

async function pick(user: User, fullName: string) {
  const list = await screen.findByRole('list', { name: 'Bitbucket repositories' })
  await user.click(within(list).getByRole('radio', { name: fullName }))
}

describe('Wizard → create on Bitbucket (FR-135, FR-136)', () => {
  it('FR135_create_selects_and_binds: creates acme/todo-app in project TODO, the wizard selects it, and the project is bound', async () => {
    const { user, connection } = await openRepository()

    await user.click(await screen.findByRole('button', { name: 'Create Bitbucket repository' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create Bitbucket repository' })
    await within(dialog).findByLabelText('Workspace')
    expect(within(dialog).getByLabelText('Project')).toHaveValue('TODO')
    await user.type(within(dialog).getByLabelText('Repository name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create repository' }))

    await waitFor(() => {
      expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/todo-app')
    })
    // A new repository holds only its README: no "has code" warning.
    expect(screen.queryByText('This repository has code. AIDE will build on it')).not.toBeInTheDocument()

    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)
    const repository = screen.getByRole('region', { name: 'Repository' })
    expect(within(repository).getByText('acme/todo-app')).toBeInTheDocument()
    expect(within(repository).getByText('ada-bb (OAuth)')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Todo app' })

    const created = projectsDb.projects.find((p) => p.name === 'Todo app')
    expect(createBody()?.git).toEqual({
      mode: 'bitbucket',
      connectionId: connection.id,
      repoId: created?.git.externalRepoId,
      repoFullName: 'acme/todo-app',
      defaultBranch: 'main',
    })
    expect(created?.git.externalRepoId).toMatch(/^\{.+\}$/)
    expect(created?.git.binding).toMatchObject({ connectionId: connection.id, login: 'ada-bb', boundBy: { name: 'Ada Lovelace' } })
  })

  it('FR136_scratch_repo_with_code: a Bitbucket repository with code warns and offers Import instead', async () => {
    const { user } = await openRepository({ slugs: ['legacy'] })
    await pick(user, 'acme/legacy')

    expect(await screen.findByText('This repository has code. AIDE will build on it')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Import instead' }))

    expect(await screen.findByRole('group', { name: 'Currently released version' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Never released' })).toBeChecked()
  })

  it.each([
    ['CREDENTIAL_NOT_OWNER', 403, true],
    ['GIT_NOT_CONNECTED', 409, false],
  ] as const)('FR136_create_%s: shown on the Bitbucket connection, back on the Repository step', async (code, status, keyed) => {
    const errors: Record<string, string[]> = keyed ? { 'git.connectionId': ['Not yours.'] } : {}
    server.use(http.post('*/api/projects', () => problem(status, code, 'The binding was refused.', { errors })))
    const { user } = await openRepository({ slugs: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    await waitFor(() => {
      expect(screen.getByLabelText('Bitbucket connection')).toHaveAccessibleDescription(
        expect.stringContaining(contextualErrorMessages.createProjectBitbucket[code]),
      )
    }, { timeout: 5000 })
  })

  it('FR136_create_repo_key: FORBIDDEN keyed git.repoId lands on the repository picker', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(403, 'FORBIDDEN', 'Write access is required.', { errors: { 'git.repoId': ['Write access is required.'] } }),
      ),
    )
    const { user } = await openRepository({ slugs: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    expect(
      await screen.findAllByText(contextualErrorMessages.createProjectBitbucket.FORBIDDEN, undefined, { timeout: 5000 }),
    ).not.toHaveLength(0)
  })

  it('FR136_create_validation_key: VALIDATION on git.connectionId goes on the Bitbucket connection', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(400, 'VALIDATION', 'Invalid.', { errors: { 'git.connectionId': ['Pick one of your Bitbucket connections.'] } }),
      ),
    )
    const { user } = await openRepository({ slugs: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    await waitFor(() => {
      expect(screen.getByLabelText('Bitbucket connection')).toHaveAccessibleDescription(
        expect.stringContaining('Pick one of your Bitbucket connections.'),
      )
    }, { timeout: 5000 })
  })
})

describe('Wizard → import from Bitbucket (FR-141)', () => {
  it('FR141_bitbucket_tags_preselect_latest_and_suggest_next_minor: v1.0.0/v1.1.0 preselects v1.1.0, version 1.2.0, detection on Bitbucket', async () => {
    const { user, connection, repos } = await openRepository({ importing: true, slugs: ['todo-app'] })
    const repo = first(repos)
    seedBitbucketRefs(repo, {
      tags: [
        { name: 'v1.0.0', sha: 'a1', semver: '1.0.0' },
        { name: 'v1.1.0', sha: 'a2', semver: '1.1.0' },
      ],
      preselectedTag: 'v1.1.0',
      suggestedDevelopmentVersion: '1.2.0',
    })
    // Importing offers no Create.
    await screen.findByRole('list', { name: 'Bitbucket repositories' })
    expect(screen.queryByRole('button', { name: 'Create Bitbucket repository' })).not.toBeInTheDocument()

    await pick(user, 'acme/todo-app')

    const group = await screen.findByRole('group', { name: 'Currently released version' })
    expect(within(group).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual(['v1.1.0', 'v1.0.0', '__never_released__'])
    await waitFor(() => {
      expect(within(group).getByRole('radio', { name: 'v1.1.0' })).toBeChecked()
    })

    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    expect(screen.getByLabelText('Version to develop')).toHaveValue('1.2.0')

    await next(user)
    await stepHeading(/Step 5 of 8: Stack/)
    await waitFor(() => {
      expect(detectBodies()).toHaveLength(1)
    })
    expect(detectBodies()[0]?.git).toEqual({ provider: 'bitbucket', repo: repo.id, ref: 'v1.1.0', connectionId: connection.id })

    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Todo app' })

    expect(createBody()).toMatchObject({
      source: 'import',
      import: { releasedTag: 'v1.1.0', developmentVersion: '1.2.0' },
      git: { mode: 'bitbucket', repoId: repo.id, connectionId: connection.id, repoFullName: 'acme/todo-app' },
    })
  })

  it('FR141_bitbucket_empty_repo: an empty Bitbucket repository is explained and offers Start from scratch', async () => {
    const { user, repos } = await openRepository({ importing: true, slugs: ['blank'] })
    seedBitbucketRefs(first(repos), { empty: true })

    await pick(user, 'acme/blank')

    expect(await screen.findByText('This repository is empty')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Never released' })).toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Start from scratch instead' }))

    await waitFor(() => {
      expect(screen.queryByText('This repository is empty')).not.toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: 'Create Bitbucket repository' })).toBeInTheDocument()
  })
})
