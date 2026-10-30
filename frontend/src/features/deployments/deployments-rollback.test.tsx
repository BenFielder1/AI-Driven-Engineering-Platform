import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, onTestFinished } from 'vitest'

import { deploymentOf, seedDeployment, seedGitHubPagesTargets, seedNetlifyRenderTargets } from '../../mocks/handlers/deployments'
import type { StoredProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { seedProjectAs } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import { ApiError, copyForCode, errorMessage, errorMessages, platformCopy } from '../../lib/errors'
import { deploymentFailure, rollbackBlockedCopy, rollbackWording } from './deployment-format'

const sha = (c: string) => c.repeat(40)

/**
 * A React SPA on Netlify and an API on Render (both set up), from oldest to newest: two Netlify production deploys,
 * two Render production deploys (the older one older than the latest migration) and a Netlify preview.
 */
function seedNetlifyRender() {
  return seedProjectAs('owner', (p) => {
    seedNetlifyRenderTargets(p, { configured: true })
    const fe = { component: 'frontend', provider: 'netlify', platform: 'netlify', target: 'production' } as const
    const be = { component: 'backend', provider: 'render', platform: 'render', target: 'production' } as const
    seedDeployment(p, { ...fe, gitSha: sha('1'), releaseVersion: '1.1.0' })
    seedDeployment(p, { ...fe, gitSha: sha('2'), releaseVersion: '1.2.0' })
    seedDeployment(p, { ...be, gitSha: sha('3'), releaseVersion: '1.1.0', olderThanLatestMigration: true })
    seedDeployment(p, { ...be, gitSha: sha('4'), releaseVersion: '1.2.0', olderThanLatestMigration: false })
    seedDeployment(p, { component: 'frontend', provider: 'netlify', platform: 'netlify', gitSha: sha('5'), releaseVersion: '1.3.0' })
  })
}

async function openDeployments(project: StoredProject) {
  await renderRoute(`/projects/${project.id}/deployments`)
  await screen.findByRole('heading', { level: 2, name: 'Deployments' })
  return userEvent.setup()
}

/** The DataTable's stacked rows (jsdom matches no media query), newest first. */
const rows = async () => {
  const list = await screen.findByRole('list', { name: 'Deployments, newest first' })
  return within(list).getAllByRole('listitem')
}

/** Narrows a destructured row (the list length is asserted by the caller). */
const at = (row: HTMLElement | undefined): HTMLElement => {
  if (!row) {
    throw new Error('Missing row')
  }
  return row
}

const rowWith = async (text: string) => {
  const row = (await rows()).find((r) => r.textContent.includes(text))
  if (!row) {
    throw new Error(`No row with ${text}`)
  }
  return row
}

describe('/projects/:id/deployments (FR-150, FR-151)', () => {
  it('FR151_filters_change_query: the component and environment filters are sent to the API and narrow the list', async () => {
    const queries: URLSearchParams[] = []
    onTestFinished(() => {
      server.events.removeAllListeners()
    })
    server.events.on('request:start', ({ request }) => {
      const url = new URL(request.url)
      if (url.pathname.endsWith('/deployments')) {
        queries.push(url.searchParams)
      }
    })
    const user = await openDeployments(seedNetlifyRender())
    expect(await rows()).toHaveLength(5)
    expect(screen.getByRole('group', { name: 'Filter deployments' })).toBeInTheDocument()

    await user.selectOptions(screen.getByLabelText('Component'), 'backend')
    await waitFor(async () => {
      expect(await rows()).toHaveLength(2)
    })
    for (const row of await rows()) {
      expect(row).toHaveTextContent('Backend')
      expect(row).toHaveTextContent('Render')
    }
    await user.selectOptions(screen.getByLabelText('Environment'), 'preview')
    expect(await screen.findByText('No deployments match these filters')).toBeInTheDocument()

    const last = queries.at(-1)
    expect(last?.get('component')).toBe('backend')
    expect(last?.get('environment')).toBe('preview')
    expect(queries.some((q) => q.get('component') === 'backend' && !q.has('environment'))).toBe(true)
  })

  it('FR151_row_and_current_badge: each row shows component, environment, provider and a Current badge on the live deployment per component', async () => {
    await openDeployments(seedNetlifyRender())
    const [preview, beLive, beOld, feLive, feOld] = await rows()
    expect(preview).toHaveTextContent('Frontend')
    expect(preview).toHaveTextContent('Preview')
    expect(preview).toHaveTextContent('Netlify')
    expect(within(at(beLive)).getByTestId('deployment-current')).toHaveTextContent('Current')
    expect(beLive).toHaveTextContent('Backend')
    expect(beLive).toHaveTextContent('4444444')
    expect(within(at(feLive)).getByTestId('deployment-current')).toHaveTextContent('Current')
    expect(within(at(beOld)).queryByTestId('deployment-current')).not.toBeInTheDocument()
    expect(within(at(feOld)).queryByTestId('deployment-current')).not.toBeInTheDocument()
    expect(within(at(preview)).queryByTestId('deployment-current')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('deployment-current')).toHaveLength(2)
  })

  it('FR150_netlify_restore: rolling back a Netlify row says Restore and posts to /rollback; the new row is current', async () => {
    const user = await openDeployments(seedNetlifyRender())
    const old = await rowWith('1111111')
    expect(within(old).queryByRole('button', { name: /^Promote/ })).not.toBeInTheDocument()
    await user.click(within(old).getByRole('button', { name: 'Restore 1.1.0 (1111111)' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Restore 1.1.0 to production?' })
    expect(dialog).toHaveTextContent('Netlify restores this deploy')
    expect(dialog).toHaveTextContent('Live now1.2.0')
    expect(within(dialog).queryByTestId('rollback-migration-warning')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Restore to production' }))

    expect(await screen.findByText('Restoring 1.1.0 (1111111) to production.')).toBeInTheDocument()
    expect(requestsTo('POST', '/rollback')).toHaveLength(1)
    expect(requestsTo('POST', '/promote')).toHaveLength(0)
    await waitFor(async () => {
      const [first] = await rows()
      expect(first).toHaveTextContent('Ada Lovelace (Rollback)')
      expect(within(at(first)).getByTestId('deployment-current')).toBeInTheDocument()
    })
  })

  it('FR150_migration_warning: rolling a backend back to a deployment older than the latest migration warns about the database', async () => {
    const user = await openDeployments(seedNetlifyRender())
    const old = await rowWith('3333333')
    await user.click(within(old).getByRole('button', { name: 'Roll back 1.1.0 (3333333)' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Roll back production to 1.1.0?' })
    expect(within(dialog).getByTestId('rollback-migration-warning')).toHaveTextContent(
      'Rolling back the backend never rolls back its database. This deployment is older than the latest applied migration',
    )
    expect(dialog).toHaveTextContent('Backend on Render')
    expect(within(dialog).getByRole('button', { name: 'Roll back' })).toBeInTheDocument()
  })

  it('FR150_github_pages_unsupported: a GitHub Pages production row says rollback is unsupported, with no button', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedGitHubPagesTargets(p)
      const pages = deploymentOf(p).targets.find((t) => t.component === 'frontend')
      if (pages) {
        pages.resourceId = 'pages'
        pages.status = 'ready'
      }
      const gh = { component: 'frontend', provider: 'github_pages', platform: 'github_pages', target: 'production' } as const
      seedDeployment(p, { ...gh, gitSha: sha('6'), releaseVersion: '1.0.0' })
      seedDeployment(p, { ...gh, gitSha: sha('7'), releaseVersion: '1.1.0' })
    })
    await openDeployments(project)
    const [live, old] = await rows()
    for (const row of [at(live), at(old)]) {
      expect(row).toHaveTextContent('GitHub Pages')
      expect(within(row).getByTestId('rollback-blocked')).toHaveTextContent("GitHub Pages doesn't support rollback")
      expect(within(row).queryByRole('button', { name: /^(Roll back|Promote|Restore|Deploy this version)/ })).not.toBeInTheDocument()
    }
  })

  it('FR150_wording: each platform words the action its own way, and every blocked reason has short copy', () => {
    expect(rollbackWording('vercel').action).toBe('Promote')
    expect(rollbackWording('netlify').action).toBe('Restore')
    expect(rollbackWording('cloudflare').action).toBe('Deploy this version')
    expect(rollbackWording('cloudflare', true).action).toBe('Roll back')
    expect(rollbackWording('render').action).toBe('Roll back')
    expect(rollbackWording('railway').action).toBe('Roll back')
    expect(rollbackBlockedCopy({ rollbackBlockedReason: 'unsupported', platform: 'github_pages' })).toBe("GitHub Pages doesn't support rollback")
    expect(rollbackBlockedCopy({ rollbackBlockedReason: 'not_ready', platform: 'render' })).toBe('Only a ready deployment can be rolled back to')
    expect(rollbackBlockedCopy({ rollbackBlockedReason: 'unknown_at_platform', platform: 'railway' })).toMatch(/^Railway has no record/)
    expect(rollbackBlockedCopy({ rollbackBlockedReason: 'not_production', platform: 'vercel' })).toMatch(/production/)
    expect(rollbackBlockedCopy({ rollbackBlockedReason: 'current', platform: 'vercel' })).toBeNull()
  })
})

describe('FR-151: production URLs on the project overview', () => {
  it('FR151_overview_production_urls: each component shows its provider and production URL', async () => {
    const project = seedNetlifyRender()
    await renderRoute(`/projects/${project.id}`)
    const card = await screen.findByRole('region', { name: 'Production URLs' })
    const urls = within(card).getByTestId('production-urls')
    expect(urls).toHaveTextContent('Frontend')
    expect(urls).toHaveTextContent('Netlify')
    expect(urls).toHaveTextContent('Backend')
    expect(urls).toHaveTextContent('Render')
    expect(within(card).getByRole('link', { name: /inventory-22222222\.example\.app/ })).toHaveAttribute(
      'href',
      'https://inventory-22222222.example.app',
    )
    expect(within(card).getByRole('link', { name: /inventory-44444444\.example\.app/ })).toBeInTheDocument()
  })
})

describe('PLATFORM_RATE_LIMITED copy (FR-150, §6)', () => {
  it('FR150_rate_limited_copy: the code names the platform and says AIDE retries, also as a message prefix', () => {
    expect(errorMessages.PLATFORM_RATE_LIMITED).toBe('{platform} is rate limiting deployments. AIDE will retry.')
    expect(platformCopy('PLATFORM_RATE_LIMITED', 'render')).toBe('Render is rate limiting deployments. AIDE will retry.')
    expect(copyForCode('PLATFORM_RATE_LIMITED')).toContain('AIDE will retry')
    const err = new ApiError({ status: 429, errorCode: 'PLATFORM_RATE_LIMITED', platform: 'netlify' })
    expect(errorMessage(err)).toBe('Netlify is rate limiting deployments. AIDE will retry.')
    expect(
      deploymentFailure({ errorCode: 'PLATFORM_ERROR', errorMessage: 'PLATFORM_RATE_LIMITED: 429 Too Many Requests', provider: 'fake', platform: 'cloudflare' }),
    ).toBe('Cloudflare is rate limiting deployments. AIDE will retry. 429 Too Many Requests')
    expect(deploymentFailure({ errorCode: null, errorMessage: 'NOT_A_CODE: boom', provider: 'vercel' })).toBe('NOT_A_CODE: boom')
  })
})
