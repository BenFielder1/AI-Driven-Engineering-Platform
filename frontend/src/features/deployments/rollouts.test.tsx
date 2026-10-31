import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { ApiError, errorMessage, errorMessages } from '../../lib/errors'
import {
  deploymentDb,
  deploymentOf,
  GITHUB_PAGES_PREVIEW_NOTE,
  makeDatabase,
  makeRollout,
  makeRolloutStep,
  rolloutStepOf,
  seedDeployment,
  seedNetlifyRenderTargets,
  seedPreviewEnvironment,
} from '../../mocks/handlers/deployments'
import { makeReleaseVersion, seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { seedRelease } from '../../mocks/handlers/release'
import { seedReviewPhase } from '../../mocks/handlers/review'
import { seedProjectAs } from '../../test/artifacts'
import { openSettings, requestsTo, signInAsOwner } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import type { ProjectRole } from '../../lib/permissions'
import { previewRolloutPolling } from './deployments-api'
import { joinNames, previewBuildFailureTitles, previewResetCopy } from './rollout-format'

const LOG = 'Running pre-deploy: /app/efbundle\nerror: relation "orders" already exists\nPre-deploy exited with 1.'
const SHA = '7c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f2a9c'
const RUN_ID = '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b'

beforeEach(() => {
  previewRolloutPolling.runningMs = 20
})

afterEach(() => {
  previewRolloutPolling.runningMs = 10_000
})

/** A React (Netlify) + ASP.NET (Render) + Render Postgres project in Review; `seed` adds its preview. */
function seedReview(role: ProjectRole, seed: (p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    seedReviewPhase(p)
    seedNetlifyRenderTargets(p, { configured: true })
    seed(p)
  })
}

async function openPreview(project: StoredProject) {
  await renderRoute(`/projects/${project.id}/review`)
  await screen.findByRole('heading', { level: 2, name: 'Review' })
  const panel = await screen.findByRole('region', { name: 'Preview deployment' })
  return { panel, user: userEvent.setup() }
}

const stepRows = async (panel: HTMLElement, name = 'Preview rollout steps') =>
  within(await within(panel).findByRole('list', { name })).getAllByRole('listitem').filter((li) => li.dataset.testid?.startsWith('rollout-step-'))

const row = (panel: HTMLElement, step: 'migrations' | 'backend' | 'frontend') => within(panel).getByTestId(`rollout-step-${step}`)

