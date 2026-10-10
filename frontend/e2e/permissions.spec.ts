import { expect, test, type BrowserContext, type Page } from '@playwright/test'

import {
  addMember,
  api,
  apiJson,
  createProjectViaApi,
  driveProject,
  registerViaApi,
  waitForPendingGate,
  type TestUser,
} from './support/api.ts'

/**
 * §2 permissions, §7 "owner-only actions are hidden for other roles, and the API enforces them anyway", FR-6/§6
 * (non-members get 404). Four users, each in their own browser context: Owner, Contributor, Viewer, outsider.
 * The project waits in Specification with a pending spec_approval gate and an open spec draft.
 */

interface Actor {
  user: TestUser
  context: BrowserContext
  page: Page
}

let owner: Actor
let contributor: Actor
let viewer: Actor
let outsider: Actor
let projectId = ''
let gateId = ''
let draftId = ''

test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ browser }) => {
  // Four registrations can span two register rate-limit windows (5/min per IP, shared with other specs).
  test.setTimeout(8 * 60_000)
  const make = async (prefix: string): Promise<Actor> => {
    const context = await browser.newContext()
    const user = await registerViaApi(context, prefix, `E2E ${prefix}`, { inHook: true })
    return { user, context, page: await context.newPage() }
  }
  owner = await make('perm-owner')
  contributor = await make('perm-contrib')
  viewer = await make('perm-viewer')
  outsider = await make('perm-outsider')

  const project = await createProjectViaApi(owner.context, `E2E Permissions ${String(Date.now())}`)
  projectId = project.id
  await addMember(owner.context, projectId, contributor.user.email, 'contributor')
  await addMember(owner.context, projectId, viewer.user.email, 'viewer')

  // Spec v1 approved, advance → pending spec_approval gate; then a human spec draft v2 waits for approval.
  await driveProject(owner.context, projectId, 'specification')
  const spec = await apiJson<{ id: string }>(owner.context, 'GET', `/api/projects/${projectId}/artifacts/technical_specification/latest`)
  await apiJson(owner.context, 'POST', `/api/projects/${projectId}/artifacts/${spec.id}/approve`, {})
  await apiJson(owner.context, 'POST', `/api/projects/${projectId}/phase/advance`, {})
  gateId = (await waitForPendingGate(owner.context, projectId, 'spec_approval')).id
  const draft = await apiJson<{ id: string }>(owner.context, 'POST', `/api/projects/${projectId}/artifacts/technical_specification`, {
    content: '# Spec\n\nA human edit waiting for approval.\n',
    baseVersion: 1,
  })
  draftId = draft.id
})

test.afterAll(async () => {
  for (const actor of [owner, contributor, viewer, outsider] as (Actor | undefined)[]) {
    await actor?.context.close()
  }
})

const gatesPanel = (page: Page) => page.getByRole('region', { name: 'Pending gates' })

