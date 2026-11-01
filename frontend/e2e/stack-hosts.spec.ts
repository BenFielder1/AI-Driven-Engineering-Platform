import { expect, test, type Page } from '@playwright/test'

import { apiJson, projectState, registerViaApi } from './support/api.ts'
import {
  createComposedProjectInWizard,
  drivePlanToExecutionStarted,
  driveSpecToPlanDraft,
  driveToSpecDraft,
  projectStack,
  waitForGateOrEscalation,
} from './support/composition.ts'
import {
  createPlatformConnection,
  putDatabase,
  putTarget,
  setUpNetlifyRenderHosts,
  waitForHostsReady,
  waitForPreviewRollout,
  waitForProductionRollout,
} from './support/hosted-stack.ts'
import { AGENT_TIMEOUT, approveDraftViaApi, approveStageGate, createLocalProject, openStage, waitForPhase } from './support/v2.ts'

/**
 * SPEC §8 v4 E2E (Fake LLM + Local Git + Fake deployments and databases): a hosted composition rolled out to preview and
 * production, a Supabase BaaS composition with a Fake migration run, the Stack step's filtering and the wizard's AI model
 * section. The migrated v2 preset project releasing a version is in migrated-preset.a11y.spec.ts (it needs the seed Admin).
 */

const IDEA = 'A recipe box: save recipes with their ingredients through an API and browse them in a web app.'

/** The wizard from scratch with a Local repository up to the Stack step, with no stack chosen yet. */
async function wizardToStack(page: Page, name: string) {
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Step 1 of 8: Source' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await page.getByLabel('Project name').fill(name)
  await page.getByLabel('Your idea').fill(IDEA)
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 3 of 8: Repository' })).toBeVisible()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 4 of 8: Version' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 5 of 8: Stack' })).toBeVisible()
}

test('S8_FR114_FR115_FR147_FR149: React + ASP.NET + EF Core on Netlify, Render and Render Postgres rolls out preview with both components, then releases to production', async ({
  page,
}) => {
  test.setTimeout(20 * 60_000)
  const context = page.context()
  const stamp = String(Date.now())
  const name = `E2E Hosted stack ${stamp}`
  await registerViaApi(context, 'hosted', 'Hosted Stack Owner')

  // FR-114, FR-115: composed in the wizard, with the hosts of the Deployment layer.
  const projectId = await createComposedProjectInWizard(page, name, IDEA, {
    hosts: { frontend: 'Netlify', backend: 'Render', database: 'Render Postgres' },
  })
  const stack = await projectStack(context, projectId)
  expect(stack.composition?.deploy).toMatchObject({
    frontendHost: 'deploy-netlify',
    backendHost: 'deploy-render',
    databaseHost: 'render-postgres',
  })

  // FR-142, FR-144, FR-152: connections, both targets and both databases, set up before anything is built.
  await setUpNetlifyRenderHosts(context, projectId, `hosted-${stamp}`)
  await page.goto(`/projects/${projectId}/settings/deployment`)
  const frontendCard = page.getByTestId('target-frontend')
  const backendCard = page.getByTestId('target-backend')
  await expect(frontendCard).toContainText('Netlify')
  await expect(backendCard).toContainText('Render')
  await expect(page.getByTestId('database-production').getByTestId('database-status')).toHaveText('Available')
  await expect(page.getByTestId('database-preview').getByTestId('database-status')).toHaveText('Available')

  // The lifecycle (Fake LLM) up to the pre-release gate. Strict: spec, plan and pre-release gates.
  const specDraft = await driveToSpecDraft(context, projectId)
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${specDraft.id}/approve`, {})
  await driveSpecToPlanDraft(context, projectId)
  await approveDraftViaApi(context, projectId, 'implementation_plan')
  await drivePlanToExecutionStarted(context, projectId)
  await waitForPhase(context, projectId, 'review', 3 * AGENT_TIMEOUT)
  await expect
    .poll(async () => (await projectState(context, projectId)).review.latestDecision, { timeout: 3 * AGENT_TIMEOUT })
    .not.toBeNull()
  if ((await projectState(context, projectId)).review.latestDecision === 'review_required') {
    await approveDraftViaApi(context, projectId, 'optimization_plan')
  }
  await waitForGateOrEscalation(context, projectId, 'pre_release', 4 * AGENT_TIMEOUT)
  expect((await projectState(context, projectId)).review.latestDecision).toBe('release_eligible')

  // FR-147: the preview rollout deployed the backend, then the frontend, each on its own host.
  const preview = await waitForPreviewRollout(context, projectId, 'succeeded')
  expect(preview.steps.map((s) => [s.step, s.state, s.provider, s.status])).toEqual([
    ['backend', 'succeeded', 'render', 'ready'],
    ['frontend', 'succeeded', 'netlify', 'ready'],
  ])
  const sha8 = preview.gitSha.slice(0, 8).toLowerCase()
  expect(preview.steps.map((s) => s.url)).toEqual([`https://fake-${sha8}-backend.render.test`, `https://fake-${sha8}-frontend.netlify.test`])
  const previewRows = await apiJson<{ component: string; gitSha: string; createdAt: string; readyAt: string | null }[]>(
    context,
    'GET',
    `/api/projects/${projectId}/deployments?target=preview`,
  )
  const ofHead = previewRows.filter((d) => d.gitSha === preview.gitSha)
  const backendRow = ofHead.find((d) => d.component === 'backend')
  const frontendRow = ofHead.find((d) => d.component === 'frontend')
  expect(new Date(backendRow?.readyAt ?? '').getTime()).toBeLessThanOrEqual(new Date(frontendRow?.createdAt ?? '').getTime())

  // FR-147: the Review page shows the same two steps.
  await page.goto(`/projects/${projectId}/review`)
  const previewSection = page.locator('#review-preview-deployment')
  await expect(previewSection.getByTestId('rollout-state')).toHaveText('Succeeded')
  const previewBackend = previewSection.getByTestId('rollout-step-backend')
  const previewFrontend = previewSection.getByTestId('rollout-step-frontend')
  await expect(previewBackend.getByTestId('rollout-step-host')).toHaveText('Render')
  await expect(previewBackend.getByTestId('deployment-status')).toHaveText('Ready')
  await expect(previewFrontend.getByTestId('rollout-step-host')).toHaveText('Netlify')
  await expect(previewFrontend.getByTestId('deployment-status')).toHaveText('Ready')

  // FR-149: approved on the Release page; the production rollout deploys both; the release says where it is live.
  await openStage(page, 'Release')
  await approveStageGate(page, 'Release approval')
  await expect
    .poll(async () => (await projectState(context, projectId)).status, { timeout: 2 * AGENT_TIMEOUT, intervals: [500, 1000] })
    .toBe('released')
  const release = await waitForProductionRollout(context, projectId)
  expect(release.tag).toBe('v0.1.0')
  expect(release.rollout?.steps.map((s) => [s.step, s.state, s.provider])).toEqual([
    ['backend', 'succeeded', 'render'],
    ['frontend', 'succeeded', 'netlify'],
  ])
  const live = release.liveOn ?? []
  expect(live.map((l) => [l.component, l.providerName]).sort()).toEqual([
    ['backend', 'Render'],
    ['frontend', 'Netlify'],
  ])

  await page.reload()
  await expect(page.getByTestId('production-live')).toContainText('Released 0.1.0 · live on Netlify and Render', { timeout: AGENT_TIMEOUT })
  const productionSection = page.locator('#release-production-deployment')
  await expect(productionSection.getByTestId('rollout-state')).toHaveText('Succeeded')
  await expect(productionSection.getByRole('list', { name: 'Production rollout steps' }).getByRole('listitem')).toHaveCount(2)
  const productionRows = await apiJson<{ component: string; status: string }[]>(context, 'GET', `/api/projects/${projectId}/deployments?target=production`)
  expect(productionRows.map((d) => [d.component, d.status]).sort()).toEqual([
    ['backend', 'ready'],
    ['frontend', 'ready'],
  ])
})

