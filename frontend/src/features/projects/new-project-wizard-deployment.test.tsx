import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { signInAs } from '../../mocks/handlers/auth'
import { seedVercelConnection, VERCEL_INSTALL_URL, vercelDb } from '../../mocks/handlers/deployments'
import { projectsDb } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'

type User = ReturnType<typeof userEvent.setup>

const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })
const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))

/** Scratch → idea → repository (`local` or a GitHub URL) → Version → Stack → Configuration → the Deployment step. */
async function toDeployment(repo: 'github' | 'local', seed?: (me: string) => void) {
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  seed?.(me.id)
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await next(user)
  await stepHeading(/Step 2 of 8: Project idea/)
  await user.type(screen.getByLabelText('Project name'), 'Inventory Tracker')
  await user.type(screen.getByLabelText('Your idea'), 'Track stock across warehouses.')
  await next(user)
  await stepHeading(/Step 3 of 8: Repository/)
  if (repo === 'local') {
    await user.click(screen.getByRole('radio', { name: 'Local repository' }))
  } else {
    await user.type(screen.getByLabelText('Repository URL'), 'https://github.com/acme/inventory')
  }
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
  return user
}

const createBody = () =>
  projectsDb.requests.find((r) => r.method === 'POST' && r.path === '/api/projects')?.body as Schemas['CreateProjectRequest'] | undefined

describe('Wizard → Deployment step (FR-88)', () => {
  it('FR88_wizard_skip_by_default: skipping sends no deployment', async () => {
    const user = await toDeployment('github')
    expect(screen.getByRole('radio', { name: /Skip for now/ })).toBeChecked()
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    expect(within(screen.getByRole('region', { name: 'Deployment' })).getByText('Skipped')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Inventory Tracker' })
    expect(createBody()).not.toHaveProperty('deployment')
  })

  it('FR88_wizard_vercel_create: an own connection and a new Vercel project are sent as `deployment`', async () => {
    let connectionId = ''
    const user = await toDeployment('github', (me) => {
      connectionId = seedVercelConnection({ userId: me, label: 'Acme team', token: 'vcp_zzzzab12', teamSlug: 'acme', scope: 'team' }).id
    })
    await user.click(screen.getByRole('radio', { name: /^Vercel/ }))
    const picker = await screen.findByLabelText('Vercel connection')
    await next(user)
    expect((await screen.findAllByText('Pick one of your Vercel connections, or skip deployment.')).length).toBeGreaterThan(0)

    await user.selectOptions(picker, 'Acme team (••••ab12) · team acme')
    expect(screen.getByLabelText('Vercel project name')).toHaveValue('inventory-tracker')
    await user.type(screen.getByLabelText('Root directory (optional)'), 'web')
    await next(user)
    await stepHeading(/Step 8 of 8: Review & create/)
    const summary = screen.getByRole('region', { name: 'Deployment' })
    expect(summary).toHaveTextContent('Vercel')
    expect(await within(summary).findByText('Acme team (••••ab12)')).toBeInTheDocument()
    expect(summary).toHaveTextContent('New project inventory-tracker (root web)')

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Inventory Tracker' })
    expect(createBody()?.deployment).toEqual({
      provider: 'vercel',
      connectionId,
      create: { name: 'inventory-tracker', rootDirectory: 'web' },
    })
    expect(projectsDb.projects[0]?.deployment?.provider).toBe('vercel')
  })

  it('FR88_wizard_git_not_connected: VERCEL_GIT_NOT_CONNECTED returns to the Deployment step with Vercel\'s install link', async () => {
    vercelDb.gitNotConnected = true
    const user = await toDeployment('github', (me) => {
      seedVercelConnection({ userId: me, label: 'Personal', token: 'vcp_q1w2' })
    })
    await user.click(screen.getByRole('radio', { name: /^Vercel/ }))
    await user.selectOptions(await screen.findByLabelText('Vercel connection'), 'Personal (••••q1w2)')
    await user.click(screen.getByRole('radio', { name: /Link an existing Vercel project/ }))
    await user.type(screen.getByLabelText('Vercel project id or name'), 'prj_shop')
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(await stepHeading(/Step 7 of 8: Deployment/)).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: /Install Vercel's GitHub App/ })).toHaveAttribute('href', VERCEL_INSTALL_URL)
    expect(screen.getByText(/We couldn't create the project/)).toBeInTheDocument()
    expect(projectsDb.projects).toHaveLength(0)
  })

  it('FR88_wizard_no_connections: Vercel without a token points to Connections', async () => {
    const user = await toDeployment('github')
    await user.click(screen.getByRole('radio', { name: /^Vercel/ }))
    expect(await screen.findByText('Add a Vercel token first')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Connections → Vercel' })).toHaveAttribute('href', '/connections')
  })

  it('FR88_wizard_local_server_decides: a Local project may pick Vercel; the server\'s VALIDATION returns to the step', async () => {
    const user = await toDeployment('local', (me) => {
      seedVercelConnection({ userId: me, label: 'Personal', token: 'vcp_q1w2' })
    })
    await user.click(screen.getByRole('radio', { name: /^Vercel/ }))
    expect(screen.getByText('Vercel deploys from GitHub')).toBeInTheDocument()
    await user.selectOptions(await screen.findByLabelText('Vercel connection'), 'Personal (••••q1w2)')
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    expect(await stepHeading(/Step 7 of 8: Deployment/)).toBeInTheDocument()
    expect((await screen.findAllByText('A Local project cannot deploy to Vercel.')).length).toBeGreaterThan(0)
    expect(projectsDb.projects).toHaveLength(0)
  })
})