test('§2: the Owner sees gate decisions, artifact approval and every settings tab', async () => {
  const { page } = owner
  await page.goto(`/projects/${projectId}`)
  await expect(gatesPanel(page).getByRole('listitem', { name: 'Specification approval' })).toBeVisible()
  await expect(gatesPanel(page).getByRole('button', { name: 'Approve', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Advance to Planning' })).toBeVisible()

  await page.goto(`/projects/${projectId}/artifacts/technical_specification`)
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Edit as new draft' })).toBeVisible()

  await page.goto(`/projects/${projectId}/settings`)
  await expect(page.getByRole('tab')).toHaveText(['General', 'Members', 'Git', 'LLM', 'Notifications', 'Danger zone'])
})

test('§2: a Contributor can edit drafts but gate, approval and settings controls are hidden — and the API says 403', async () => {
  const { page, context } = contributor
  await page.goto(`/projects/${projectId}`)
  await expect(gatesPanel(page).getByRole('listitem', { name: 'Specification approval' })).toBeVisible()
  await expect(page.getByText('Only project Owners can decide gates.')).toBeVisible()
  await expect(gatesPanel(page).getByRole('button')).toHaveCount(0)

  await page.goto(`/projects/${projectId}/artifacts/technical_specification`)
  await expect(page.getByRole('button', { name: 'Edit as new draft' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Reject', exact: true })).toHaveCount(0)

  await page.goto(`/projects/${projectId}/settings`)
  await expect(page.getByRole('tab')).toHaveText(['General', 'Members'])
  await page.goto(`/projects/${projectId}/settings?tab=danger`)
  await expect(page.getByRole('button', { name: 'Roll back…' })).toHaveCount(0)

  expect((await api(context, 'POST', `/api/projects/${projectId}/gates/${gateId}/approve`, {})).status()).toBe(403)
  expect((await api(context, 'POST', `/api/projects/${projectId}/artifacts/${draftId}/approve`, {})).status()).toBe(403)
  expect((await api(context, 'POST', `/api/projects/${projectId}/rollback`, { targetVersion: 1, reason: 'no' })).status()).toBe(403)
  expect((await api(context, 'PATCH', `/api/projects/${projectId}`, { name: 'Renamed' })).status()).toBe(403)
  expect((await api(context, 'GET', `/api/projects/${projectId}/settings`)).status()).toBe(403)
  const problem = (await (await api(context, 'POST', `/api/projects/${projectId}/gates/${gateId}/approve`, {})).json()) as {
    errorCode?: string
  }
  expect(problem.errorCode).toBe('FORBIDDEN')
})

test('§2: a Viewer sees the project read-only — no advance, edit or run controls — and every mutation is 403', async () => {
  const { page, context } = viewer
  await page.goto(`/projects/${projectId}`)
  await expect(gatesPanel(page).getByRole('listitem', { name: 'Specification approval' })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Advance to/ })).toHaveCount(0)

  await page.goto(`/projects/${projectId}/artifacts/technical_specification`)
  await expect(page.getByRole('heading', { level: 2, name: 'Technical specification' })).toBeVisible()
  await expect(page.getByLabel('Version', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Edit as new draft' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)

  await page.goto(`/projects/${projectId}/refinement`)
  await expect(page.getByRole('button', { name: /round|refinement/i })).toHaveCount(0)

  await page.goto(`/projects/${projectId}/execution`)
  await expect(page.getByText('Only Contributors and Owners can start, pause or resume it.')).toBeVisible()
  await expect(page.getByRole('button', { name: /execution/i })).toHaveCount(0)

  await page.goto(`/projects/${projectId}/review`)
  await expect(page.getByRole('button', { name: 'Run review' })).toHaveCount(0)

  expect((await api(context, 'POST', `/api/projects/${projectId}/phase/advance`, {})).status()).toBe(403)
  expect(
    (await api(context, 'POST', `/api/projects/${projectId}/artifacts/technical_specification`, { content: '# x\n', baseVersion: 1 })).status(),
  ).toBe(403)
  expect((await api(context, 'POST', `/api/projects/${projectId}/execution/start`)).status()).toBe(403)
  expect((await api(context, 'POST', `/api/projects/${projectId}/gates/${gateId}/approve`, {})).status()).toBe(403)
})

test('§2, §6: a non-member gets "not found" in the UI and 404 from the API, and the project is not listed', async () => {
  const { page, context } = outsider
  await page.goto(`/projects/${projectId}`)
  await expect(page.getByRole('heading', { level: 1, name: 'Project not found' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Back to projects' })).toBeVisible()

  expect((await api(context, 'GET', `/api/projects/${projectId}`)).status()).toBe(404)
  expect((await api(context, 'GET', `/api/projects/${projectId}/state`)).status()).toBe(404)
  expect((await api(context, 'POST', `/api/projects/${projectId}/gates/${gateId}/approve`, {})).status()).toBe(404)
  const list = await apiJson<{ items: { id: string }[] }>(context, 'GET', '/api/projects?limit=100')
  expect(list.items.map((p) => p.id)).not.toContain(projectId)
})
