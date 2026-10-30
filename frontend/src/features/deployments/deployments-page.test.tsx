import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedDeployment, seedDeploymentSettings, seedVercelConnection } from '../../mocks/handlers/deployments'
import { projectsDb, type StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import type { ProjectRole } from '../../lib/permissions'
import { describeActivity } from '../activity/activity-summary'

/** A project with Vercel set up (bound by its Owner) and, from oldest to newest, two production and two preview rows. */
function seedWithDeployments(role: ProjectRole) {
  return seedProjectAs(role, (p) => {
    const ownerId = p.members.find((m) => m.role === 'owner')?.userId ?? ''
    seedDeploymentSettings(p, { connection: seedVercelConnection({ userId: ownerId, label: 'Team', token: 'vcp_aaaa0001' }) })
    seedDeployment(p, { target: 'production', gitSha: '1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', releaseVersion: '1.1.0', url: 'https://shop-old.vercel.app' })
    seedDeployment(p, { target: 'production', gitSha: '2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', releaseVersion: '1.2.0', url: 'https://shop.vercel.app' })
    seedDeployment(p, {
      target: 'preview',
      status: 'error',
      gitSha: '3333333ccccccccccccccccccccccccccccccccc',
      errorCode: 'VERCEL_ERROR',
      errorMessage: 'Build failed.',
      buildLogTail: 'Type error: x is not assignable',
    })
    seedDeployment(p, {
      target: 'preview',
      status: 'building',
      url: null,
      gitSha: '4444444ddddddddddddddddddddddddddddddddd',
      trigger: 'manual',
      triggeredBy: { id: ownerId, name: 'Grace Hopper' },
    })
  })
}

async function openDeployments(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/deployments`)
  await screen.findByRole('heading', { level: 2, name: 'Deployments' })
  return { ...utils, user: userEvent.setup() }
}

/** The DataTable's rows (stacked cards: jsdom matches no media query, so it renders the below-md layout). */
const rows = async () => {
  const list = await screen.findByRole('list', { name: 'Deployments, newest first' })
  return within(list).getAllByRole('listitem')
}

/** The four seeded rows, newest first: building preview, failed preview, live production, older production. */
const fourRows = async () => {
  const [building, failed, live, old] = await rows()
  if (!building || !failed || !live || !old) {
    throw new Error('Expected four deployments')
  }
  return [building, failed, live, old] as const
}

describe('/projects/:id/deployments (FR-91)', () => {
  it('FR91_list: target, version, short SHA, state (live pill while building), URL, time and who triggered it, newest first', async () => {
    const project = seedWithDeployments('owner')
    await openDeployments(project)
    const [building, failed, live, old] = await fourRows()
    expect(building).toHaveTextContent('Preview')
    expect(building).toHaveTextContent('4444444')
    expect(within(building).getByTestId('deployment-status')).toHaveTextContent('Building')
    expect(within(building).getByRole('status')).toHaveTextContent('(in progress)')
    expect(building).toHaveTextContent('No URL yet')
    expect(building).toHaveTextContent('Grace Hopper (Manual)')
    expect(within(failed).getByTestId('deployment-status')).toHaveTextContent('Error')
    expect(live).toHaveTextContent('1.2.0')
    expect(within(live).getByTestId('deployment-current')).toHaveTextContent('Current')
    expect(within(live).getByRole('link', { name: /shop\.vercel\.app/ })).toHaveAttribute('href', 'https://shop.vercel.app')
    expect(old).toHaveTextContent('AIDE')
  })

  it('FR91_promote_only_ready_production: Owners can promote an earlier ready production deployment, not the live one or previews', async () => {
    const project = seedWithDeployments('owner')
    const { user } = await openDeployments(project)
    const [building, failed, live, old] = await fourRows()
    for (const row of [building, failed, live]) {
      expect(within(row).queryByRole('button', { name: /^Promote/ })).not.toBeInTheDocument()
    }
    // Redeploy: not while building; previews and production otherwise.
    expect(within(building).queryByRole('button', { name: /^Redeploy/ })).not.toBeInTheDocument()
    expect(within(failed).getByRole('button', { name: /^Redeploy/ })).toBeInTheDocument()

    await user.click(within(old).getByRole('button', { name: 'Promote 1.1.0 (1111111)' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Promote 1.1.0 to production?' })
    expect(dialog).toHaveTextContent("Git isn't changed")
    expect(dialog).toHaveTextContent('Live now1.2.0')
    await user.click(within(dialog).getByRole('button', { name: 'Promote to production' }))

    expect(await screen.findByText('Promoting 1.1.0 (1111111) to production.')).toBeInTheDocument()
    // FR-150: the UI uses /rollback (`/promote` is the API's v2 alias).
    expect(requestsTo('POST', '/rollback')).toHaveLength(1)
    expect(requestsTo('POST', '/promote')).toHaveLength(0)
    await waitFor(async () => {
      const [first] = await rows()
      expect(first).toHaveTextContent('Ada Lovelace (Promote)')
    })
  })

  it('FR91_promote_invalid: INVALID_DEPLOYMENT keeps the dialog open with the copy', async () => {
    const project = seedWithDeployments('owner')
    const { user } = await openDeployments(project)
    const [, , , old] = await fourRows()
    // The deployment stopped being ready meanwhile.
    const stored = projectsDb.projects[0]?.deployments?.[0]
    if (stored) {
      stored.status = 'canceled'
    }
    await user.click(within(old).getByRole('button', { name: 'Promote 1.1.0 (1111111)' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Promote 1.1.0 to production?' })
    await user.click(within(dialog).getByRole('button', { name: 'Promote to production' }))
    expect(await within(dialog).findByText(/only a ready production deployment that isn't live can/)).toBeInTheDocument()
  })

  it('FR91_contributor: no Promote and no production Redeploy, but previews can be redeployed and deployed', async () => {
    const project = seedWithDeployments('contributor')
    const { user } = await openDeployments(project)
    const [, failed, live, old] = await fourRows()
    expect(screen.queryByRole('button', { name: /^Promote/ })).not.toBeInTheDocument()
    expect(within(live).queryByRole('button', { name: /^Redeploy/ })).not.toBeInTheDocument()
    expect(within(old).queryByRole('button', { name: /^Redeploy/ })).not.toBeInTheDocument()
    await user.click(within(failed).getByRole('button', { name: /^Redeploy preview/ }))
    expect(await screen.findByText(/Redeploying 3333333/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Deploy preview' })).toBeInTheDocument()
  })

  it('FR91_viewer: read only', async () => {
    const project = seedWithDeployments('viewer')
    await openDeployments(project)
    await rows()
    expect(screen.queryByRole('button', { name: /Promote|Redeploy|Deploy preview/ })).not.toBeInTheDocument()
  })

  it('FR91_filter_by_target: the target filter lists only that target', async () => {
    const project = seedWithDeployments('owner')
    const { user } = await openDeployments(project)
    await rows()
    await user.selectOptions(screen.getByLabelText('Environment'), 'production')
    await waitFor(async () => {
      expect(await rows()).toHaveLength(2)
    })
    for (const row of await rows()) {
      expect(row).toHaveTextContent('Production')
    }
  })

  it('FR91_not_configured: without deployment settings, an empty state links Owners to Settings → Deployment', async () => {
    const project = seedProjectAs('owner')
    await openDeployments(project)
    expect(screen.getByText("Deployment isn't set up")).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Set up deployment' })).toHaveAttribute('href', `/projects/${project.id}/settings/deployment`)
  })

  it('FR91_activity: deployment events read with target, version, SHA and state', () => {
    expect(
      describeActivity({
        type: 'DEPLOYMENT_UPDATED',
        payload: { deploymentId: 'd', target: 'production', status: 'ready', url: 'https://shop.vercel.app', releaseVersion: '1.2.0', gitSha: 'abcdef1234' },
        summary: 'fallback',
      }),
    ).toBe('Production deployment of 1.2.0 (abcdef1) ready at https://shop.vercel.app')
    expect(describeActivity({ type: 'DEPLOYMENT_UPDATED', payload: { target: 'preview', status: 'error' }, summary: 'x' })).toBe(
      'Preview deployment failed',
    )
    expect(
      describeActivity({ type: 'DEPLOYMENT_FAILED', payload: { target: 'production', status: 'error', releaseVersion: '1.2.0', gitSha: 'abcdef1234' }, summary: 'x' }),
    ).toBe('Production deployment of 1.2.0 (abcdef1) failed')
    expect(
      describeActivity({
        type: 'DEPLOYMENT_PREVIEW_READY',
        payload: { target: 'preview', status: 'ready', url: 'https://p.vercel.app', releaseVersion: '1.3.0', gitSha: '1234567890' },
        summary: 'x',
      }),
    ).toBe('Preview deployment of 1.3.0 (1234567) ready at https://p.vercel.app')
  })
})

describe('FR-54: the Deployments nav item', () => {
  it('FR91_nav_visibility: Deployments shows in the project nav only when deployment is set up', async () => {
    const plain = seedProjectAs('owner')
    const first = await renderRoute(`/projects/${plain.id}`)
    const nav = await screen.findByRole('navigation', { name: 'Project sections' })
    await within(nav).findByRole('link', { name: /Activity/ })
    expect(within(nav).queryByRole('link', { name: /Deployments/ })).not.toBeInTheDocument()
    first.unmount()

    seedDeploymentSettings(plain)
    await renderRoute(`/projects/${plain.id}`)
    const nav2 = await screen.findByRole('navigation', { name: 'Project sections' })
    expect(await within(nav2).findByRole('link', { name: /Deployments/ })).toHaveAttribute('href', `/projects/${plain.id}/deployments`)
  })
})
