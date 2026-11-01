import { screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { ApiError, errorMessage } from '../../lib/errors'
import { seedUser } from '../../mocks/handlers/auth'
import { deploymentDb, makeDatabase, makeTarget, seedDeploymentGroup, seedNetlifyRenderTargets } from '../../mocks/handlers/deployments'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { openSettings, requestsTo, signInAsOwner } from '../../test/project-settings'
import { deploymentSettingsPolling } from './deployments-api'
import { groupRequestBody, type GroupSetupValues } from './host-setup-body'

const TITLE = 'Frontend, backend and database on Railway'

async function openAsOwner(seed: (me: string, p: StoredProject) => void) {
  const me = signInAsOwner()
  const project = seedProject({})
  seed(me.id, project)
  const utils = await openSettings('deployment', project)
  await screen.findByRole('heading', { level: 2, name: 'Deployment' })
  return utils
}

const groupCard = () => screen.findByRole('region', { name: TITLE })
const allParts = ['frontend', 'backend', 'database'] as const

function seedReadyGroup(me: string, p: StoredProject) {
  const connection = seedPlatformConnection({ userId: me, provider: 'railway', label: 'Railway key', token: 'rwy_aaaa1111', accountName: 'acme' })
  return seedDeploymentGroup(p, { provider: 'railway', parts: [...allParts], connection })
}

beforeEach(() => {
  deploymentSettingsPolling.setupMs = 20
})

afterEach(() => {
  deploymentSettingsPolling.setupMs = 2_000
})

describe('Settings → Deployment group cards (FR-191)', () => {
  it('FR191_group_card_holds_the_parts_cards_connected_by_the_project_link_and_each_status', async () => {
    await openAsOwner(seedReadyGroup)
    const card = await groupCard()
    expect(within(card).getByTestId('group-binding')).toHaveTextContent('Railway · Railway key (••••1111) · account acme · connected by Ada Lovelace')
    expect(within(card).getByRole('link', { name: /shop/ })).toHaveAttribute('href', 'https://railway.com/project/prj_shop')
    expect(within(card).getByRole('link', { name: /shop/ })).toHaveAttribute('rel', 'noopener noreferrer')
    expect(within(card).getByTestId('group-status')).toHaveTextContent('Ready')

    const parts = within(card).getByRole('list', { name: 'Parts of the Railway group' })
    expect(within(parts).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual([
      'Frontend',
      'Backend',
      'Database (production)',
      'Database (preview)',
    ])
    expect(within(parts).getAllByTestId('group-part-status').map((e) => e.textContent)).toEqual(['Ready', 'Ready', 'Available', 'Available'])

    expect(within(card).getByRole('region', { name: 'Frontend on Railway' })).toBeInTheDocument()
    expect(within(card).getByRole('region', { name: 'Backend on Railway' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 3, name: 'Targets' })).not.toBeInTheDocument()
    expect(within(card).queryByRole('button', { name: /^Retry/ })).not.toBeInTheDocument()
  })

  it('FR191_v4_rows_with_no_group_look_unchanged', async () => {
    await openAsOwner((_me, p) => {
      seedNetlifyRenderTargets(p)
    })
    expect(await screen.findByRole('region', { name: 'Frontend on Netlify' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 3, name: 'Targets' })).toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Databases' })).toBeInTheDocument()
    expect(screen.queryByTestId('group-status')).not.toBeInTheDocument()
  })

  it('FR191_parts_a_v4_project_set_up_alone_stay_as_they_are_when_the_stack_would_group_them', async () => {
    await openAsOwner((me, p) => {
      const c = seedPlatformConnection({ userId: me, provider: 'railway', label: 'Railway key', token: 'rwy_aaaa1111' })
      seedDeploymentGroup(p, { provider: 'railway', parts: ['frontend', 'backend'] })
      const d = p.deployment
      const target = d?.targets.find((t) => t.component === 'frontend')
      if (target) {
        target.resourceId = 'alone:svc'
        target.name = 'alone'
        target.binding = { connectionId: c.id, label: c.label, last4: '1111', accountName: null, status: 'valid', boundBy: { id: me, name: 'Ada Lovelace' } }
      }
    })
    expect(await screen.findByRole('region', { name: 'Frontend on Railway' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 3, name: 'Targets' })).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'Set up the Railway group' })).not.toBeInTheDocument()
  })

  it('FR190_retry_shows_only_when_a_part_failed_and_creates_only_that_part', async () => {
    deploymentDb.groupPartFailure = 'frontend'
    const { user } = await openAsOwner(seedReadyGroup)
    const card = await groupCard()
    const parts = within(card).getByRole('list', { name: 'Parts of the Railway group' })
    expect(within(parts).getByRole('listitem', { name: 'Frontend' })).toHaveTextContent('Error')
    expect(within(parts).getByRole('listitem', { name: 'Frontend' })).toHaveTextContent('Railway returned an error. Try again later.')
    expect(within(parts).getByRole('listitem', { name: 'Backend' })).toHaveTextContent('Ready')
    expect(within(card).getByTestId('group-status')).toHaveTextContent('Error')

    await user.click(within(card).getByRole('button', { name: 'Retry the Railway group' }))
    await waitFor(() => {
      expect(within(parts).getByRole('listitem', { name: 'Frontend' })).toHaveTextContent('Ready')
    })
    expect(requestsTo('POST', '/groups/railway/retry')).toHaveLength(1)
    expect(within(card).queryByRole('button', { name: 'Retry the Railway group' })).not.toBeInTheDocument()
  })

  it('FR191_rebind_replaces_the_connection_of_the_group', async () => {
    const { user } = await openAsOwner((me, p) => {
      seedReadyGroup(me, p)
      seedPlatformConnection({ userId: me, provider: 'railway', label: 'New key', token: 'rwy_bbbb2222' })
    })
    const card = await groupCard()
    await user.click(within(card).getByRole('button', { name: 'Change the connection of the Railway group' }))
    const form = await within(card).findByRole('form', { name: 'Rebind the Railway group' })
    await user.click(within(form).getByRole('button', { name: 'Save connection' }))
    expect(within(form).getByText('Pick one of your Railway connections.')).toBeVisible()
    await user.selectOptions(within(form).getByLabelText('Railway connection'), within(form).getByRole('option', { name: /New key/ }))
    await user.click(within(form).getByRole('button', { name: 'Save connection' }))
    await waitFor(() => {
      expect(within(card).getByTestId('group-binding')).toHaveTextContent('New key')
    })
    expect(requestsTo('PUT', '/groups/railway')[0]?.body).toEqual({ connectionId: expect.any(String) as string })
  })

  it('FR191_rebind_to_a_token_that_cant_see_the_project_says_so_and_keeps_the_old_binding', async () => {
    deploymentDb.rebindUnreachable = true
    const { user } = await openAsOwner((me, p) => {
      seedReadyGroup(me, p)
      seedPlatformConnection({ userId: me, provider: 'railway', label: 'New key', token: 'rwy_bbbb2222' })
    })
    const card = await groupCard()
    await user.click(within(card).getByRole('button', { name: 'Change the connection of the Railway group' }))
    const form = await within(card).findByRole('form', { name: 'Rebind the Railway group' })
    await user.selectOptions(within(form).getByLabelText('Railway connection'), within(form).getByRole('option', { name: /New key/ }))
    await user.click(within(form).getByRole('button', { name: 'Save connection' }))
    expect(await within(form).findByText(/keeps its current connection/)).toBeVisible()
    expect(within(card).getByTestId('group-binding')).toHaveTextContent('Railway key')
  })

  it('FR191_remove_says_platform_resources_stay', async () => {
    const { user } = await openAsOwner(seedReadyGroup)
    const card = await groupCard()
    await user.click(within(card).getByRole('button', { name: 'Remove the Railway group' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(dialog).toHaveTextContent('Nothing is deleted on Railway: its project, services and databases stay.')
    await user.click(within(dialog).getByRole('button', { name: 'Remove group' }))
    await waitFor(() => {
      expect(requestsTo('DELETE', '/groups/railway')).toHaveLength(1)
    })
    expect(await screen.findByRole('form', { name: 'Set up the Railway group' })).toBeInTheDocument()
  })

  it('FR190_a_not_configured_group_offers_create_and_a_taken_name_says_link_it_instead', async () => {
    deploymentDb.existingProjectNames = ['taken']
    const { user } = await openAsOwner((me, p) => {
      seedPlatformConnection({ userId: me, provider: 'railway', label: 'Railway key', token: 'rwy_aaaa1111' })
      seedDeploymentGroup(p, { provider: 'railway', parts: [...allParts] })
    })
    const card = await groupCard()
    const form = within(card).getByRole('form', { name: 'Set up the Railway group' })
    await user.selectOptions(await within(form).findByLabelText('Railway connection'), within(form).getByRole('option', { name: /Railway key/ }))
    const name = within(form).getByLabelText('Name')
    await user.clear(name)
    await user.type(name, 'Bad Name')
    await user.click(within(form).getByRole('button', { name: 'Set up the group' }))
    expect((await within(form).findAllByText(/Use lowercase letters/)).length).toBeGreaterThan(0)

    await user.clear(name)
    await user.type(name, 'taken')
    await user.click(within(form).getByRole('button', { name: 'Set up the group' }))
    expect(await within(form).findByText('Railway already has a project with that name. Link it instead, or choose another name.')).toBeVisible()

    await user.clear(name)
    await user.type(name, 'fresh')
    await user.click(within(form).getByRole('button', { name: 'Set up the group' }))
    await waitFor(() => {
      expect(within(card).getByTestId('group-project')).toHaveTextContent('fresh')
    })
    expect(requestsTo('PUT', '/groups/railway').at(-1)?.body).toEqual({ connectionId: expect.any(String) as string, create: { name: 'fresh' } })
  })

  it('FR191_a_part_that_left_the_group_rejoins_only_when_the_owner_says_so', async () => {
    const { user } = await openAsOwner((me, p) => {
      const group = seedReadyGroup(me, p)
      seedPlatformConnection({ userId: me, provider: 'railway', label: 'Second', token: 'rwy_cccc3333' })
      const d = p.deployment
      if (d && group.id) {
        d.targets = d.targets.map((t) => (t.component === 'frontend' ? makeTarget('frontend', 'railway') : t))
      }
    })
    const card = await groupCard()
    const frontend = within(card).getByRole('region', { name: 'Frontend on Railway' })
    const form = within(frontend).getByRole('form', { name: 'Set up the frontend on Railway' })
    await user.selectOptions(await within(form).findByLabelText('Railway connection'), within(form).getByRole('option', { name: /Second/ }))
    await user.click(within(form).getByRole('checkbox', { name: 'Set this up in the Railway group' }))
    await user.click(within(form).getByRole('button', { name: 'Set up Railway' }))
    await waitFor(() => {
      expect(requestsTo('PUT', '/targets/frontend')).toHaveLength(1)
    })
    expect(requestsTo('PUT', '/targets/frontend')[0]?.body).toMatchObject({ groupId: expect.any(String) as string })
  })

  it('FR191_a_member_sees_the_group_but_no_controls', async () => {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper' })
    const me = signInAsOwner()
    const connection = seedPlatformConnection({ userId: owner.id, provider: 'railway', label: 'Railway key', token: 'rwy_aaaa1111' })
    const project = seedProject({ members: [{ userId: owner.id, role: 'owner' }, { userId: me.id, role: 'viewer' }] })
    seedDeploymentGroup(project, { provider: 'railway', parts: [...allParts], connection })
    await openSettings('deployment', project)
    const card = await groupCard()
    expect(within(card).getByTestId('group-binding')).toHaveTextContent('connected by Grace Hopper')
    expect(within(card).queryByRole('button', { name: /group/ })).not.toBeInTheDocument()
  })

  it('FR192_placement_unavailable_and_vercels_note_are_visible_text', async () => {
    await openAsOwner((me, p) => {
      const connection = seedPlatformConnection({ userId: me, provider: 'render', label: 'Render key', token: 'rnd_aaaa1111' })
      const group = seedDeploymentGroup(p, { provider: 'render', parts: ['backend', 'database'], connection })
      group.statusReason = 'project_placement_unavailable'
      seedDeploymentGroup(p, { provider: 'vercel', parts: ['frontend'], capability: 'shared_setup' })
      p.deployment?.databases.push(makeDatabase('preview', 'render'))
    })
    expect(await screen.findByTestId('group-status-reason')).toHaveTextContent("Render project grouping isn't available")
    expect(screen.getByText('Vercel keeps each part in its own project; these use the same team and name.')).toBeVisible()
  })
})

describe('Deployment group copy and requests (§6, FR-190)', () => {
  const values: GroupSetupValues = { connectionId: 'c1', mode: 'link', name: '', region: '', plan: '', resourceId: 'p2', linkFrontend: 'p1:web', linkBackend: 'p2:api', linkDatabase: '' }

  it('FR190_a_railway_service_of_another_project_is_left_out_of_the_link', () => {
    expect(groupRequestBody({ provider: 'railway', capability: 'project', parts: [...allParts] }, values)).toEqual({
      connectionId: 'c1',
      link: { resourceId: 'p2', parts: { backend: 'p2:api' } },
    })
  })

  it('FR192_a_vercel_link_names_each_part_and_no_project', () => {
    expect(
      groupRequestBody({ provider: 'vercel', capability: 'shared_setup', parts: ['frontend', 'backend'] }, { ...values, resourceId: '', linkFrontend: 'v1', linkBackend: 'v2' }),
    ).toEqual({ connectionId: 'c1', link: { parts: { frontend: 'v1', backend: 'v2' } } })
  })

  it('S6_DEPLOYMENT_GROUP_INVALID_and_PLATFORM_RESOURCE_EXISTS_use_the_spec_copy', () => {
    const reason = 'a group needs at least two parts on the platform'
    expect(errorMessage(new ApiError({ status: 400, errorCode: 'DEPLOYMENT_GROUP_INVALID', platform: 'railway', reason }))).toBe(
      `These parts can't share a Railway project: ${reason}.`,
    )
    // The API's title carries the platform when the problem has no `platform` extension.
    expect(errorMessage(new ApiError({ status: 400, errorCode: 'DEPLOYMENT_GROUP_INVALID', reason, title: `These parts can't share a Render project: ${reason}.` }))).toBe(
      `These parts can't share a Render project: ${reason}.`,
    )
    expect(errorMessage(new ApiError({ status: 409, errorCode: 'PLATFORM_RESOURCE_EXISTS', platform: 'render' }))).toBe(
      'Render already has a project with that name. Link it instead, or choose another name.',
    )
  })
})
