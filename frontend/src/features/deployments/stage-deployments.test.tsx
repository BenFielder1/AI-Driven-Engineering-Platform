import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { makeRollout, makeRolloutStep, seedDeployment, seedDeploymentSettings, seedVercelConnection } from '../../mocks/handlers/deployments'
import { makeReleaseVersion, type StoredProject } from '../../mocks/handlers/projects'
import { seedRelease } from '../../mocks/handlers/release'
import { categoryScores, seedComplianceReport, seedReviewPhase } from '../../mocks/handlers/review'
import { seedProjectAs } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import type { ProjectRole } from '../../lib/permissions'

const LOG = "src/app/page.tsx:12:7 Type error: Type 'string' is not assignable to type 'number'."

function setUpVercel(p: StoredProject) {
  const ownerId = p.members.find((m) => m.role === 'owner')?.userId ?? ''
  seedDeploymentSettings(p, { connection: seedVercelConnection({ userId: ownerId, label: 'Team', token: 'vcp_aaaa0001' }) })
}

describe('Review page → preview deployment (FR-90)', () => {
  function seedFailedPreview(role: ProjectRole) {
    return seedProjectAs(role, (p) => {
      seedReviewPhase(p)
      setUpVercel(p)
      const preview = seedDeployment(p, {
        target: 'preview',
        status: 'error',
        gitSha: '9f8e7d6c5b4a39f8e7d6c5b4a39f8e7d6c5b4a3f',
        errorCode: 'VERCEL_ERROR',
        errorMessage: 'Command "npm run build" exited with 1.',
        buildLogTail: LOG,
      })
      seedComplianceReport(p, {
        categoryScores: categoryScores(0.8),
        decision: 'review_required',
        violations: [
          {
            category: 'architecture',
            severity: 'critical',
            source: 'deterministic',
            message: 'Vercel preview build failed',
            file: null,
            evidence: LOG,
          },
        ],
        externalChecks: {
          checks: [],
          checksWaitedSeconds: null,
          checksTimedOut: false,
          preview: { deploymentId: preview.id, status: 'error', url: preview.url, buildLogTail: LOG, waitedSeconds: 95, timedOut: false },
        },
      })
    })
  }

  async function openReview(project: StoredProject) {
    await renderRoute(`/projects/${project.id}/review`)
    await screen.findByRole('heading', { level: 2, name: 'Review' })
    return userEvent.setup()
  }

  it('FR90_review_preview_error_with_log: the panel shows the failed preview, its error and build-log tail, and Redeploy', async () => {
    const project = seedFailedPreview('contributor')
    const user = await openReview(project)
    const panel = await screen.findByRole('region', { name: 'Preview deployment' })
    expect(await within(panel).findAllByTestId('deployment-status')).not.toHaveLength(0)
    expect(within(panel).getAllByTestId('deployment-status')[0]).toHaveTextContent('Error')
    expect(within(panel).getByTestId('deployment-failure')).toHaveTextContent('Vercel reported the build or deployment as failed.')
    expect(within(panel).getByTestId('deployment-failure')).toHaveTextContent('exited with 1')
    const log = within(panel).getByTestId('build-log-tail')
    expect(log).toHaveTextContent('Type error')
    expect(log.tagName).toBe('PRE')
    expect(log.closest('details')).toHaveAttribute('open')

    await user.click(within(panel).getByRole('button', { name: /^Redeploy preview/ }))
    expect(await screen.findByText(/Redeploying 9f8e7d6/)).toBeInTheDocument()
    expect(requestsTo('POST', '/redeploy')).toHaveLength(1)
  })

  it('FR90_cycle_detail_preview_check: the cycle shows the preview it waited for, the critical finding and the log', async () => {
    const project = seedFailedPreview('owner')
    await openReview(project)
    const checks = await screen.findByRole('region', { name: 'External checks' })
    const preview = within(checks).getByTestId('report-preview')
    expect(preview).toHaveTextContent('Waited 1 min 35 s for the preview')
    expect(preview).toHaveTextContent('Vercel preview build failed')
    expect(within(preview).getByTestId('build-log-tail')).toHaveTextContent('Type error')
    expect(screen.getByRole('region', { name: /^Violations/ })).toHaveTextContent('Vercel preview build failed')
  })

  it('FR90_preview_timed_out: review that stopped waiting says so', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReviewPhase(p)
      seedComplianceReport(p, {
        externalChecks: {
          checks: [],
          checksWaitedSeconds: null,
          checksTimedOut: false,
          preview: { deploymentId: 'd1', status: 'building', url: null, buildLogTail: null, waitedSeconds: 1200, timedOut: true },
        },
      })
    })
    await openReview(project)
    const preview = within(await screen.findByRole('region', { name: 'External checks' })).getByTestId('report-preview')
    expect(preview).toHaveTextContent('Review stopped waiting for the preview')
    expect(preview).toHaveTextContent('after 20 min')
  })

  it('FR147_cycle_detail_rollout: a v4 cycle shows the whole preview rollout it waited for, read-only', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedReviewPhase(p)
      const rollout = makeRollout(p, {
        state: 'failed',
        steps: [
          makeRolloutStep('backend', { state: 'failed', provider: 'render', status: 'error', deploymentId: 'be', errorMessage: 'Pre-deploy exited with 1.', buildLogTail: LOG, canRedeploy: true }),
          makeRolloutStep('frontend', { state: 'skipped', provider: 'netlify', note: 'Not started because the backend preview failed.' }),
        ],
      })
      seedComplianceReport(p, {
        externalChecks: {
          checks: [],
          checksWaitedSeconds: null,
          checksTimedOut: false,
          preview: { deploymentId: 'be', status: 'error', url: null, buildLogTail: LOG, waitedSeconds: 60, timedOut: false, rollout },
        },
      })
    })
    await openReview(project)
    const preview = within(await screen.findByRole('region', { name: 'External checks' })).getByTestId('report-preview')
    expect(within(preview).getByTestId('rollout-state')).toHaveTextContent('Failed')
    expect(preview).toHaveTextContent('a failed preview migration a critical data model finding')
    expect(within(within(preview).getByTestId('rollout-step-backend')).getByTestId('deployment-failure')).toHaveTextContent('Pre-deploy exited with 1.')
    expect(within(preview).getByTestId('rollout-step-frontend')).toHaveTextContent('Skipped: Not started because the backend preview failed.')
    expect(within(preview).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
  })

  it('FR90_viewer_cannot_redeploy', async () => {
    const project = seedFailedPreview('viewer')
    await openReview(project)
    const panel = await screen.findByRole('region', { name: 'Preview deployment' })
    await within(panel).findByTestId('deployment-failure')
    expect(within(panel).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
  })
})

