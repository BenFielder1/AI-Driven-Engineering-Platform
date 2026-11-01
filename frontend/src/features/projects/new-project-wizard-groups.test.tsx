import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { projectsDb } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'
import { deploymentSettingsPolling } from '../deployments/deployments-api'
import { clearWizardDraft } from './wizard-draft'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })
const layerHeading = (name: string) => screen.findByRole('heading', { level: 3, name })
const pick = async (user: User, name: string) => {
  await user.click(await screen.findByRole('radio', { name }))
}
const requests = (method: string, suffix: string) => projectsDb.requests.filter((r) => r.method === method && r.path.endsWith(suffix))

interface Stack {
  frontend?: string
  backend?: string
  data?: string
  hosts: [string, string, string]
}

const pickHosts = async (user: User, [frontend, backend, database]: Stack['hosts']) => {
  await user.click(within(await screen.findByRole('group', { name: 'Frontend host' })).getByRole('radio', { name: frontend }))
  await user.click(within(screen.getByRole('group', { name: 'Backend host' })).getByRole('radio', { name: backend }))
  await user.click(within(screen.getByRole('group', { name: 'Database host' })).getByRole('radio', { name: database }))
}

const toStepAfterStack = async (user: User) => {
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
}

/** Through the wizard with `stack`, to the Deployment step. */
async function toDeployment({ frontend = 'React + Vite', backend = 'ASP.NET Core', data = 'EF Core + PostgreSQL', hosts }: Stack) {
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  for (const provider of ['railway', 'render', 'vercel', 'netlify'] as const) {
    seedPlatformConnection({ userId: me.id, provider, label: `${provider} key`, token: `tok_${provider}1111` })
  }
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await stepHeading(/Step 1 of 8: Source/)
  await next(user)
  await user.type(screen.getByLabelText('Project name'), 'Storefront')
  await user.type(screen.getByLabelText('Your idea'), 'Sell posters online.')
  await next(user)
  await user.type(await screen.findByLabelText('Repository URL'), 'https://github.com/ada/storefront')
  await next(user)
  await stepHeading(/Step 4 of 8: Version/)
  await next(user)
  await stepHeading(/Step 5 of 8: Stack/)
  await user.click(screen.getByRole('radio', { name: 'Choose the stack' }))
  await layerHeading('Frontend')
  await pick(user, frontend)
  await next(user)
  await layerHeading('Backend')
  await pick(user, backend)
  await next(user)
  await layerHeading('Data')
  await pick(user, data)
  await next(user)
  await layerHeading('Version control')
  await next(user)
  await layerHeading('Deployment')
  await pickHosts(user, hosts)
  await toStepAfterStack(user)
  return user
}

const RAILWAY: Stack['hosts'] = ['Railway', 'Railway', 'Railway Postgres']
const ALL_ON_RAILWAY = 'Frontend, backend and database on Railway'

/** Each window's title, in order. */
const windowTitles = () => [...document.querySelectorAll('[data-testid^="wizard-host-"] h3')].map((h) => h.textContent)

const window_ = (name: string) => screen.findByRole('region', { name })

const tick = async (user: User, windowName: string, part: string) => {
  const region = await window_(windowName)
  await user.click(within(within(region).getByRole('group', { name: 'Deploy parts to separate projects' })).getByRole('checkbox', { name: part }))
}

const settled = () => waitFor(() => { expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled() })

beforeEach(() => {
  deploymentSettingsPolling.setupMs = 10
})

afterEach(() => {
  deploymentSettingsPolling.setupMs = 2_000
  clearWizardDraft()
})