describe('Review page → preview rollout panel (FR-147)', () => {
  it('FR147_rollout_rows_in_order: migrations, backend, then frontend, each with host, state and URL; the run is linked', async () => {
    const project = seedReview('contributor', (p) => {
      seedPreviewEnvironment(p, {
        current: makeRollout(p, {
          state: 'succeeded',
          gitSha: SHA,
          steps: [
            makeRolloutStep('frontend', { state: 'succeeded', provider: 'netlify', status: 'ready', url: 'https://aide-v0-1-0--inventory-web.netlify.app', canRedeploy: true, deploymentId: 'fe1' }),
            makeRolloutStep('migrations', { state: 'succeeded', migrationRunId: RUN_ID }),
            makeRolloutStep('backend', { state: 'succeeded', provider: 'render', status: 'ready', url: 'https://inventory-api-preview.onrender.com', canRedeploy: true, deploymentId: 'be1' }),
          ],
        }),
      })
    })
    const { panel } = await openPreview(project)
    const rows = await stepRows(panel)
    expect(rows.map((r) => r.dataset.testid)).toEqual(['rollout-step-migrations', 'rollout-step-backend', 'rollout-step-frontend'])
    expect(within(panel).getByTestId('rollout-state')).toHaveTextContent('Succeeded')
    expect(panel).toHaveTextContent('Commit 7c1d8e7')

    const migrations = row(panel, 'migrations')
    expect(within(migrations).getByTestId('rollout-step-state')).toHaveTextContent('Succeeded')
    expect(within(migrations).getByRole('link', { name: 'View the migration run' })).toHaveAttribute('href', `/projects/${project.id}/migrations`)

    const backend = row(panel, 'backend')
    expect(within(backend).getByTestId('rollout-step-host')).toHaveTextContent('Render')
    expect(within(backend).getByTestId('deployment-status')).toHaveTextContent('Ready')
    expect(within(backend).getByRole('link', { name: /inventory-api-preview\.onrender\.com/ })).toHaveAttribute(
      'href',
      'https://inventory-api-preview.onrender.com',
    )
    const frontend = row(panel, 'frontend')
    expect(within(frontend).getByTestId('rollout-step-host')).toHaveTextContent('Netlify')
    expect(within(frontend).getByRole('link', { name: /netlify\.app/ })).toBeInTheDocument()
  })

  it('FR147_failed_predeploy_frontend_skipped: a failed backend pre-deploy shows its error and open log tail; the frontend is skipped', async () => {
    const project = seedReview('contributor', (p) => {
      seedPreviewEnvironment(p, {
        current: makeRollout(p, {
          state: 'failed',
          steps: [
            makeRolloutStep('backend', {
              state: 'failed',
              provider: 'render',
              status: 'error',
              deploymentId: 'be1',
              errorMessage: 'The pre-deploy command exited with 1.',
              buildLogTail: LOG,
              canRedeploy: true,
            }),
            makeRolloutStep('frontend', { state: 'skipped', provider: 'netlify', note: 'Not started because the backend preview failed.' }),
          ],
        }),
      })
    })
    const { panel } = await openPreview(project)
    expect(await within(panel).findByTestId('rollout-state')).toHaveTextContent('Failed')
    const backend = row(panel, 'backend')
    expect(within(backend).getByTestId('deployment-status')).toHaveTextContent('Error')
    expect(within(backend).getByTestId('deployment-failure')).toHaveTextContent('The pre-deploy command exited with 1.')
    const log = within(backend).getByTestId('build-log-tail')
    expect(log.tagName).toBe('PRE')
    expect(log).toHaveTextContent('relation "orders" already exists')
    expect(log.closest('details')).toHaveAttribute('open')

    const frontend = row(panel, 'frontend')
    expect(within(frontend).getByTestId('rollout-step-state')).toHaveTextContent('Skipped')
    expect(within(frontend).getByTestId('rollout-step-note')).toHaveTextContent('Skipped: Not started because the backend preview failed.')
    expect(within(frontend).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
  })

  it('FR147_github_pages_note: a GitHub Pages frontend is skipped with its note, shown once', async () => {
    const dbNote = "Preview uses the production database without this version's migrations."
    const project = seedReview('owner', (p) => {
      seedPreviewEnvironment(p, {
        notes: [dbNote, GITHUB_PAGES_PREVIEW_NOTE],
        current: makeRollout(p, {
          state: 'succeeded',
          steps: [
            makeRolloutStep('backend', { state: 'succeeded', provider: 'render', status: 'ready', url: 'https://api-preview.onrender.com', deploymentId: 'be1', canRedeploy: true }),
            makeRolloutStep('frontend', { state: 'skipped', provider: 'github_pages', note: GITHUB_PAGES_PREVIEW_NOTE }),
          ],
        }),
      })
    })
    const { panel } = await openPreview(project)
    const frontend = await within(panel).findByTestId('rollout-step-frontend')
    expect(within(frontend).getByTestId('rollout-step-host')).toHaveTextContent('GitHub Pages')
    expect(within(frontend).getByTestId('rollout-step-note')).toHaveTextContent(`Skipped: ${GITHUB_PAGES_PREVIEW_NOTE}`)
    const notes = within(panel).getAllByTestId('preview-note')
    expect(notes.map((n) => n.textContent)).toEqual([dbNote])
  })

  it('FR147_redeploy_and_polling: a running rollout is polled until it succeeds; a Contributor redeploys a step', async () => {
    const project = seedReview('contributor', (p) => {
      const be = seedDeployment(p, { component: 'backend', provider: 'render', platform: 'render', gitSha: SHA, status: 'building', url: null })
      const beReady = { ...be, status: 'ready' as const, url: 'https://inventory-api-preview.onrender.com' }
      const fe = seedDeployment(p, { component: 'frontend', provider: 'netlify', platform: 'netlify', gitSha: SHA, url: 'https://x--inventory-web.netlify.app' })
      const running = makeRollout(p, { gitSha: SHA, steps: [rolloutStepOf(be), makeRolloutStep('frontend', { provider: 'netlify' })] })
      const done = makeRollout(p, { gitSha: SHA, state: 'succeeded', steps: [rolloutStepOf(beReady), rolloutStepOf(fe)] })
      seedPreviewEnvironment(p, { current: running }, [running, running, done])
    })
    const { panel, user } = await openPreview(project)
    expect(await within(panel).findByTestId('rollout-state')).toHaveTextContent('Rolling out')
    expect(within(row(panel, 'frontend')).getByTestId('rollout-step-state')).toHaveTextContent('Waiting')
    expect(within(panel).queryByRole('button', { name: 'Deploy preview' })).not.toBeInTheDocument()

    await waitFor(() => {
      expect(within(panel).getByTestId('rollout-state')).toHaveTextContent('Succeeded')
    })
    await user.click(within(row(panel, 'backend')).getByRole('button', { name: 'Redeploy preview backend (7c1d8e7)' }))
    expect(await screen.findByText(/Redeploying 7c1d8e7/)).toBeInTheDocument()
    expect(requestsTo('POST', '/redeploy')).toHaveLength(1)
  })

  it('FR147_viewer_reads_only: a Viewer sees the rows but no Redeploy or Deploy preview', async () => {
    const project = seedReview('viewer', (p) => {
      seedPreviewEnvironment(p, {
        current: makeRollout(p, {
          state: 'succeeded',
          steps: [makeRolloutStep('frontend', { state: 'succeeded', provider: 'vercel', status: 'ready', deploymentId: 'fe1', canRedeploy: true })],
        }),
      })
    })
    const { panel } = await openPreview(project)
    await within(panel).findByTestId('rollout-step-frontend')
    expect(within(panel).queryByRole('button', { name: /Redeploy|Deploy preview/ })).not.toBeInTheDocument()
  })
})

const DIVERGED = 'The preview database has migration 0003_add_index, which the version branch no longer has.'

/** The diverged preview environment of FR-148: the last rollout blocked, the preview Render Postgres available. */
function seedDiverged(p: StoredProject) {
  deploymentOf(p).databases = [
    makeDatabase('production', 'render', { status: 'available' }),
    makeDatabase('preview', 'render', { status: 'available', resetRecommended: true }),
  ]
  seedPreviewEnvironment(p, {
    databaseDiverged: true,
    divergedMessage: DIVERGED,
    current: makeRollout(p, {
      state: 'blocked',
      errorCode: 'PREVIEW_DATABASE_DIVERGED',
      steps: [makeRolloutStep('backend', { provider: 'render' }), makeRolloutStep('frontend', { provider: 'netlify' })],
    }),
  })
}

describe('Preview database reset (FR-148)', () => {
  it('FR148_diverged_owner_resets: the blocked rollout says why, and the Owner resets the preview database after confirming', async () => {
    const project = seedReview('owner', seedDiverged)
    const { panel, user } = await openPreview(project)
    expect(await within(panel).findByTestId('preview-diverged')).toHaveTextContent(DIVERGED)
    expect(within(panel).getByTestId('rollout-state')).toHaveTextContent('Blocked')
    expect(within(panel).getByTestId('rollout-failure')).toHaveTextContent(errorMessages.PREVIEW_DATABASE_DIVERGED)

    await user.click(within(panel).getByRole('button', { name: 'Reset preview database' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Reset the preview database?' })
    expect(dialog).toHaveTextContent('drops every table and row in the preview database')
    expect(requestsTo('POST', '/databases/preview/reset')).toHaveLength(0)
    await user.click(within(dialog).getByRole('button', { name: 'Reset' }))

    expect((await screen.findAllByText(previewResetCopy.reset_schema_external)).length).toBeGreaterThan(0)
    expect(requestsTo('POST', '/databases/preview/reset')).toHaveLength(1)
    await waitFor(() => {
      expect(within(panel).queryByTestId('preview-diverged')).not.toBeInTheDocument()
    })
  })

  it('FR148_contributor_cannot_reset: a Contributor sees the reason and that an Owner can reset; Deploy preview says why it is blocked', async () => {
    const project = seedReview('contributor', seedDiverged)
    const { panel, user } = await openPreview(project)
    expect(await within(panel).findByTestId('preview-diverged')).toHaveTextContent('A project Owner can reset it.')
    expect(within(panel).queryByRole('button', { name: 'Reset preview database' })).not.toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Deploy preview' }))
    expect(await screen.findByText(errorMessages.PREVIEW_DATABASE_DIVERGED)).toBeInTheDocument()
    expect(requestsTo('POST', '/deployments/preview')).toHaveLength(1)
  })

  it('FR148_reset_recommended_manual_railway: the preview card says Reset recommended; on Railway the reset is recorded and done there', async () => {
    const me = signInAsOwner()
    const project = seedProject({})
    expect(me.id).toBeTruthy()
    seedNetlifyRenderTargets(project, { configured: true })
    deploymentOf(project).databases = [
      makeDatabase('production', 'railway', { status: 'available' }),
      makeDatabase('preview', 'railway', { status: 'available', resetRecommended: true }),
    ]
    const { user } = await openSettings('deployment', project)
    const card = await screen.findByRole('region', { name: 'Preview database' })
    expect(within(card).getByTestId('database-reset-recommended')).toHaveTextContent('migrations the version branch no longer has')
    expect(within(await screen.findByRole('region', { name: 'Production database' })).queryByRole('button', { name: 'Reset preview database' })).not.toBeInTheDocument()

    await user.click(within(card).getByRole('button', { name: 'Reset preview database' }))
    await user.click(within(await screen.findByRole('alertdialog', { name: 'Reset the preview database?' })).getByRole('button', { name: 'Reset' }))
    expect(await within(card).findByTestId('preview-reset-result')).toHaveTextContent(previewResetCopy.manual)
    expect(previewResetCopy.manual).toMatch(/Empty the preview Postgres in Railway/)
  })

  it('FR148_invalid_state_copy: a reset refused while a rollout runs shows the reason in the INVALID_STATE copy', async () => {
    signInAsOwner()
    const project = seedProject({})
    seedNetlifyRenderTargets(project, { configured: true })
    deploymentOf(project).databases = [makeDatabase('preview', 'render', { status: 'available' })]
    deploymentDb.previewResetBusy = true
    const { user } = await openSettings('deployment', project)
    const card = await screen.findByRole('region', { name: 'Preview database' })
    expect(within(card).queryByTestId('database-reset-recommended')).not.toBeInTheDocument()
    await user.click(within(card).getByRole('button', { name: 'Reset preview database' }))
    await user.click(within(await screen.findByRole('alertdialog', { name: 'Reset the preview database?' })).getByRole('button', { name: 'Reset' }))
    expect(await within(card).findByTestId('preview-reset-error')).toHaveTextContent(
      "This can't be done right now. A preview rollout or reset is running. Wait for it to finish, then reset the preview database.",
    )
    expect(errorMessage(new ApiError({ status: 409, errorCode: 'INVALID_STATE' }))).toBe(errorMessages.INVALID_STATE)
  })
})

/** Release 0.1.0 of the Netlify + Render project, released; `release` overrides the release attempt. */
function seedReleased(role: ProjectRole, release: (p: StoredProject) => Parameters<typeof seedRelease>[1]) {
  return seedProjectAs(role, (p) => {
    p.currentPhase = 'release'
    p.status = 'released'
    p.releaseVersions = [makeReleaseVersion({ version: '0.1.0', status: 'released' })]
    seedNetlifyRenderTargets(p, { configured: true })
    seedRelease(p, { tag: 'v0.1.0', ...release(p) })
  })
}

async function openProduction(project: StoredProject) {
  await renderRoute(`/projects/${project.id}/release`)
  await screen.findAllByRole('heading', { level: 2, name: 'Release' })
  return { panel: await screen.findByRole('region', { name: 'Production deployment' }), user: userEvent.setup() }
}

describe('Release page → production rollout (FR-149)', () => {
  it('FR149_timeline_live_on: migrations → backend → frontend, and "Released 0.1.0 · live on Netlify and Render" with both URLs', async () => {
    const project = seedReleased('owner', (p) => ({
      migrationRunId: RUN_ID,
      rollout: makeRollout(p, {
        environment: 'production',
        state: 'succeeded',
        gitRef: 'main',
        steps: [
          makeRolloutStep('migrations', { state: 'succeeded', migrationRunId: RUN_ID }),
          makeRolloutStep('backend', { state: 'succeeded', provider: 'render', status: 'ready', url: 'https://inventory-api.onrender.com', deploymentId: 'be', canRedeploy: true }),
          makeRolloutStep('frontend', { state: 'succeeded', provider: 'netlify', status: 'ready', url: 'https://inventory-web.netlify.app', deploymentId: 'fe', canRedeploy: true }),
        ],
      }),
      liveOn: [
        { component: 'backend', provider: 'render', providerName: 'Render', url: 'https://inventory-api.onrender.com' },
        { component: 'frontend', provider: 'netlify', providerName: 'Netlify', url: 'https://inventory-web.netlify.app' },
      ],
    }))
    const { panel } = await openProduction(project)
    const live = await within(panel).findByTestId('production-live')
    expect(live).toHaveTextContent('Released 0.1.0 · live on Netlify and Render')
    expect(within(live).getByRole('link', { name: /inventory-web\.netlify\.app/ })).toHaveAttribute('href', 'https://inventory-web.netlify.app')
    expect(within(live).getByRole('link', { name: /inventory-api\.onrender\.com/ })).toHaveAttribute('href', 'https://inventory-api.onrender.com')
    const rows = await stepRows(panel, 'Production rollout steps')
    expect(rows.map((r) => r.dataset.testid)).toEqual(['rollout-step-migrations', 'rollout-step-backend', 'rollout-step-frontend'])
    expect(within(row(panel, 'migrations')).getByRole('link', { name: 'View the migration run' })).toBeInTheDocument()
    expect(within(panel).getByTestId('rollout-state')).toHaveTextContent('Succeeded')
    expect(within(panel).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
    expect(joinNames(['Vercel', 'Render', 'Railway'])).toBe('Vercel, Render and Railway')
  })

  it('FR149_failed_step_redeploy: a failed frontend shows its error; the Owner redeploys it, a Contributor is told an Owner can', async () => {
    const seed = (p: StoredProject) => {
      const fe = seedDeployment(p, {
        target: 'production',
        component: 'frontend',
        provider: 'netlify',
        platform: 'netlify',
        status: 'error',
        gitSha: SHA,
        errorMessage: 'Build script returned non-zero exit code: 2',
      })
      return {
        rollout: makeRollout(p, {
          environment: 'production',
          state: 'failed',
          gitSha: SHA,
          steps: [makeRolloutStep('backend', { state: 'succeeded', provider: 'render', status: 'ready' }), rolloutStepOf(fe)],
        }),
      }
    }
    const owner = await openProduction(seedReleased('owner', seed))
    expect(await within(owner.panel).findByText(/The release itself is done/)).toHaveTextContent('Fix the cause, then redeploy the failed step.')
    expect(within(row(owner.panel, 'frontend')).getByTestId('deployment-failure')).toHaveTextContent('non-zero exit code: 2')
    await owner.user.click(within(owner.panel).getByRole('button', { name: 'Redeploy production frontend (7c1d8e7)' }))
    expect(await screen.findByText(/Redeploying 7c1d8e7/)).toBeInTheDocument()
  })

  it('FR149_contributor_cannot_redeploy_rollout', async () => {
    const { panel } = await openProduction(
      seedReleased('contributor', (p) => ({
        rollout: makeRollout(p, {
          environment: 'production',
          state: 'failed',
          steps: [makeRolloutStep('frontend', { state: 'failed', provider: 'netlify', status: 'error', deploymentId: 'fe', canRedeploy: true })],
        }),
      })),
    )
    expect(await within(panel).findByText(/An Owner can redeploy/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: /Redeploy/ })).not.toBeInTheDocument()
  })

  it('FR149_migrations_applied_not_merged: the warning and the production migration run link', async () => {
    const project = seedReleased('owner', () => ({ status: 'failed', migrationRunId: RUN_ID, migrationsAppliedNotMerged: true }))
    const { panel } = await openProduction(project)
    expect(await within(panel).findByTestId('migrations-applied-not-merged')).toHaveTextContent('migrations were applied')
    expect(panel).toHaveTextContent('Migrations applied, code not merged')
    expect(within(panel).getByRole('link', { name: 'View the production migration run' })).toHaveAttribute('href', `/projects/${project.id}/migrations`)
  })
})

describe('Rollback dialog → migration warning (FR-150)', () => {
  it('FR150_migration_warning_backend_only: only a backend older than the latest migration warns; a frontend or newer backend does not', async () => {
    const sha = (c: string) => c.repeat(40)
    const project = seedProjectAs('owner', (p) => {
      seedNetlifyRenderTargets(p, { configured: true })
      const fe = { component: 'frontend', provider: 'netlify', platform: 'netlify', target: 'production' } as const
      const be = { component: 'backend', provider: 'render', platform: 'render', target: 'production' } as const
      seedDeployment(p, { ...fe, gitSha: sha('1'), releaseVersion: '1.1.0', olderThanLatestMigration: true })
      seedDeployment(p, { ...fe, gitSha: sha('2'), releaseVersion: '1.2.0' })
      seedDeployment(p, { ...be, gitSha: sha('3'), releaseVersion: '1.0.0', olderThanLatestMigration: true })
      seedDeployment(p, { ...be, gitSha: sha('4'), releaseVersion: '1.1.0', olderThanLatestMigration: false })
      seedDeployment(p, { ...be, gitSha: sha('5'), releaseVersion: '1.2.0', olderThanLatestMigration: false })
    })
    await renderRoute(`/projects/${project.id}/deployments`)
    const user = userEvent.setup()
    const list = await screen.findByRole('list', { name: 'Deployments, newest first' })

    await user.click(within(list).getByRole('button', { name: 'Restore 1.1.0 (1111111)' }))
    let dialog = await screen.findByRole('alertdialog', { name: 'Restore 1.1.0 to production?' })
    expect(within(dialog).queryByTestId('rollback-migration-warning')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    await user.click(within(list).getByRole('button', { name: 'Roll back 1.1.0 (4444444)' }))
    dialog = await screen.findByRole('alertdialog', { name: 'Roll back production to 1.1.0?' })
    expect(within(dialog).queryByTestId('rollback-migration-warning')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    await user.click(within(list).getByRole('button', { name: 'Roll back 1.0.0 (3333333)' }))
    dialog = await screen.findByRole('alertdialog', { name: 'Roll back production to 1.0.0?' })
    expect(within(dialog).getByTestId('rollback-migration-warning')).toHaveTextContent('Rolling back the backend never rolls back its database.')
  })
})

describe('Preview build failure titles (FR-90, FR-147)', () => {
  it('FR90_preview_build_failure_title: a lone Vercel frontend that failed reads "Vercel preview build failed"; a pre-deploy failure names the migration', () => {
    expect(previewBuildFailureTitles([makeRolloutStep('frontend', { state: 'failed', provider: 'vercel', status: 'error' })])).toEqual([
      'Vercel preview build failed',
    ])
    expect(
      previewBuildFailureTitles([
        makeRolloutStep('frontend', { state: 'skipped' }),
        makeRolloutStep('backend', { state: 'failed', provider: 'render', status: 'error', errorMessage: 'pre_deploy_failed' }),
        makeRolloutStep('migrations', { state: 'succeeded' }),
      ]),
    ).toEqual(['Backend preview pre-deploy migration failed'])
    expect(previewBuildFailureTitles([makeRolloutStep('frontend', { state: 'succeeded', provider: 'netlify', status: 'ready' })])).toEqual([])
  })
})