describe('Release page → production deployment (FR-91)', () => {
  function seedReleased(role: ProjectRole, status: 'ready' | 'error') {
    return seedProjectAs(role, (p) => {
      p.currentPhase = 'release'
      p.status = 'released'
      p.releaseVersions = [makeReleaseVersion({ version: '1.2.0', status: 'released' })]
      setUpVercel(p)
      const production = seedDeployment(p, {
        target: 'production',
        status,
        gitRef: 'main',
        gitSha: '3f2a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f',
        releaseVersion: '1.2.0',
        url: 'https://inventory.vercel.app',
        ...(status === 'error' ? { errorCode: 'VERCEL_ERROR', errorMessage: 'Build failed.', buildLogTail: 'Error: missing env' } : {}),
      })
      seedRelease(p, { tag: 'v1.2.0', productionDeployment: production })
    })
  }

  async function openRelease(project: StoredProject) {
    await renderRoute(`/projects/${project.id}/release`)
    // findAll: the stage header and the release panel both have a level-2 "Release" heading.
    await screen.findAllByRole('heading', { level: 2, name: 'Release' })
    return userEvent.setup()
  }

  it('FR91_release_live_on_vercel: a ready production deployment shows "Released 1.2.0 · live on Vercel" and its URL', async () => {
    const project = seedReleased('owner', 'ready')
    await openRelease(project)
    const panel = await screen.findByRole('region', { name: 'Production deployment' })
    expect(await within(panel).findByTestId('production-live')).toHaveTextContent('Released 1.2.0 · live on Vercel')
    expect(within(within(panel).getByTestId('production-live')).getByRole('link', { name: /inventory\.vercel\.app/ })).toHaveAttribute(
      'href',
      'https://inventory.vercel.app',
    )
    expect(within(panel).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
  })

  it('FR91_failed_production_redeploy: a failed production deployment shows the error and Redeploy for Owners', async () => {
    const project = seedReleased('owner', 'error')
    const user = await openRelease(project)
    const panel = await screen.findByRole('region', { name: 'Production deployment' })
    expect(await within(panel).findByText(/The release itself is done/)).toBeInTheDocument()
    expect(within(panel).getByTestId('deployment-failure')).toHaveTextContent('Build failed.')
    await user.click(within(panel).getByRole('button', { name: /^Redeploy production/ }))
    expect(await screen.findByText(/Redeploying 3f2a9c1/)).toBeInTheDocument()
  })

  it('FR91_contributor_cannot_redeploy_production', async () => {
    const project = seedReleased('contributor', 'error')
    await openRelease(project)
    const panel = await screen.findByRole('region', { name: 'Production deployment' })
    expect(await within(panel).findByText(/An Owner can redeploy/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
  })
})

describe('Overview → latest deployment (FR-90, FR-91)', () => {
  it('FR91_overview_latest_deployment: the newest deployment with its live state pill and a link to all deployments', async () => {
    const project = seedProjectAs('owner', (p) => {
      setUpVercel(p)
      seedDeployment(p, { target: 'production', releaseVersion: '0.1.0' })
      seedDeployment(p, { target: 'preview', status: 'building', url: null, gitSha: 'abcdef0123456789abcdef0123456789abcdef01' })
    })
    await renderRoute(`/projects/${project.id}`)
    const card = await screen.findByRole('region', { name: 'Latest deployment' })
    const latest = await within(card).findByTestId('latest-deployment')
    expect(latest).toHaveTextContent('Preview')
    expect(latest).toHaveTextContent('abcdef0')
    expect(within(latest).getByTestId('deployment-status')).toHaveTextContent('Building')
    expect(within(card).getByRole('link', { name: 'All deployments' })).toHaveAttribute('href', `/projects/${project.id}/deployments`)
  })

  it('FR91_overview_hidden_without_deployment', async () => {
    const project = seedProjectAs('owner')
    await renderRoute(`/projects/${project.id}`)
    await screen.findByRole('region', { name: 'Review' })
    expect(screen.queryByRole('region', { name: 'Latest deployment' })).not.toBeInTheDocument()
  })
})
