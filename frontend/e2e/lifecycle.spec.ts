import { expect, test, type Locator, type Page } from '@playwright/test'

import { addMember, apiJson, registerViaApi } from './support/api.ts'

/**
 * NFR-TEST, M8: the full lifecycle in a real browser with the Fake LLM provider and the Local Git provider —
 * register → wizard (strict mode: spec, plan and pre-release gates) → refinement rounds → spec → plan → execution
 * (live, FR-46) → branch comparison (FR-36) → review loop (FR-38–FR-41) → pre-release gate → release tagged with the
 * initial version (FR-42, FR-64, FR-66) → the released version is closed for rollback (FR-63) → Start new version
 * (FR-60, FR-62) → activity feed (FR-50) → notifications (FR-47).
 * A second user (a Viewer, in their own browser context) watches the overview to check FR-46's 2-second bound.
 */

const AGENT_TIMEOUT = 60_000
const LIVE_BOUND_MS = 2_000

function projectNav(page: Page): Locator {
  return page.getByRole('navigation', { name: 'Project sections' })
}

async function openSection(page: Page, name: 'Overview' | 'Refinement' | 'Artifacts' | 'Execution' | 'Review' | 'Settings') {
  await projectNav(page).getByRole('link', { name, exact: true }).click()
}

/** On an artifact detail page: approve the shown draft through the confirmation dialog. */
async function approveShownDraft(page: Page, label: string, version: number) {
  await page.getByRole('button', { name: 'Approve', exact: true }).click()
  const dialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: `Approve ${label} v${String(version)}?` }) })
  await dialog.getByRole('button', { name: `Approve v${String(version)}`, exact: true }).click()
  await expect(page.getByText(`v${String(version)} approved and locked.`)).toBeVisible()
}

/** From the Artifacts page: wait (live) for the agent's draft of `label`, open it and approve it. */
async function approveGeneratedArtifact(page: Page, label: string) {
  await openSection(page, 'Artifacts')
  const card = page.getByRole('region', { name: new RegExp(`^${label}$`, 'i') })
  await expect(card.getByText('Draft', { exact: true })).toBeVisible({ timeout: AGENT_TIMEOUT })
  await card.getByRole('link', { name: `Open ${label.toLowerCase()}` }).click()
  await expect(page.getByRole('heading', { level: 2, name: label })).toBeVisible()
  const version = Number((await page.getByLabel('Version', { exact: true }).inputValue()) || '1')
  await approveShownDraft(page, label.toLowerCase(), version)
}

function pendingGates(page: Page): Locator {
  return page.getByRole('region', { name: 'Pending gates' })
}

async function approvePendingGate(page: Page, gateLabel: string) {
  const item = pendingGates(page).getByRole('listitem', { name: gateLabel })
  await expect(item).toBeVisible({ timeout: AGENT_TIMEOUT })
  await item.getByRole('button', { name: 'Approve', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `Approve ${gateLabel.toLowerCase()}?` })
  await dialog.getByLabel('Comment (optional)').fill(`E2E: ${gateLabel} looks good`)
  const decided = page.waitForResponse((r) => /\/gates\/[^/]+\/approve$/.test(r.url()) && r.request().method() === 'POST')
  await dialog.getByRole('button', { name: 'Approve', exact: true }).click()
  expect((await decided).status()).toBe(200)
  // SPEC 7.1: the decision's result is a toast.
  await expect(page.getByText(`${gateLabel} approved.`)).toBeVisible()
}

/** FR-54: the project's current stage in the header's stage stepper. */
function currentPhase(page: Page): Locator {
  return page.getByRole('navigation', { name: 'Lifecycle stages' }).locator('[data-stage-state="current"]')
}

