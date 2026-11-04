import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { problem } from '../../mocks/handlers/problem'
import { projectsDb } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { renderRoute } from '../../test/render'
import { deploymentSettingsPolling } from '../deployments/deployments-api'
import { saveWizardDraft, readWizardDraft, clearWizardDraft } from './wizard-draft'
import { wizardDefaults } from './schemas'
import { hostChecks, wizardHosts } from './wizard-hosts'

type User = ReturnType<typeof userEvent.setup>

const next = (user: User) => user.click(screen.getByRole('button', { name: 'Next' }))
const stepHeading = (name: RegExp) => screen.findByRole('heading', { level: 2, name })
const layerHeading = (name: string) => screen.findByRole('heading', { level: 3, name })
const pick = async (user: User, name: string) => {
  await user.click(await screen.findByRole('radio', { name }))
}
const requests = (method: string, suffix: string) => projectsDb.requests.filter((r) => r.method === method && r.path.endsWith(suffix))

/** Through the wizard with React + ASP.NET + EF Core on Netlify, Render and `database`, to the Deployment step. */
async function toHostsStep(database = 'Render Postgres', seed?: (me: string) => void) {
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  seed?.(me.id)
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
  await pick(user, 'React + Vite')
  await next(user)
  await layerHeading('Backend')
  await pick(user, 'ASP.NET Core')
  await next(user)
  await layerHeading('Data')
  await pick(user, 'EF Core + PostgreSQL')
  await next(user)
  await layerHeading('Version control')
  await next(user)
  await layerHeading('Deployment')
  await user.click(within(await screen.findByRole('group', { name: 'Frontend host' })).getByRole('radio', { name: 'Netlify' }))
  await user.click(within(screen.getByRole('group', { name: 'Backend host' })).getByRole('radio', { name: 'Render' }))
  await user.click(within(screen.getByRole('group', { name: 'Database host' })).getByRole('radio', { name: database }))
  await next(user)
  await stepHeading(/Step 6 of 8: Configuration/)
  await next(user)
  await stepHeading(/Step 7 of 8: Deployment/)
  return user
}

const card = (name: string) => screen.findByRole('region', { name })

const GROUP = 'Backend and database on Render'

/** Chooses "create" on a card (`project`: a group's) and its only connection. */
async function createWith(user: User, name: string, platform: string, connection: RegExp) {
  const region = await card(name)
  await user.click(within(region).getByRole('radio', { name: name === GROUP ? 'Create a new project' : new RegExp(`^Create a new ${platform}`) }))
  await user.selectOptions(await within(region).findByLabelText(`${platform} connection`), within(region).getByRole('option', { name: connection }))
  return region
}

function seedHosts(me: string) {
  seedPlatformConnection({ userId: me, provider: 'netlify', label: 'Netlify key', token: 'nfp_aaaa1111' })
  seedPlatformConnection({ userId: me, provider: 'render', label: 'Render key', token: 'rnd_bbbb2222' })
}

beforeEach(() => {
  deploymentSettingsPolling.setupMs = 10
})

afterEach(() => {
  deploymentSettingsPolling.setupMs = 2_000
  clearWizardDraft()
})