test('S8_FR145_FR147_FR153: a Vue + Supabase BaaS composition runs a Fake migration on its preview database during the rollout', async ({ page }) => {
  test.setTimeout(15 * 60_000)
  const context = page.context()
  const stamp = String(Date.now())
  await registerViaApi(context, 'baas', 'BaaS Owner')

  // FR-114: nothing but the frontend and Supabase as its backend. A retry creates a new project (and so a new, empty
  // Fake database), so every attempt applies the file.
  const project = await createLocalProject(context, {
    name: `E2E Vue Notes ${stamp}`,
    idea: 'A notes app: sign in, write notes, tag them and search them.',
    stack: {
      frontend: { pack: 'fe-vue', path: '.' },
      backend: { pack: 'none' },
      data: { pack: 'data-supabase' },
      vcs: { pack: 'vcs-github' },
      deploy: { frontendHost: 'deploy-netlify', databaseHost: 'supabase', local: 'none' },
    },
  })
  const projectId = project.id
  const stack = await projectStack(context, projectId)
  expect(stack.composition?.data).toMatchObject({ pack: 'data-supabase', mode: 'baas' })

  const netlify = await createPlatformConnection(context, 'netlify', 'Fake Netlify')
  const supabase = await createPlatformConnection(context, 'supabase', 'Fake Supabase')
  await putTarget(context, projectId, 'frontend', 'netlify', netlify, `notes-${stamp}`)
  await putDatabase(context, projectId, 'production', 'supabase', supabase, 'eu-west-2')
  await putDatabase(context, projectId, 'preview', 'supabase', supabase, 'eu-west-2')
  await waitForHostsReady(context, projectId, ['frontend'], ['production', 'preview'])

  const specDraft = await driveToSpecDraft(context, projectId)
  await apiJson(context, 'POST', `/api/projects/${projectId}/artifacts/${specDraft.id}/approve`, {})
  await driveSpecToPlanDraft(context, projectId)
  await approveDraftViaApi(context, projectId, 'implementation_plan')
  await drivePlanToExecutionStarted(context, projectId)

  // FR-147, FR-153: the preview rollout migrates first, then deploys the frontend.
  const rollout = await waitForPreviewRollout(context, projectId, 'succeeded', 6 * AGENT_TIMEOUT)
  expect(rollout.steps.map((s) => [s.step, s.state])).toEqual([
    ['migrations', 'succeeded'],
    ['frontend', 'succeeded'],
  ])
  const runId = rollout.steps[0]?.migrationRunId ?? ''
  expect(runId).not.toBe('')

  const run = await apiJson<{
    environment: string
    status: string
    gitSha: string
    files: { version: string; name: string }[]
    advisorFindings: { name: string }[]
  }>(context, 'GET', `/api/projects/${projectId}/migrations/${runId}`)
  expect(run).toMatchObject({ environment: 'preview', status: 'succeeded', gitSha: rollout.gitSha })
  // §9: the Fake's security advisors report one finding on every Supabase run.
  expect(run.advisorFindings.map((f) => f.name)).toContain('rls_disabled_in_public')
  expect(run.files.map((f) => f.version)).toEqual(['20260101000000'])

  // FR-153: the Migrations page lists the run with its status, and the Review page links to it.
  await page.goto(`/projects/${projectId}/migrations`)
  const card = page.getByRole('article', { name: /^Preview migration run / })
  await expect(card.getByTestId('migration-status')).toHaveText('Succeeded')
  await expect(card.getByRole('list', { name: 'Files applied' })).toContainText('20260101000000')
  await expect(card.getByRole('list', { name: 'Security advisor findings' })).toContainText('RLS Disabled in Public')

  await page.goto(`/projects/${projectId}/review`)
  const step = page.locator('#review-preview-deployment').getByTestId('rollout-step-migrations')
  await expect(step).toContainText('Database migrations')
  await step.getByRole('link', { name: 'View the migration run' }).click()
  await expect(page.getByRole('heading', { level: 2, name: 'Migrations', exact: true })).toBeVisible()
})