describe('Wizard → deployment groups: §4.28 worked examples (FR-188, FR-189)', () => {
  it('FR188_railway_three_times_gives_exactly_one_window', async () => {
    await toDeployment({ hosts: RAILWAY })
    const group = await window_(ALL_ON_RAILWAY)
    expect(windowTitles()).toEqual([ALL_ON_RAILWAY])
    expect(within(group).getByText('This covers both the production and the preview database.')).toBeInTheDocument()
    expect(within(group).getByRole('group', { name: 'Deploy parts to separate projects' })).toBeInTheDocument()
    expect(within(group).getByRole('radio', { name: 'Set up later' })).toBeChecked()
  })

  it('FR188_netlify_render_render_gives_two_windows_with_the_group_first', async () => {
    await toDeployment({ hosts: ['Netlify', 'Render', 'Render Postgres'] })
    await window_('Backend and database on Render')
    expect(windowTitles()).toEqual(['Backend and database on Render', 'Frontend on Netlify'])
    // Netlify shares a platform with nothing, so it has no "separate" option (§4.28 row 8).
    expect(within(await window_('Frontend on Netlify')).queryByRole('group', { name: 'Deploy parts to separate projects' })).not.toBeInTheDocument()
  })

  it('FR189_ticking_the_database_gives_two_windows_and_shows_the_caveat_as_text', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    await tick(user, ALL_ON_RAILWAY, 'Database')
    const database = await window_('Database on Railway Postgres')
    expect(windowTitles()).toEqual(['Frontend and backend on Railway', 'Database on Railway Postgres'])
    expect(await within(database).findByTestId('window-caveat')).toHaveTextContent('Separate Railway project: no private network, public connection, higher latency')
    expect(within(database).getByRole('checkbox', { name: 'Database' })).toBeChecked()
    // The tick's checkbox moved with its part: focus went with it, not back to the page.
    await waitFor(() => {
      expect(within(database).getByRole('checkbox', { name: 'Database' })).toHaveFocus()
    })
    expect(requests('POST', '/api/stack/options').at(-1)?.body).toMatchObject({ deploy: { separateParts: ['database'] } })
  })

  it('FR189_all_three_ticked_gives_three_windows_stored_as_separate_parts', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    await tick(user, ALL_ON_RAILWAY, 'Database')
    await tick(user, 'Frontend and backend on Railway', 'Frontend')
    await tick(user, 'Backend on Railway', 'Backend')
    await window_('Frontend on Railway')
    expect(windowTitles()).toEqual(['Frontend on Railway', 'Backend on Railway', 'Database on Railway Postgres'])

    await settled()
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })
    const body = requests('POST', '/api/projects')[0]?.body as { stack: { deploy: { separateParts: string[] } } }
    expect([...body.stack.deploy.separateParts].sort()).toEqual(['backend', 'database', 'frontend'])
  })

  it('FR189_unticking_brings_the_part_back_into_the_group', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    await tick(user, ALL_ON_RAILWAY, 'Database')
    await tick(user, 'Database on Railway Postgres', 'Database')
    await window_(ALL_ON_RAILWAY)
    expect(windowTitles()).toEqual([ALL_ON_RAILWAY])
    expect(screen.queryByTestId('window-caveat')).not.toBeInTheDocument()
  })

  it('FR189_splitting_the_frontend_states_that_same_origin_is_gone_and_does_not_send_it', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    await tick(user, ALL_ON_RAILWAY, 'Frontend')
    expect(await screen.findByText('Same-origin on Railway needs the frontend and backend in one project')).toBeVisible()
    await settled()
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })
    expect(requests('POST', '/api/projects')[0]?.body).not.toMatchObject({ stack: { originModel: 'same-origin' } })
  })

  it('FR189_a_part_R13_removed_shows_the_server_caveat', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    await tick(user, ALL_ON_RAILWAY, 'Database')
    await settled()
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await stepHeading(/Step 6 of 8: Configuration/)
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await layerHeading('Deployment')
    await pickHosts(user, ['Railway', 'Railway', 'External'])
    await toStepAfterStack(user)
    expect(await screen.findByText("The database doesn't share a platform with another part, so it can't be split off.")).toBeVisible()
  })

  it('FR192_vercel_window_says_each_part_keeps_its_own_project', async () => {
    await toDeployment({ backend: 'FastAPI', data: 'SQLAlchemy', hosts: ['Vercel', 'Vercel', 'Supabase'] })
    const group = await window_('Frontend and backend on Vercel')
    expect(within(group).getByText('Vercel keeps each part in its own project; these use the same team and name.')).toBeVisible()
    expect(windowTitles()).toEqual(['Frontend and backend on Vercel', 'Production database on Supabase', 'Preview database on Supabase'])
    expect(within(group).getByRole('radio', { name: 'Create new projects' })).toBeInTheDocument()
  })
})