test('NFR-TEST, FR-5–FR-66: an idea becomes a tagged release, a new version starts, and the history and notifications show it', async ({
  page,
  browser,
}) => {
  test.setTimeout(10 * 60_000)
  const projectName = `E2E Ticket tracker ${String(Date.now())}`

  // A fresh owner. Registered through the API: the register page is covered by auth.spec.ts, and every UI register
  // adds a rate-limited (5/min per IP) form submission this journey doesn't need.
  const owner = await registerViaApi(page.context(), 'owner', 'Lifecycle Owner')
  await page.goto('/projects')
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()

  // FR-5, FR-66: the five-step wizard, strict approval mode, Local Git, the default initial version 0.1.0.
  await page.getByRole('link', { name: 'New project' }).first().click()
  await expect(page.getByRole('heading', { level: 1, name: 'New project' })).toBeVisible()
  await page.getByLabel('Project name').fill(projectName)
  await page.getByLabel('Your idea').fill('A ticket tracker API for small support teams: create, assign and close tickets.')
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 2 of 5: Configuration' })).toBeVisible()
  await page.getByRole('radio', { name: /^Strict/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByRole('radio', { name: /^Local repository/ }).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 4 of 5: Version' })).toBeVisible()
  await expect(page.getByLabel('Initial version')).toHaveValue('0.1.0')
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { name: 'Step 5 of 5: Review & create' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Configuration' }).getByText('Strict')).toBeVisible()
  await page.getByRole('button', { name: 'Create project' }).click()

  await expect(page.getByRole('heading', { level: 1, name: projectName })).toBeVisible()
  const projectId = /\/projects\/([^/?#]+)/.exec(page.url())?.[1] ?? ''
  expect(projectId).not.toBe('')
  await expect(currentPhase(page)).toContainText('Refinement')

  // FR-46 watcher: a second user in their own browser context, added as a Viewer.
  const watcherContext = await browser.newContext()
  const watcher = await watcherContext.newPage()
  try {
    await registerViaApi(watcherContext, 'watcher', 'Live Watcher')
    const watcherEmail = (await (await watcherContext.request.get('/api/auth/me')).json()) as { email: string }
    await addMember(page.context(), projectId, watcherEmail.email, 'viewer')

    // FR-27: first refinement round, answer one clarifying question, run another round → draft v2.
    await page.getByRole('link', { name: 'Refine the idea' }).click()
    await page.getByRole('button', { name: 'Start refinement' }).click()
    await expect(page.getByRole('heading', { name: 'Current draft: v1' })).toBeVisible({ timeout: AGENT_TIMEOUT })
    const answer = 'Support agents who sign in with their work email.'
    await page.getByLabel(/Who are the main users of/).fill(answer)
    await page.getByRole('button', { name: 'Run another round' }).click()
    await expect(page.getByRole('heading', { name: 'Current draft: v2' })).toBeVisible({ timeout: AGENT_TIMEOUT })
    await expect(page.getByRole('region', { name: 'Current draft: v2' }).getByText(answer)).toBeVisible()

    // FR-17: approve project request v2 from its artifact page.
    await page.getByRole('link', { name: 'Open in Artifacts' }).click()
    await expect(page.getByRole('heading', { level: 2, name: 'Project request' })).toBeVisible()
    await approveShownDraft(page, 'project request', 2)

    // FR-12: strict mode has no gate into Specification.
    await openSection(page, 'Overview')
    await page.getByRole('button', { name: 'Advance to Specification' }).click()
    await expect(page.getByText('The project moved to Specification (checkpoint 2).')).toBeVisible()

    // FR-28: the Spec Generator's draft arrives live; approve it.
    await approveGeneratedArtifact(page, 'Technical specification')

    // FR-12: advancing to Planning creates a spec_approval gate instead.
    await openSection(page, 'Overview')
    await watcher.goto(`/projects/${projectId}`)
    await expect(watcher.getByRole('heading', { level: 1, name: projectName })).toBeVisible()
    await expect(pendingGates(watcher).getByText('No gates are waiting for a decision')).toBeVisible()
    // The viewer sees no owner/writer controls (§2, §7).
    await expect(watcher.getByRole('button', { name: /^Advance to/ })).toHaveCount(0)

    await page.getByRole('button', { name: 'Advance to Planning' }).click()
    await expect(page.getByText('Approval required')).toBeVisible()
    await expect(currentPhase(page)).toContainText('Specification')

    // FR-46: the watcher's page shows the new gate without reloading, within 2 s.
    await expect(pendingGates(watcher).getByRole('listitem', { name: 'Specification approval' })).toBeVisible({
      timeout: LIVE_BOUND_MS,
    })
    await expect(watcher.getByText('Only project Owners can decide gates.')).toBeVisible()
    await expect(pendingGates(watcher).getByRole('button', { name: 'Approve' })).toHaveCount(0)

    // FR-13: the Owner approves; FR-46: the watcher sees the decision and the phase change within 2 s.
    await approvePendingGate(page, 'Specification approval')
    await expect(pendingGates(watcher).getByText('No gates are waiting for a decision')).toBeVisible({ timeout: LIVE_BOUND_MS })
    await expect(currentPhase(watcher)).toContainText('Planning', { timeout: LIVE_BOUND_MS })
    await expect(currentPhase(page)).toContainText('Planning')

    // FR-29: plan draft → approve; FR-12: strict mode gates execution with plan_approval.
    await approveGeneratedArtifact(page, 'Implementation plan')
    await openSection(page, 'Overview')
    await page.getByRole('button', { name: 'Advance to Execution' }).click()
    await expect(page.getByText('Approval required')).toBeVisible()
    await approvePendingGate(page, 'Plan approval')
    await expect(currentPhase(page)).toContainText('Execution')

    // FR-31, FR-34, FR-46: start execution and watch every step complete on the same page (no reload).
    await openSection(page, 'Execution')
    await page.evaluate(() => {
      ;(window as unknown as { __e2eNoReload: boolean }).__e2eNoReload = true
    })
    await page.getByRole('button', { name: 'Start execution' }).click()
    const steps = page.getByRole('list', { name: 'Execution steps' })
    await expect(page.getByText(/^(\d+) of \1 steps completed$/)).toBeVisible({ timeout: 2 * AGENT_TIMEOUT })
    const stepCount = await steps.getByRole('listitem').count()
    expect(stepCount).toBeGreaterThan(0)
    await expect(steps.getByText('Completed', { exact: true })).toHaveCount(stepCount)
    await expect(steps.getByText('Commit').first()).toBeVisible()
    expect(await page.evaluate(() => (window as unknown as { __e2eNoReload?: boolean }).__e2eNoReload)).toBe(true)

    // FR-35, FR-36: the Local "PR" is a recorded branch comparison with the step commits and changed files.
    const comparison = page.getByRole('region', { name: 'Branch comparison' })
    await expect(comparison).toBeVisible({ timeout: AGENT_TIMEOUT })
    await expect(comparison.getByRole('heading', { name: /^Commits \((\d+)\)$/ })).toBeVisible()
    await expect(comparison.getByRole('list', { name: 'Commits' }).getByRole('listitem')).not.toHaveCount(0)
    await expect(comparison.getByRole('list', { name: 'Files changed' })).toContainText('src/server.js')

    // FR-38–FR-40: review cycle 1 (Fake: 0.86 < 0.90) needs another cycle with an optimisation plan.
    await openSection(page, 'Review')
    await expect(page.getByRole('heading', { name: /^Latest report: cycle 1,/ })).toBeVisible({ timeout: AGENT_TIMEOUT })
    await expect(page.getByText('Another cycle needed').first()).toBeVisible()
    await page.getByRole('link', { name: 'Open the optimisation plan' }).click()
    await expect(page.getByRole('heading', { level: 2, name: 'Optimisation plan' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Approve', exact: true })).toBeVisible({ timeout: AGENT_TIMEOUT })
    await approveShownDraft(page, 'optimisation plan', 1)

    // FR-40, FR-41: the optimisation step runs, review cycle 2 (0.93) is release eligible; the trend has two points.
    await openSection(page, 'Review')
    await expect(page.getByRole('heading', { name: /^Latest report: cycle 2,/ })).toBeVisible({ timeout: 3 * AGENT_TIMEOUT })
    await expect(page.getByText('Release eligible', { exact: true }).first()).toBeVisible()
    await expect(page.getByRole('region', { name: 'Compliance trend' })).toBeVisible()

    // FR-39, FR-42: the pre-release gate; approving it merges and tags.
    await openSection(page, 'Overview')
    await approvePendingGate(page, 'Release approval')
    const release = page.getByRole('region', { name: 'Release' })
    await expect(release.getByText('Released', { exact: true })).toBeVisible({ timeout: AGENT_TIMEOUT })
    // FR-64, FR-66: the first release tags the initial version.
    await expect(release.getByText('v0.1.0', { exact: true })).toBeVisible()
    const tag = 'v0.1.0'
    await expect(page.getByTestId('version-badges').first()).toContainText('Released 0.1.0')

    // FR-63, D-V2-3: a released version is closed; rollback says so and the way forward is a new version.
    await openSection(page, 'Settings')
    await page.getByRole('navigation', { name: 'Settings sections' }).getByRole('link', { name: 'Danger zone' }).click()
    const rollback = page.getByRole('button', { name: 'Roll back…' })
    await expect(rollback).toHaveAttribute('aria-disabled', 'true')
    await expect(rollback).toHaveAccessibleDescription(/Version 0\.1\.0 is released and closed/)

    // FR-60, FR-62: start 0.2.0 from the overview; the project goes back to Refinement with both badges.
    await openSection(page, 'Overview')
    await page.getByRole('region', { name: '0.1.0 is released' }).getByRole('button', { name: 'Start new version' }).click()
    const start = page.getByRole('dialog', { name: 'Start a new version' })
    await expect(start.getByLabel('New version')).toHaveValue('0.2.0')
    await start.getByLabel('What should this version add or fix?').fill('E2E: add ticket priorities')
    await start.getByRole('button', { name: 'Start development' }).click()
    await expect(start).toBeHidden()
    await expect(currentPhase(page)).toContainText('Refinement')
    const badges = page.getByRole('heading', { level: 1, name: projectName }).locator('xpath=ancestor::header[1]').getByTestId('version-badges')
    await expect(badges).toContainText('Released 0.1.0')
    await expect(badges).toContainText('In development 0.2.0')

    // FR-62: the released 0.1.0 can be opened read-only from the version switcher.
    await page.getByRole('button', { name: /^Release version: 0\.2\.0/ }).click()
    await page.getByRole('menuitem', { name: /^0\.1\.0/ }).click()
    await expect(page.getByTestId('read-only-banner')).toHaveText(/Viewing 0\.1\.0 \(released\)\. Read-only/)
    await page.getByRole('button', { name: 'Back to the current version' }).click()
    await expect(page.getByTestId('read-only-banner')).toBeHidden()

    // FR-50: the activity feed tells the story, newest first.
    const activity = page.getByRole('list', { name: 'Activity' })
    await expect(activity.getByText('Version 0.2.0 started from v0.1.0')).toBeVisible()
    await page.getByLabel('Show', { exact: true }).selectOption('release')
    await expect(activity.getByText(`Released: merged and tagged ${tag}`)).toBeVisible()
    await page.getByLabel('Show', { exact: true }).selectOption('gate')
    await expect(activity.getByText('Specification approval gate approved')).toBeVisible()
    await expect(activity.getByText('Release approval gate approved')).toBeVisible()

    // FR-47: the bell shows unread notifications; marking one read and then all read updates the count.
    // Notifications are delivered by background jobs, so first wait (through the API) until this journey's expected set is
    // in: the agents' drafts, both review cycles and the release (gate_required isn't awaited: the
    // worker drops it when the gate is decided before delivery), and the server's unread count has stopped
    // moving. Only then are the before/after counts comparable.
    interface NotificationPage { items: { id: string; type: string; title: string; readAt: string | null }[]; unreadCount: number }
    const serverUnread = async () =>
      (await apiJson<{ unreadCount: number }>(page.context(), 'GET', '/api/me/notifications/unread-count')).unreadCount
    const expectedTypes: Record<string, number> = { artifact_ready: 1, review_completed: 2, release_completed: 1 }
    const recentCounts: number[] = []
    await expect
      .poll(
        async () => {
          const list = await apiJson<NotificationPage>(page.context(), 'GET', '/api/me/notifications?limit=100')
          const missing = Object.entries(expectedTypes)
            .filter(([type, n]) => list.items.filter((i) => i.type === type).length < n)
            .map(([type]) => type)
          recentCounts.push(list.unreadCount)
          const stable = recentCounts.length >= 3 && recentCounts.slice(-3).every((c) => c === list.unreadCount)
          return missing.length > 0 ? `missing ${missing.join(', ')}` : stable ? 'settled' : `unread ${String(list.unreadCount)} still moving`
        },
        { timeout: AGENT_TIMEOUT, intervals: [1_000] },
      )
      .toBe('settled')
    const unreadBefore = await serverUnread()
    expect(unreadBefore).toBeGreaterThan(0)

    // The bell is polled (30 s); a reload makes it fetch the settled count now.
    await page.reload()
    const bell = page.getByRole('button', { name: /^Notifications, \d+ unread$/ })
    await expect(bell).toHaveAccessibleName(`Notifications, ${String(unreadBefore)} unread`)
    await bell.click()
    await expect(page.getByRole('list', { name: 'Latest notifications' })).toContainText(`released ${tag}`)
    await page.getByRole('link', { name: 'View all notifications' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Notifications' })).toBeVisible()
    const notifications = page.getByRole('list', { name: 'Notifications', exact: true })
    for (const label of ['Artifact ready', 'Review completed', 'Release completed']) {
      await expect(notifications.getByText(new RegExp(`^${label} · `)).first()).toBeVisible()
    }

    // Mark the newest unread one read: it loses its Unread badge and the count drops by exactly one (bell and server).
    const markOne = page.getByRole('button', { name: /^Mark ".*" as read$/ }).first()
    const markedTitle = /^Mark "(.*)" as read$/.exec((await markOne.getAttribute('aria-label')) ?? '')?.[1] ?? ''
    expect(markedTitle).not.toBe('')
    const marked = notifications.getByRole('listitem', { name: markedTitle, exact: true }).first()
    await expect(marked.getByText('Unread', { exact: true })).toBeVisible()
    await markOne.click()
    await expect(marked.getByText('Unread', { exact: true })).toHaveCount(0)
    await expect(marked.getByRole('button', { name: /^Mark ".*" as read$/ })).toHaveCount(0)
    expect(await serverUnread()).toBe(unreadBefore - 1)
    await expect(bell).toHaveAccessibleName(`Notifications, ${String(unreadBefore - 1)} unread`)

    await page.getByRole('button', { name: 'Mark all as read' }).click()
    await expect(bell).toHaveAccessibleName('Notifications, 0 unread')
    expect(await serverUnread()).toBe(0)
    expect(owner.email).toContain('@example.com')
  } finally {
    await watcherContext.close()
  }
})
