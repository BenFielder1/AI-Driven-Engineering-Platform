import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { contextualErrorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { seedCredential } from '../../mocks/handlers/connections'
import { GITLAB_COM_ID, seedGitLabConnection, seedGitLabRefs, seedGitLabRepo } from '../../mocks/handlers/gitlab'
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

/** Signs in with a gitlab.com connection (with projects `repos` in acme) and walks to the Repository step, with GitLab chosen. */
async function openRepository({
  importing = false,
  repos = [] as string[],
  setup,
}: { importing?: boolean; repos?: string[]; setup?: (userId: string) => void } = {}) {
  const me = signInAs({ name: 'Ada Lovelace', systemRole: 'user' })
  setup?.(me.id)
  const connection = seedGitLabConnection(me.id)
  const seeded = repos.map((path) => seedGitLabRepo(connection.id, { path }))
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
  await user.click(await screen.findByRole('radio', { name: 'GitLab' }))
  return { user, connection, me, repos: seeded }
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

async function pick(user: User, fullPath: string) {
  const list = await screen.findByRole('list', { name: 'GitLab projects' })
  await user.click(within(list).getByRole('radio', { name: fullPath }))
}

describe('Wizard → create on GitLab (FR-132, FR-136)', () => {
  it('FR132_create_selects_and_binds: creates acme/todo-app, the wizard selects it, and the project is bound to the connection', async () => {
    const { user, connection } = await openRepository()

    await user.click(await screen.findByRole('button', { name: 'Create GitLab project' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create GitLab project' })
    await user.selectOptions(await within(dialog).findByLabelText('Namespace'), 'acme')
    await user.type(within(dialog).getByLabelText('Project name'), 'todo-app')
    await user.click(within(dialog).getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(screen.getByText('Selected:').parentElement).toHaveTextContent('Selected: acme/todo-app')
    })
    // A new project holds only its README: no "has code" warning.
    expect(screen.queryByText('This project has code. AIDE will build on it')).not.toBeInTheDocument()
    expect(screen.queryByText("AIDE can't create projects on GitLab yet. Choose another option to continue.")).not.toBeInTheDocument()

    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)
    const repository = screen.getByRole('region', { name: 'Repository' })
    expect(within(repository).getByText('acme/todo-app')).toBeInTheDocument()
    expect(within(repository).getByText('ada on GitLab.com (gitlab.com)')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Todo app' })

    const created = projectsDb.projects.find((p) => p.name === 'Todo app')
    expect(createBody()?.git).toEqual({
      mode: 'gitlab',
      instanceId: GITLAB_COM_ID,
      connectionId: connection.id,
      repoId: created?.git.externalRepoId,
      repoFullName: 'acme/todo-app',
      defaultBranch: 'main',
    })
    expect(created?.git.binding).toMatchObject({ connectionId: connection.id, login: 'ada', boundBy: { name: 'Ada Lovelace' } })
  })

  it('FR132_scratch_repo_with_code: a GitLab project with code warns and offers Import instead', async () => {
    const { user } = await openRepository({ repos: ['legacy'] })
    await pick(user, 'acme/legacy')

    expect(await screen.findByText('This project has code. AIDE will build on it')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Import instead' }))

    expect(await screen.findByRole('group', { name: 'Currently released version' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Never released' })).toBeChecked()
  })

  it.each([
    ['CREDENTIAL_NOT_OWNER', 403, 'GitLab connection'],
    ['GIT_NOT_CONNECTED', 409, 'GitLab connection'],
  ] as const)('FR136_create_%s: shown on the GitLab connection, back on the Repository step', async (code, status, label) => {
    server.use(http.post('*/api/projects', () => problem(status, code, 'The binding was refused.')))
    const { user } = await openRepository({ repos: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    await waitFor(() => {
      expect(screen.getByLabelText(label)).toHaveAccessibleDescription(
        expect.stringContaining(contextualErrorMessages.createProjectGitLab[code]),
      )
    }, { timeout: 5000 })
  })

  it('FR136_create_llm_credential_first: a key-less CREDENTIAL_NOT_OWNER with an AI connection chosen goes to that connection (checked before GitLab)', async () => {
    server.use(http.post('*/api/projects', () => problem(403, 'CREDENTIAL_NOT_OWNER', 'Not yours.')))
    const { user } = await openRepository({
      repos: ['todo-app'],
      setup: (userId) => {
        seedCredential({ userId, provider: 'anthropic', label: 'Work key', apiKey: 'sk-ant-new-a1b2' })
      },
    })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 6 of 8: Configuration/)).toBeInTheDocument()
    expect(await screen.findByLabelText('Anthropic connection')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR136_create_vercel_connection_key: CREDENTIAL_NOT_OWNER keyed deployment.connectionId goes to Deployment, not GitLab', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(403, 'CREDENTIAL_NOT_OWNER', 'Not yours.', { errors: { 'deployment.connectionId': ['Not yours.'] } }),
      ),
    )
    const { user } = await openRepository({ repos: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 7 of 8: Deployment/)).toBeInTheDocument()
    expect(screen.queryByText(contextualErrorMessages.createProjectGitLab.CREDENTIAL_NOT_OWNER)).not.toBeInTheDocument()
  })

  it('FR136_create_repo_key: NOT_FOUND keyed git.repoId lands on the project picker', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(404, 'NOT_FOUND', 'No such project.', { errors: { 'git.repoId': ['No such project.'] } }),
      ),
    )
    const { user } = await openRepository({ repos: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    expect(
      await screen.findAllByText(contextualErrorMessages.createProjectGitLab.NOT_FOUND, undefined, { timeout: 5000 }),
    ).not.toHaveLength(0)
  })

  it('FR136_create_instance_key: NOT_FOUND keyed git.instanceId lands on the GitLab connection', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(404, 'NOT_FOUND', 'The GitLab instance was not found.', { errors: { 'git.instanceId': ['No such instance.'] } }),
      ),
    )
    const { user } = await openRepository({ repos: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    await waitFor(() => {
      expect(screen.getByLabelText('GitLab connection')).toHaveAccessibleDescription(
        expect.stringContaining(contextualErrorMessages.createProjectGitLab.NOT_FOUND),
      )
    }, { timeout: 5000 })
  })

  it('FR132_create_forbidden: FORBIDDEN (below Developer access) is shown on the project picker', async () => {
    server.use(http.post('*/api/projects', () => problem(403, 'FORBIDDEN', 'Developer access is required.')))
    const { user } = await openRepository({ repos: ['todo-app'] })
    await pick(user, 'acme/todo-app')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    expect(
      await screen.findAllByText(contextualErrorMessages.createProjectGitLab.FORBIDDEN, undefined, { timeout: 5000 }),
    ).not.toHaveLength(0)
  })
})

describe('Wizard → import from GitLab (FR-141)', () => {
  it('FR141_tags_preselect_latest_and_suggest_next_minor: v1.0.0/v1.1.0 preselects v1.1.0, version 1.2.0, detection on GitLab', async () => {
    const { user, connection, repos } = await openRepository({ importing: true, repos: ['todo-app'] })
    const repo = first(repos)
    seedGitLabRefs(repo, {
      tags: [
        { name: 'v1.0.0', sha: 'a1', semver: '1.0.0' },
        { name: 'v1.1.0', sha: 'a2', semver: '1.1.0' },
      ],
      preselectedTag: 'v1.1.0',
      suggestedDevelopmentVersion: '1.2.0',
    })
    // Importing offers no Create.
    await screen.findByRole('list', { name: 'GitLab projects' })
    expect(screen.queryByRole('button', { name: 'Create GitLab project' })).not.toBeInTheDocument()

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
    expect(detectBodies()[0]?.git).toEqual({
      provider: 'gitlab',
      repo: repo.id,
      ref: 'v1.1.0',
      instanceId: GITLAB_COM_ID,
      connectionId: connection.id,
    })

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
      git: { mode: 'gitlab', repoId: repo.id, connectionId: connection.id, instanceId: GITLAB_COM_ID },
    })
  })

  it('FR141_empty_project: an empty GitLab project is explained and offers Start from scratch', async () => {
    const { user, repos } = await openRepository({ importing: true, repos: ['blank'] })
    seedGitLabRefs(first(repos), { empty: true })

    await pick(user, 'acme/blank')

    expect(await screen.findByText('This project is empty')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Never released' })).toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Start from scratch instead' }))

    await waitFor(() => {
      expect(screen.queryByText('This project is empty')).not.toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: 'Create GitLab project' })).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Currently released version' })).not.toBeInTheDocument()
  })

  it('FR141_tag_not_found: TAG_NOT_FOUND on create returns to the released version and reloads the tags', async () => {
    server.use(http.post('*/api/projects', () => problem(400, 'TAG_NOT_FOUND', 'The tag was not found.')))
    const { user, repos } = await openRepository({ importing: true, repos: ['todo-app'] })
    seedGitLabRefs(first(repos), {
      tags: [{ name: 'v1.1.0', sha: 'a2', semver: '1.1.0' }],
      preselectedTag: 'v1.1.0',
      suggestedDevelopmentVersion: '1.2.0',
    })
    await pick(user, 'acme/todo-app')
    await screen.findByRole('group', { name: 'Currently released version' })
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await versionToReview(user)

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await stepHeading(/Step 3 of 8: Repository/)
    expect(
      await screen.findAllByText(contextualErrorMessages.createProject.TAG_NOT_FOUND, undefined, { timeout: 5000 }),
    ).not.toHaveLength(0)
  })
})