describe('Wizard → Deployment step of a composed stack (§7, FR-115, FR-144, FR-152)', () => {
  it('S7_FR115_the_step_offers_the_hosts_the_stack_chose_and_setting_up_later_changes_nothing', async () => {
    const user = await toHostsStep()

    expect(await card('Frontend on Netlify')).toBeInTheDocument()
    expect(await card(GROUP)).toBeInTheDocument()
    expect(screen.queryByRole('radio', { name: /^Vercel/ })).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Readiness checks' })).getByText('Repository access')).toBeInTheDocument()
    for (const radio of screen.getAllByRole('radio', { name: 'Set up later' })) {
      expect(radio).toBeChecked()
    }

    await next(user)
    await stepHeading(/Step 8 of 8/)
    expect(within(screen.getByRole('region', { name: 'Deployment' })).getAllByText('Set up later')).toHaveLength(2)
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })
    expect(requests('PUT', '/targets/frontend')).toHaveLength(0)
    expect(requests('PUT', '/databases/production')).toHaveLength(0)
    expect(requests('POST', '/api/projects')[0]?.body).not.toHaveProperty('deployment')
  })

  it('S7_changing_a_host_resets_its_region_and_plan_and_a_typed_name_but_the_suggested_name_comes_back', async () => {
    const user = await toHostsStep('Render Postgres', seedHosts)
    const before = await createWith(user, GROUP, 'Render', /Render key/)
    await user.type(within(before).getByLabelText('Region (optional)'), 'frankfurt')
    await user.type(within(before).getByLabelText('Plan (optional)'), 'starter')
    await user.clear(within(before).getByLabelText('Name'))
    await user.type(within(before).getByLabelText('Name'), 'my-own')

    await user.click(screen.getByRole('button', { name: 'Back' }))
    await stepHeading(/Step 6 of 8: Configuration/)
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await layerHeading('Deployment')
    const backendHost = within(screen.getByRole('group', { name: 'Backend host' }))
    await user.click(backendHost.getByRole('radio', { name: 'Railway' }))
    await user.click(backendHost.getByRole('radio', { name: 'Render' }))
    await next(user)
    await stepHeading(/Step 6 of 8: Configuration/)
    await next(user)
    await stepHeading(/Step 7 of 8: Deployment/)

    const after = await createWith(user, GROUP, 'Render', /Render key/)
    expect(within(after).getByLabelText('Region (optional)')).toHaveValue('')
    expect(within(after).getByLabelText('Plan (optional)')).toHaveValue('')
    expect(within(after).getByLabelText('Name')).toHaveValue('storefront')
  })

  it('S7_FR144_a_target_needs_an_own_connection_and_is_set_up_with_the_same_endpoint_as_settings_after_create', async () => {
    const user = await toHostsStep('Render Postgres', seedHosts)
    const frontend = await card('Frontend on Netlify')
    await user.click(within(frontend).getByRole('radio', { name: /^Create a new Netlify/ }))
    await next(user)
    expect((await screen.findAllByText('Pick one of your Netlify connections, or set this up later.')).length).toBeGreaterThan(0)

    await user.selectOptions(await within(frontend).findByLabelText('Netlify connection'), within(frontend).getByRole('option', { name: /Netlify key/ }))
    expect(within(frontend).getByLabelText('Name')).toHaveValue('storefront-web')
    await createWith(user, GROUP, 'Render', /Render key/)
    await next(user)
    await stepHeading(/Step 8 of 8/)
    const summary = screen.getByRole('region', { name: 'Deployment' })
    expect(summary).toHaveTextContent('New site storefront-web')
    expect(summary).toHaveTextContent('New project storefront')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })

    expect(requests('PUT', '/targets/frontend')[0]?.body).toEqual({ provider: 'netlify', connectionId: expect.any(String) as string, create: { name: 'storefront-web' } })
    // FR-190: one PUT for the group, whose job resolves the order inside: no target or database PUT, and no wait.
    expect(requests('PUT', '/groups/render')[0]?.body).toEqual({ connectionId: expect.any(String) as string, create: { name: 'storefront' } })
    expect(requests('PUT', '/targets/backend')).toHaveLength(0)
    expect(requests('PUT', '/databases/production')).toHaveLength(0)
    expect(requests('GET', '/settings/deployment')).toHaveLength(0)
  })

  it('S7_a_failed_setup_after_create_keeps_the_project_and_points_to_settings_deployment', async () => {
    const user = await toHostsStep('Render Postgres', seedHosts)
    server.use(
      http.put('*/api/projects/:id/settings/deployment/groups/render', () =>
        problem(409, 'PLATFORM_RESOURCE_EXISTS', 'Render already has a project with that name.', { platform: 'render' }),
      ),
    )
    await createWith(user, 'Frontend on Netlify', 'Netlify', /Netlify key/)
    await createWith(user, GROUP, 'Render', /Render key/)
    await next(user)
    await stepHeading(/Step 8 of 8/)
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    const failures = await screen.findByTestId('host-setup-failures')
    expect(within(failures).getByRole('listitem')).toHaveTextContent(/^Backend and database on Render: Render already has a project with that name\. Link it instead, or choose another name\.$/)
    expect(screen.getByRole('alert')).toHaveTextContent(/The project is created/)
    expect(requests('PUT', '/targets/frontend')).toHaveLength(1)
    expect(projectsDb.projects).toHaveLength(1)
    const projectId = projectsDb.projects[0]?.id ?? ''
    expect(screen.getByRole('link', { name: 'Open Settings → Deployment' })).toHaveAttribute('href', `/projects/${projectId}/settings/deployment`)
    expect(screen.getByRole('link', { name: 'Go to the project' })).toHaveAttribute('href', `/projects/${projectId}`)
    expect(screen.queryByRole('button', { name: 'Create project' })).not.toBeInTheDocument()
  })

  it('S7_FR152_an_external_database_takes_a_connection_string_that_is_never_shown_or_kept_in_the_draft', async () => {
    const user = await toHostsStep('External')
    const production = await card('Production database on External database')
    await user.click(within(production).getByRole('radio', { name: 'Enter a connection string' }))
    const field = within(production).getByLabelText('Production connection string')
    expect(field).toHaveAttribute('type', 'password')
    await user.type(field, 'postgresql://app:s3cret-pw@db.example.com:5432/shop')
    await next(user)
    await stepHeading(/Step 8 of 8/)
    expect(screen.getByRole('region', { name: 'Deployment' })).toHaveTextContent('Connection string entered (not shown)')
    expect(document.body.innerHTML).not.toContain('s3cret-pw')

    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await screen.findByRole('heading', { level: 1, name: 'Storefront' })
    await waitFor(() => {
      expect(requests('PUT', '/databases/production')[0]?.body).toEqual({
        provider: 'external',
        external: { connectionString: 'postgresql://app:s3cret-pw@db.example.com:5432/shop' },
      })
    })
  })

  it('S7_the_wizard_draft_never_keeps_a_connection_string', () => {
    saveWizardDraft({ ...wizardDefaults, gitToken: 'tok', hostDbProductionConnectionString: 'postgres://a:b@c/d', hostDbProductionMode: 'external' }, 6)
    const draft = readWizardDraft()
    expect(draft?.values.hostDbProductionMode).toBe('external')
    expect(draft?.values.hostDbProductionConnectionString).toBe('')
    expect(window.sessionStorage.getItem('aide.new-project-wizard.draft')).not.toContain('postgres://')
  })

  it('S7_FR188_windows_decide_the_hosts_and_a_railway_database_alone_needs_no_target', () => {
    const windows = [
      { platform: 'github_pages', parts: ['frontend'], capability: 'none', environment: null },
      { platform: 'railway', parts: ['database'], capability: 'none', environment: null },
    ]
    const stack = { stackKind: 'compose', hostWindows: JSON.stringify(windows) }
    expect(wizardHosts(stack).map((h) => h.title)).toEqual(['Frontend on GitHub Pages', 'Database on Railway Postgres'])
    expect(wizardHosts({ ...stack, stackKind: 'general' })).toEqual([])
    expect(hostChecks({ ...wizardDefaults, ...stack, hostDbProductionMode: 'create', hostDbProductionConnectionId: 'c1' })).toEqual([])
  })

  it('S7_FR188_a_lone_render_database_has_a_window_per_environment', () => {
    const windows = (['production', 'preview'] as const).map((environment) => ({ platform: 'render', parts: ['database'], capability: 'none', environment }))
    const hosts = wizardHosts({ stackKind: 'compose', hostWindows: JSON.stringify(windows) })
    expect(hosts.map((h) => h.title)).toEqual(['Production database on Render Postgres', 'Preview database on Render Postgres'])
  })

  it('S7_FR152_the_preview_database_must_be_separate_and_supabase_needs_a_region', () => {
    const windows = (['production', 'preview'] as const).map((environment) => ({ platform: 'supabase', parts: ['database'], capability: 'none', environment }))
    const stack = { stackKind: 'compose', hostWindows: JSON.stringify(windows) }
    const issues = hostChecks({
      ...wizardDefaults,
      ...stack,
      hostDbProductionMode: 'link',
      hostDbProductionConnectionId: 'c1',
      hostDbProductionResourceId: 'ref1',
      hostDbPreviewMode: 'link',
      hostDbPreviewConnectionId: 'c1',
      hostDbPreviewResourceId: 'ref1',
    })
    expect(issues).toEqual([['hostDbPreviewResourceId', 'The preview and production databases must be separate.']])

    expect(hostChecks({ ...wizardDefaults, ...stack, hostDbProductionMode: 'create', hostDbProductionConnectionId: 'c1' }).map(([f]) => f)).toEqual(['hostDbProductionRegion'])
  })
})
