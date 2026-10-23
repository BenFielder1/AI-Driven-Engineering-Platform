import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'

const TOKEN = 'ghp_supersecretvalue123'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })

/** Opens the wizard and passes the Source step with "Start from scratch". */
async function startWizard() {
  signInAs({ systemRole: 'user' })
  const utils = await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  expect(screen.getByRole('radio', { name: 'Start from scratch' })).toBeChecked()
  await next(user)
  await stepHeading(/Step 2 of 8: Project idea/)
  return { ...utils, user }
}

async function fillIdea(user: User) {
  await user.type(screen.getByLabelText('Project name'), 'Inventory tracker')
  await user.type(screen.getByLabelText('Your idea'), 'Track stock across warehouses.')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
}

/** From the Repository step with Local chosen: Version (default kept) → Configuration. */
async function localToConfiguration(user: User) {
  await user.click(screen.getByRole('radio', { name: 'Local repository' }))
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
}

describe('New project wizard (FR-5, FR-73)', () => {
  it('FR73_source_step_first: the wizard starts with Source, scratch selected', async () => {
    signInAs({ systemRole: 'user' })
    await renderRoute('/projects/new')
    await stepHeading(/Step 1 of 8: Source/)
    const steps = screen.getByRole('list', { name: 'Wizard steps' })
    expect(within(steps).getAllByRole('listitem').map((li) => li.textContent.replace('Completed: ', ''))).toEqual([
      '1. Source',
      '2. Project idea',
      '3. Repository',
      '4. Version',
      '5. Stack',
      '6. Configuration',
      '7. Deployment',
      '8. Review & create',
    ])
    expect(screen.getByRole('radio', { name: 'Import an existing repository' })).not.toBeChecked()
  })

  it('FR-5: the idea step requires a name and an idea, and focuses the first invalid field', async () => {
    const { user } = await startWizard()

    await next(user)

    expect(await screen.findAllByText('Enter a project name.')).not.toHaveLength(0)
    expect(screen.getAllByText('Describe your idea: what should the software do, and for whom?')).not.toHaveLength(0)
    expect(screen.getByLabelText('Project name')).toHaveFocus()
    expect(screen.getByLabelText('Project name')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('heading', { level: 2, name: /Step 2 of 8/ })).toBeInTheDocument()
  })

  it('FR-5: the step indicator marks the current step, and focus moves to the new step heading', async () => {
    const { user } = await startWizard()
    const steps = screen.getByRole('list', { name: 'Wizard steps' })
    expect(within(steps).getByText(/2\. Project idea/).closest('li')).toHaveAttribute('aria-current', 'step')

    await fillIdea(user)

    expect(screen.getByRole('heading', { level: 2, name: /Step 3 of 8/ })).toHaveFocus()
    expect(within(steps).getByText(/3\. Repository/).closest('li')).toHaveAttribute('aria-current', 'step')
  })

  it('FR-5: configuration shows the defaults (advisory, 0.90, 20, 3) and validates the ranges', async () => {
    const { user } = await startWizard()
    await fillIdea(user)
    await localToConfiguration(user)

    expect(screen.getByRole('radio', { name: 'Advisory' })).toBeChecked()
    expect(screen.getByLabelText('Compliance threshold')).toHaveValue(0.9)
    expect(screen.getByLabelText('Max files per step')).toHaveValue(20)
    expect(screen.getByLabelText('Max review cycles')).toHaveValue(3)
    expect(screen.getByRole('radio', { name: 'Strict' })).toHaveAccessibleDescription(
      'An Owner approves the spec, the plan and the release before each phase starts.',
    )

    await user.clear(screen.getByLabelText('Compliance threshold'))
    await user.type(screen.getByLabelText('Compliance threshold'), '1.5')
    await user.type(screen.getByLabelText('Token budget (optional)'), '500')
    await next(user)

    expect(
      await screen.findAllByText('Enter a compliance threshold from 0.50 to 1.00 (two decimal places at most).'),
    ).not.toHaveLength(0)
    expect(
      screen.getAllByText('Enter a whole number of at least 1,000 tokens, or leave it empty for no limit.'),
    ).not.toHaveLength(0)
    expect(screen.getByRole('heading', { level: 2, name: /Step 6 of 8/ })).toBeInTheDocument()
  })

  it('FR-5: the repository step requires a GitHub URL; Local explains the managed repository instead', async () => {
    const { user } = await startWizard()
    await fillIdea(user)

    await next(user)
    expect(await screen.findAllByText('Enter the GitHub repository URL.')).not.toHaveLength(0)

    await user.type(screen.getByLabelText('Repository URL'), 'https://gitlab.com/a/b')
    await next(user)
    expect(await screen.findAllByText('Enter a URL like https://github.com/owner/repo.')).not.toHaveLength(0)

    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
    expect(screen.queryByLabelText('Repository URL')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Access token (optional)')).not.toBeInTheDocument()
    expect(screen.getByText(/AIDE creates and keeps a local Git repository/)).toBeInTheDocument()
    // FR-73: the server path field is for Local imports only.
    expect(screen.queryByLabelText(/Path of an existing repository on the server/)).not.toBeInTheDocument()

    await next(user)
    expect(await stepHeading(/Step 4 of 8: Version/)).toBeInTheDocument()
  })

  it('FR66_initial_version_defaults_to_0_1_0_and_is_validated', async () => {
    const { user } = await startWizard()
    await fillIdea(user)
    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)

    const input = screen.getByLabelText('Initial version')
    expect(input).toHaveValue('0.1.0')
    await user.clear(input)
    await user.type(input, '01.0.0')
    await next(user)
    expect(await screen.findAllByText(/Enter a version as MAJOR\.MINOR\.PATCH/)).not.toHaveLength(0)
    expect(screen.getByLabelText('Initial version')).toHaveAttribute('aria-invalid', 'true')

    await user.clear(screen.getByLabelText('Initial version'))
    await user.type(screen.getByLabelText('Initial version'), 'v1.0.0')
    await next(user)
    await stepHeading(/Step 5 of 8: Stack/)
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    expect(within(screen.getByRole('region', { name: 'Version' })).getByText('1.0.0')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Inventory tracker' })).toBeInTheDocument()
    const body = projectsDb.requests.find((r) => r.method === 'POST')?.body as Schemas['CreateProjectRequest']
    expect(body.initialVersion).toBe('1.0.0')
    expect(body.source).toBe('scratch')
    expect(body).not.toHaveProperty('import')
    expect(projectsDb.projects[0]?.releaseVersions.map((v) => [v.version, v.status])).toEqual([['1.0.0', 'in_development']])
  })

  it('FR-5: Back keeps the entered values', async () => {
    const { user } = await startWizard()
    await fillIdea(user)

    await user.click(screen.getByRole('button', { name: 'Back' }))

    expect(await screen.findByLabelText('Project name')).toHaveValue('Inventory tracker')
    expect(screen.getByRole('heading', { level: 2, name: /Step 2 of 8/ })).toHaveFocus()
  })

  it('FR-5: review shows a summary without the token, and Create navigates to the new project', async () => {
    const { user, router } = await startWizard()
    await fillIdea(user)
    await user.type(screen.getByLabelText('Repository URL'), 'https://github.com/acme/inventory')
    const tokenInput = screen.getByLabelText('Access token (optional)')
    expect(tokenInput).toHaveAttribute('type', 'password')
    await user.type(tokenInput, TOKEN)
    await next(user)
    await stepHeading(/Step 4 of 8: Version/)
    await next(user)
    await stepHeading(/Step 5 of 8: Stack/)
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await user.click(screen.getByRole('radio', { name: 'Strict' }))
    await user.type(screen.getByLabelText('Token budget (optional)'), '50000')
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)

    const git = await screen.findByRole('region', { name: 'Repository' })
    expect(within(git).getByText('https://github.com/acme/inventory')).toBeInTheDocument()
    expect(within(git).getByText('Provided')).toBeInTheDocument()
    expect(screen.queryByText(TOKEN)).not.toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Source' })).getByText('Start from scratch')).toBeInTheDocument()
    const config = screen.getByRole('region', { name: 'Configuration' })
    expect(within(config).getByText('Strict')).toBeInTheDocument()
    expect(within(config).getByText('50,000 tokens')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Inventory tracker' })).toBeInTheDocument()
    const created = projectsDb.projects[0]
    expect(router.state.location.pathname).toBe(`/projects/${created?.id ?? ''}`)
    const body = projectsDb.requests.find((r) => r.method === 'POST')?.body as Schemas['CreateProjectRequest']
    expect(body).toEqual({
      name: 'Inventory tracker',
      idea: 'Track stock across warehouses.',
      approvalMode: 'strict',
      complianceThreshold: 0.9,
      maxFilesPerStep: 20,
      maxReviewCycles: 3,
      tokenBudget: 50000,
      source: 'scratch',
      initialVersion: '0.1.0',
      git: { mode: 'github_pat', repoUrl: 'https://github.com/acme/inventory', defaultBranch: 'main', token: TOKEN },
    })
  })

  it('FR-5: server validation errors send the user back to the step with the field', async () => {
    server.use(
      http.post('*/api/projects', () =>
        problem(400, 'VALIDATION', 'Invalid', { errors: { name: ['A project with this name already exists.'] } }),
      ),
    )
    const { user } = await startWizard()
    await user.type(screen.getByLabelText('Project name'), 'Inventory tracker')
    await user.type(screen.getByLabelText('Your idea'), 'An idea')
    await next(user)
    await stepHeading(/Step 3 of 8/)
    await localToConfiguration(user)
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)
    await next(user)
    await user.click(await screen.findByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 2 of 8/)).toBeInTheDocument()
    expect(screen.getAllByText('A project with this name already exists.')).not.toHaveLength(0)
    await waitFor(() => {
      expect(screen.getByLabelText('Project name')).toHaveAttribute('aria-invalid', 'true')
    })
  })
})
