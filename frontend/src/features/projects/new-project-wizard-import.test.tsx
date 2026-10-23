import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { contextualErrorMessages } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { configureGitHubApp, connectGitHub, githubDb, seedRepo } from '../../mocks/handlers/github'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })

/** A non-empty repo with releases v2.2.0 and v2.3.0 (latest), semver tags and one non-semver tag. */
function seedShop() {
  const repo = seedRepo({ owner: 'ada', name: 'shop', defaultBranch: 'trunk', empty: null })
  githubDb.refs[repo.fullName] = {
    defaultBranch: 'trunk',
    empty: false,
    releases: [
      { tagName: 'v2.2.0', name: 'Spring', publishedAt: '2026-05-01T10:00:00Z', isLatest: false },
      { tagName: 'v2.3.0', name: 'Summer', publishedAt: '2026-08-01T10:00:00Z', isLatest: true },
    ],
    tags: [
      { name: 'v2.3.0', sha: 'a1', semver: '2.3.0' },
      { name: 'v2.2.0', sha: 'a2', semver: '2.2.0' },
      { name: 'v1.0.0', sha: 'a3', semver: '1.0.0' },
      { name: 'nightly', sha: 'a4', semver: null },
    ],
  }
  return repo
}

/** Signs in (App registered and connected unless `app: false`), chooses Import and fills the Request step. */
async function openImport({ app = true } = {}) {
  const me = signInAs({ systemRole: 'user' })
  if (app) {
    configureGitHubApp()
    connectGitHub(me.id)
  }
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await user.click(screen.getByRole('radio', { name: 'Import an existing repository' }))
  await next(user)
  await stepHeading(/Step 2 of 8: Request/)
  await user.type(screen.getByLabelText('Project name'), 'Shop')
  await user.type(screen.getByLabelText('What should this version deliver?'), 'Add CSV export')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  return { user }
}

async function pickShop(user: User) {
  const list = await screen.findByRole('list', { name: 'Repositories' })
  await user.click(within(list).getByRole('radio', { name: 'ada/shop' }))
  await screen.findByRole('group', { name: 'Currently released version' })
}

/** Version → Configuration → Review & create. */
async function toReview(user: User) {
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
  await next(user)
  await stepHeading(/Step 8 of 8: Review & create/)
}

const createBody = () =>
  projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/projects')?.body as
    | Schemas['CreateProjectRequest']
    | undefined

function projectHeader(name: string): HTMLElement {
  const header = screen.getByRole('heading', { level: 1, name }).closest('header')
  if (!header) {
    throw new Error('No project header')
  }
  return header
}