test('S8_FR114: on the Stack step, picking ASP.NET Core leaves Prisma listed but disabled, with its reason', async ({ page }) => {
  await registerViaApi(page.context(), 'stackfilter', 'Stack Filter Owner')
  await wizardToStack(page, `E2E Stack filter ${String(Date.now())}`)
  await page.getByRole('radio', { name: /^Choose the stack/ }).check()

  await expect(page.getByRole('heading', { level: 3, name: 'Frontend', exact: true })).toBeVisible()
  await page.getByRole('radio', { name: 'React + Vite', exact: true }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { level: 3, name: 'Backend', exact: true })).toBeVisible()
  await page.getByRole('radio', { name: 'ASP.NET Core', exact: true }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { level: 3, name: 'Data', exact: true })).toBeVisible()

  const data = page.getByRole('group', { name: 'Data', exact: true })
  await expect(data.getByRole('radio', { name: 'EF Core + PostgreSQL', exact: true })).toBeVisible()
  await data.getByText(/^Not available with your choices \(\d+\)$/).click()
  const reason = 'Prisma needs Express, NestJS or Next.js full-stack'
  const prisma = data.getByRole('radio', { name: 'Prisma', exact: true })
  await expect(prisma).toHaveAttribute('aria-disabled', 'true')
  await expect(prisma).toHaveAccessibleDescription(new RegExp(reason.replace(/[.+]/g, '\\$&')))
  await expect(data.getByText(reason)).toBeVisible()

  // A forbidden card can't be chosen. `force`: Playwright treats aria-disabled as not actionable.
  await prisma.click({ force: true })
  await expect(prisma).not.toBeChecked()
  await expect(page.getByRole('heading', { level: 3, name: 'Data', exact: true })).toBeVisible()
})

test('S8_FR159: the AI model section chooses the Fake provider and fake-1, and the project is created with them', async ({ page }) => {
  const context = page.context()
  await registerViaApi(context, 'llmchoice', 'LLM Choice Owner')
  const name = `E2E AI model ${String(Date.now())}`
  await wizardToStack(page, name)
  await expect(page.getByRole('radio', { name: /^No stack \(general\)/ })).toBeChecked()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 6 of 8: Configuration' })).toBeVisible()

  const section = page.getByRole('region', { name: 'AI model' })
  await expect(section).toBeVisible()
  // The keyless Ollama first, so choosing Fake below is an actual change (Fake is CI's preselected default, FR-158).
  await section.getByRole('radio', { name: 'Ollama', exact: true }).check()
  const model = section.getByLabel('Default model', { exact: true })
  await expect(model).not.toHaveValue('fake-1')
  const fake = section.getByRole('radio', { name: 'Fake (testing)', exact: true })
  await fake.check()
  await expect(fake).toHaveAccessibleDescription('No key needed.')
  await model.selectOption('fake-1')
  await expect(model).toHaveValue('fake-1')

  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 7 of 8: Deployment' })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Step 8 of 8: Review & create' })).toBeVisible()
  await page.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()

  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  const llm = await apiJson<{ provider: string; defaultModel: string }>(context, 'GET', `/api/projects/${projectId}/settings/llm`)
  expect(llm).toMatchObject({ provider: 'fake', defaultModel: 'fake-1' })
})
