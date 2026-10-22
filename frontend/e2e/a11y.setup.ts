import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

import { expect, test as setup } from '@playwright/test'

import { A11Y_FIXTURE, type A11yFixture } from './support/a11y-fixture.ts'
import { api, apiJson, createProjectViaApi, driveProject, registerViaApi, waitForDraft } from './support/api.ts'
import { createLocalProject, createVercelConnection } from './support/v2.ts'

/**
 * NFR-A11Y fixture: one user with a Fake Vercel connection and a released strict-mode project (Fake + Local + Fake
 * deployments, so the deployment pages have preview and production rows: NFR-A11Y2), and a second project whose project
 * request has two versions (for the diff view). Runs once per test run, before the a11y project.
 */
setup('a11y fixture: a released project and a two-version project request', async ({ browser }) => {
  setup.setTimeout(5 * 60_000)
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

  const fixture: A11yFixture = { storageState: await context.storageState(), projectId: project.id, diffProjectId: diffProject.id }
  mkdirSync(dirname(A11Y_FIXTURE), { recursive: true })
  writeFileSync(A11Y_FIXTURE, JSON.stringify(fixture))
  await context.close()
})