describe('Wizard → setting up a group after create (FR-188, FR-190)', () => {
  it('FR188_a_created_group_takes_one_put_with_no_client_side_wait', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    const group = await window_(ALL_ON_RAILWAY)
    await user.click(within(group).getByRole('radio', { name: 'Create a new project' }))
    await next(user)
    expect((await screen.findAllByText('Pick one of your Railway connections, or set this up later.')).length).toBeGreaterThan(0)

    await user.selectOptions(await within(group).findByLabelText('Railway connection'), within(group).getByRole('option', { name: /railway key/ }))
    expect(within(group).getByLabelText('Name')).toHaveValue('storefront')
    expect(within(group).queryByLabelText('Region (optional)')).not.toBeInTheDocument()
    await next(user)
    await stepHeading(/Step 8 of 8/)
    expect(screen.getByRole('region', { name: 'Deployment' })).toHaveTextContent('New project storefront')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })

    expect(requests('PUT', '/groups/railway')[0]?.body).toEqual({ connectionId: expect.any(String) as string, create: { name: 'storefront' } })
    for (const suffix of ['/targets/frontend', '/targets/backend', '/databases/production', '/databases/preview']) {
      expect(requests('PUT', suffix)).toHaveLength(0)
    }
  })

  it('FR190_link_picks_a_project_then_a_service_per_part_or_create_in_this_project', async () => {
    const user = await toDeployment({ hosts: RAILWAY })
    const group = await window_(ALL_ON_RAILWAY)
    await user.click(within(group).getByRole('radio', { name: 'Link an existing project' }))
    await user.selectOptions(await within(group).findByLabelText('Railway connection'), within(group).getByRole('option', { name: /railway key/ }))
    await user.selectOptions(await within(group).findByLabelText('Existing project'), 'projects_1')

    const frontend = await within(group).findByLabelText('Frontend service')
    // Only the chosen project's services are offered.
    expect(within(frontend).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Create in this project',
      'shop-web · inventory-projects',
      'shop-api · inventory-projects',
    ])
    await user.selectOptions(frontend, 'projects_1:svc_web')
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })

    expect(requests('PUT', '/groups/railway')[0]?.body).toEqual({
      connectionId: expect.any(String) as string,
      link: { resourceId: 'projects_1', parts: { frontend: 'projects_1:svc_web' } },
    })
  })

  it('FR188_a_render_database_outside_a_group_still_waits_for_its_target', async () => {
    const user = await toDeployment({ hosts: ['Netlify', 'Render', 'Render Postgres'] })
    await tick(user, 'Backend and database on Render', 'Database')
    const database = await window_('Database on Render Postgres')
    expect(within(database).getByText(/Add the preview database in Settings/)).toBeVisible()
    await settled()
    await user.click(within(await window_('Backend on Render')).getByRole('radio', { name: /^Create a new Render/ }))
    const backend = await window_('Backend on Render')
    await user.selectOptions(await within(backend).findByLabelText('Render connection'), within(backend).getByRole('option', { name: /render key/ }))
    await user.click(within(database).getByRole('radio', { name: /^Create a new Render/ }))
    await user.selectOptions(await within(database).findByLabelText('Render connection'), within(database).getByRole('option', { name: /render key/ }))
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })

    expect(requests('PUT', '/groups/render')).toHaveLength(0)
    const paths = projectsDb.requests.map((r) => r.path)
    expect(paths.findIndex((p) => p.endsWith('/databases/production'))).toBeGreaterThan(paths.findIndex((p) => p.endsWith('/targets/backend')))
    expect(requests('PUT', '/databases/preview')).toHaveLength(0)
  })
})
