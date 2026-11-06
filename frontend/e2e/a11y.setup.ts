import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

import { expect, test as setup } from '@playwright/test'

import { A11Y_FIXTURE, type A11yFixture } from './support/a11y-fixture.ts'
import { api, apiJson, createProjectViaApi, driveProject, registerViaApi, waitForDraft } from './support/api.ts'
import { NETLIFY_RENDER_STACK, setUpNetlifyRenderHosts } from './support/hosted-stack.ts'
import { createLocalProject, createVercelConnection } from './support/lifecycle.ts'
import {
  approveProfileViaApi,
  createInferProjectViaApi,
  createLinearConnection,
  createRailwayConnection,
  saveRailwayProfileViaApi,
  UNKNOWN_BACKEND_MARKER,
  waitForProfileDraft,
} from './support/tracker-inference.ts'

/**
 * NFR-A11Y fixture: one user with a Fake Vercel connection and a released strict-mode project (Fake + Local + Fake
 * deployments, so the deployment pages have preview and production rows: NFR-A11Y2), and a second project whose project
 * request has two versions (for the diff view). Runs once per test run, before the a11y project.
 */
setup('a11y fixture: a released project and a two-version project request', async ({ browser }) => {
  setup.setTimeout(14 * 60_000)
  const context = await browser.newContext()
  await registerViaApi(context, 'a11y', 'Accessibility Tester')
  const vercelConnectionId = await createVercelConnection(context, 'A11y Fake Vercel')
  const project = await createLocalProject(context, { name: `E2E Accessibility ${String(Date.now())}`, vercelConnectionId })
  await driveProject(context, project.id, 'released')

  const diffProject = await createProjectViaApi(context, `E2E Accessibility diff ${String(Date.now())}`)
  await apiJson(context, 'POST', `/api/projects/${diffProject.id}/refinement/run`, { answers: [] })
  await waitForDraft(context, diffProject.id, 'project_request')
  const second = await api(context, 'POST', `/api/projects/${diffProject.id}/refinement/run`, {
    answers: [{ questionId: 'q1', answer: 'Support agents.' }],
  })
  expect(second.status()).toBe(202)
  await expect
    .poll(
      async () =>
        (await apiJson<{ version: number }>(context, 'GET', `/api/projects/${diffProject.id}/artifacts/project_request/latest`)).version,
      { timeout: 60_000 },
    )
    .toBe(2)

  // SPEC §8 0.4.0: Settings → Stack and Settings → Deployment with real target and database cards.
  const stamp = String(Date.now())
  const stackProject = await createLocalProject(context, { name: `E2E Accessibility stack ${stamp}`, stack: NETLIFY_RENDER_STACK })
  await setUpNetlifyRenderHosts(context, stackProject.id, `a11y-${stamp}`)

  // SPEC 0.5.0 §7 (axe on the Analysis page, the tracker dialog and the group windows): a Linear and a Railway connection,
  // one inferring import left with a blank, and one approved with a Railway group to set up.
  await createLinearConnection(context, 'A11y Fake Linear')
  await createRailwayConnection(context, 'A11y Fake Railway')
  const request = `Add CSV export ${UNKNOWN_BACKEND_MARKER}`
  const inputProject = await createInferProjectViaApi(context, `E2E Accessibility analysis ${stamp}`, request)
  const approvedProject = await createInferProjectViaApi(context, `E2E Accessibility analysis approved ${stamp}`, request)
  await waitForProfileDraft(context, inputProject.id)
  const draft = await saveRailwayProfileViaApi(context, approvedProject.id, await waitForProfileDraft(context, approvedProject.id))
  await approveProfileViaApi(context, approvedProject.id, draft)

  const fixture: A11yFixture = {
    storageState: await context.storageState(),
    projectId: project.id,
    diffProjectId: diffProject.id,
    stackProjectId: stackProject.id,
    analysisInputProjectId: inputProject.id,
    analysisApprovedProjectId: approvedProject.id,
  }
  mkdirSync(dirname(A11Y_FIXTURE), { recursive: true })
  writeFileSync(A11Y_FIXTURE, JSON.stringify(fixture))
  await context.close()
})