describe('Wizard → Import an existing repository (FR-73)', () => {
  it('FR73_latest_release_preselected_and_next_minor_prefilled: v2.3.0 → 2.4.0, sent with source import; badges after creation', async () => {
    seedShop()
    const { user } = await openImport()
    await pickShop(user)

    const group = screen.getByRole('group', { name: 'Currently released version' })
    expect(within(group).getAllByRole('radio').map((r) => r.getAttribute('value'))).toEqual([
      'v2.3.0',
      'v2.2.0',
      'v1.0.0',
      '__never_released__',
    ])
    expect(within(group).getByRole('radio', { name: 'v2.3.0' })).toBeChecked()
    expect(within(group).getByRole('radio', { name: 'v2.3.0' })).toHaveAccessibleDescription(
      'Latest GitHub release · Summer · published Aug 1, 2026',
    )
    expect(within(group).queryByRole('radio', { name: 'nightly' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Default branch')).toHaveValue('trunk')
    // An import doesn't get the scratch warning about existing code.
    expect(screen.queryByText('This repository has code. AIDE will build on it')).not.toBeInTheDocument()

    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    expect(screen.getByLabelText('Version to develop')).toHaveValue('2.4.0')

    await toReview(user)
    const repository = screen.getByRole('region', { name: 'Repository' })
    expect(within(repository).getByText('v2.3.0')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Version' })).getByText('2.4.0')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Source' })).getByText('Import an existing repository')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Request' })).getByText('Add CSV export')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Shop' })).toBeInTheDocument()
    expect(createBody()).toMatchObject({
      name: 'Shop',
      idea: 'Add CSV export',
      source: 'import',
      import: { releasedTag: 'v2.3.0', developmentVersion: '2.4.0' },
      git: { mode: 'github_app', installationId: 9001, repoFullName: 'ada/shop', defaultBranch: 'trunk' },
    })
    expect(createBody()).not.toHaveProperty('initialVersion')
    const badges = within(projectHeader('Shop')).getByTestId('version-badges')
    expect(badges).toHaveTextContent('Released 2.3.0')
    expect(badges).toHaveTextContent('In development 2.4.0')
  })

  it('FR73_version_validated_live_against_the_released_version', async () => {
    seedShop()
    const { user } = await openImport()
    await pickShop(user)
    await user.click(screen.getByRole('radio', { name: 'v2.2.0' }))
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)

    const input = screen.getByLabelText('Version to develop')
    expect(input).toHaveValue('2.3.0')
    await user.clear(input)
    await user.type(input, '2.2.0')
    // Live: no Next needed.
    expect(await screen.findAllByText('The version must be greater than the released version 2.2.0.')).not.toHaveLength(0)
    expect(input).toHaveAttribute('aria-invalid', 'true')

    await user.clear(input)
    await user.type(input, 'v3.0.0')
    await waitFor(() => {
      expect(input).not.toHaveAttribute('aria-invalid', 'true')
    })
    await toReview(user)
    expect(within(screen.getByRole('region', { name: 'Version' })).getByText('3.0.0')).toBeInTheDocument()
  })

  it('FR73_never_released: releasedTag is sent as null and the version defaults to 0.1.0', async () => {
    seedShop()
    const { user } = await openImport()
    await pickShop(user)
    await user.click(screen.getByRole('radio', { name: 'Never released' }))
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    expect(screen.getByLabelText('Version to develop')).toHaveValue('0.1.0')

    await toReview(user)
    expect(within(screen.getByRole('region', { name: 'Repository' })).getByText('Never released')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Shop' })).toBeInTheDocument()
    expect(createBody()?.import).toEqual({ releasedTag: null, developmentVersion: '0.1.0' })
    const badges = within(projectHeader('Shop')).getByTestId('version-badges')
    expect(badges).toHaveTextContent('In development 0.1.0')
    expect(badges).not.toHaveTextContent('Released')
  })

  it('FR73_tag_not_found: the server copy is shown on the released version, back on the Repository step', async () => {
    seedShop()
    server.use(http.post('*/api/projects', () => problem(400, 'TAG_NOT_FOUND', 'The tag v2.3.0 was not found.')))
    const { user } = await openImport()
    await pickShop(user)
    await next(user)
    await stepHeading(/Step 4 of 8/)
    await toReview(user)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 3 of 8: Repository/)).toBeInTheDocument()
    expect(screen.getAllByText(contextualErrorMessages.createProject.TAG_NOT_FOUND)).not.toHaveLength(0)
  })

  it('FR73_version_not_greater: the server copy is shown on the version to develop', async () => {
    seedShop()
    server.use(http.post('*/api/projects', () => problem(409, 'VERSION_NOT_GREATER', 'Not greater.')))
    const { user } = await openImport()
    await pickShop(user)
    await next(user)
    await stepHeading(/Step 4 of 8/)
    await toReview(user)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 4 of 8: Version/)).toBeInTheDocument()
    expect(screen.getAllByText(contextualErrorMessages.createProject.VERSION_NOT_GREATER)).not.toHaveLength(0)
    expect(screen.getByLabelText('Version to develop')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR73_local_import_path: only a Local import asks for the server path; a typed tag sets the released version', async () => {
    const { user } = await openImport({ app: false })
    const pathLabel = /Path of an existing repository on the server \(development only\)/
    expect(screen.queryByLabelText(pathLabel)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Tag of the currently released version (optional)')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
    const path = screen.getByLabelText(pathLabel)
    await next(user)
    expect(await screen.findAllByText('Enter the path of the repository to import.')).not.toHaveLength(0)
    await user.type(path, 'fixtures/shop')
    await user.type(screen.getByLabelText('Tag of the currently released version (optional)'), 'v1.4.0')
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    expect(screen.getByLabelText('Version to develop')).toHaveValue('1.5.0')
    await toReview(user)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Shop' })).toBeInTheDocument()
    expect(createBody()?.git).toEqual({ mode: 'local', defaultBranch: 'main', localSourcePath: 'fixtures/shop' })
    expect(createBody()?.import).toEqual({ releasedTag: 'v1.4.0', developmentVersion: '1.5.0' })
  })

  it('FR73_unknown_repo: a 404 on a URL + token import is shown on the repository URL', async () => {
    server.use(http.post('*/api/projects', () => problem(404, 'NOT_FOUND', 'Repository not found.')))
    const { user } = await openImport({ app: false })
    await user.type(screen.getByLabelText('Repository URL'), 'https://github.com/ada/missing')
    await next(user)
    await stepHeading(/Step 4 of 8/)
    await toReview(user)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 3 of 8: Repository/)).toBeInTheDocument()
    expect(screen.getAllByText(contextualErrorMessages.createProject.NOT_FOUND)).not.toHaveLength(0)
    expect(screen.getByLabelText('Repository URL')).toHaveAttribute('aria-invalid', 'true')
  })

  it('FR86_import_instead: a non-empty repo in "start from scratch" offers Import instead, as never released', async () => {
    const me = signInAs({ systemRole: 'user' })
    configureGitHubApp()
    connectGitHub(me.id)
    seedShop()
    await renderRoute('/projects/new')
    const user = userEvent.setup()
    await stepHeading(/Step 1 of 8: Source/)
    await next(user)
    await user.type(await screen.findByLabelText('Project name'), 'Shop')
    await user.type(screen.getByLabelText('Your idea'), 'A shop')
    await next(user)
    await stepHeading(/Step 3 of 8: Repository/)
    await user.click(within(await screen.findByRole('list', { name: 'Repositories' })).getByRole('radio', { name: 'ada/shop' }))

    expect(await screen.findByText('This repository has code. AIDE will build on it')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Import instead' }))

    const group = await screen.findByRole('group', { name: 'Currently released version' })
    expect(within(group).getByRole('radio', { name: 'Never released' })).toBeChecked()
    expect(screen.queryByText('This repository has code. AIDE will build on it')).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Wizard steps' })).getByText(/2\. Request/)).toBeInTheDocument()
  })
})
