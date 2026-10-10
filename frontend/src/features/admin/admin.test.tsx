import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages } from '../../lib/errors'
import { adminDb, seedAdminEvent, seedAdminRun, seedDeadLetter } from '../../mocks/handlers/admin'
import { authDb, seedUser, signInAs } from '../../mocks/handlers/auth'
import { problem } from '../../mocks/handlers/problem'
import { seedProject } from '../../mocks/handlers/projects'
import { seedRun } from '../../mocks/handlers/runs'
import { server } from '../../mocks/server'
import { currentHref, renderRoute } from '../../test/render'
import { adminEventsSearchSchema, endOfDay, isCalendarDate, startOfDay } from './admin-search'

function signInAdmin() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'admin' })
}

/** Renders `path` and waits until the admin list has loaded. */
async function open(path: string, heading: string) {
  const utils = renderRoute(path)
  await screen.findByRole('heading', { level: 2, name: heading })
  await waitFor(() => {
    expect(screen.queryByText(/^Loading (events|runs|dead letters|users)…$/)).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

function table(name: RegExp) {
  return screen.getByRole('table', { name })
}

/** Body rows (the header row is excluded). */
function rows(name: RegExp) {
  return within(table(name)).getAllByRole('row').slice(1)
}

const lastRequest = () => adminDb.requests.at(-1)?.searchParams

describe('Admin area access (FR-3, FR-51)', () => {
  it('FR3_ forbidden: non-admins get the forbidden view on every admin route and no admin request is made', async () => {
    seedUser({ systemRole: 'admin', email: 'root@example.com' })
    signInAs({ systemRole: 'user', email: 'bob@example.com', name: 'Bob' })

    for (const path of ['/admin', '/admin/events', '/admin/runs', '/admin/dead-letters', '/admin/users']) {
      const { unmount } = renderRoute(path)
      expect(await screen.findByRole('heading', { level: 1, name: "You don't have access to this page" })).toBeInTheDocument()
      expect(screen.queryByRole('navigation', { name: 'Admin sections' })).not.toBeInTheDocument()
      unmount()
    }
    expect(adminDb.requests).toHaveLength(0)
  })

  it('FR51_ landing: /admin links to the four sections, and the section nav marks the current one', async () => {
    signInAdmin()
    renderRoute('/admin')
    const user = userEvent.setup()

    expect(await screen.findByRole('heading', { level: 1, name: 'Admin' })).toBeInTheDocument()
    const sections = screen.getByRole('navigation', { name: 'Admin sections' })
    expect(within(sections).getAllByRole('link').map((l) => l.textContent)).toEqual([
      'Events',
      'Agent runs',
      'Dead letters',
      'Users',
    ])
    expect(screen.getByRole('heading', { level: 3, name: 'Dead letters' })).toBeInTheDocument()

    await user.click(within(sections).getByRole('link', { name: 'Users' }))
    expect(await screen.findByRole('heading', { level: 2, name: 'Users' })).toBeInTheDocument()
    expect(within(sections).getByRole('link', { name: 'Users' })).toHaveAttribute('aria-current', 'page')
  })
})

describe('Admin events (FR-51)', () => {
  it('FR51_ events: lists events of every project, newest first, with project, category, outcome and actor', async () => {
    signInAdmin()
    const a = seedProject({ name: 'Inventory tracker' })
    seedAdminEvent({ projectId: a.id, projectName: a.name, summary: 'Technical specification v2 approved' })
    seedAdminEvent({
      projectId: a.id,
      projectName: a.name,
      type: 'RUN_FAILED',
      category: 'run',
      outcome: 'failure',
      summary: 'Spec generator run failed',
      actor: { id: null, name: 'aide-system', isSystem: true },
    })
    seedAdminEvent({ type: 'PROJECT_DELETED', category: 'project', summary: 'Project Old app deleted', actor: { id: 'x', name: 'Ada Lovelace', isSystem: false } })
    await open('/admin/events', 'Events')

    expect(screen.getByText('Showing 1–3 of 3 events')).toBeInTheDocument()
    const t = table(/Events of every project, newest first/)
    expect(within(t).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Event',
      'Project',
      'Category',
      'Outcome',
      'Actor',
      'When',
    ])
    const row = (summary: string) => {
      const found = rows(/Events of every project/).find((r) => within(r).queryByText(summary))
      if (!found) {
        throw new Error(`No row for ${summary}`)
      }
      return found
    }
    expect(rows(/Events of every project/).map((r) => within(r).getByRole('rowheader').firstChild?.textContent)).toEqual([
      'Project Old app deleted',
      'Spec generator run failed',
      'Technical specification v2 approved',
    ])
    const deleted = row('Project Old app deleted')
    const failed = row('Spec generator run failed')
    const approved = row('Technical specification v2 approved')
    expect(within(deleted).getByRole('rowheader')).toHaveTextContent(/^Project Old app deletedPROJECT_DELETED/)
    expect(within(deleted).getByText('—')).toBeInTheDocument()
    expect(within(failed).getByText('Failure')).toBeInTheDocument()
    expect(within(failed).getByText('aide-system (system)')).toBeInTheDocument()
    expect(within(failed).getByRole('link', { name: 'Inventory tracker' })).toHaveAttribute('href', `/projects/${a.id}`)
    expect(within(approved).getByText('Artifacts')).toBeInTheDocument()
  })

  it('FR51_ filters: project, type, category, outcome and dates are applied to the URL and the API query', async () => {
    signInAdmin()
    const a = seedProject({ name: 'Inventory tracker' })
    const b = seedProject({ name: 'Billing service' })
    seedAdminEvent({ projectId: a.id, projectName: a.name, type: 'RUN_FAILED', category: 'run', outcome: 'failure', summary: 'Run failed in A' })
    seedAdminEvent({ projectId: b.id, projectName: b.name, type: 'RUN_FAILED', category: 'run', outcome: 'failure', summary: 'Run failed in B' })
    seedAdminEvent({ projectId: a.id, projectName: a.name, summary: 'Approved in A' })
    const { user, router } = await open('/admin/events', 'Events')

    await user.selectOptions(await screen.findByRole('combobox', { name: 'Project' }), 'Inventory tracker')
    await user.type(screen.getByRole('textbox', { name: 'Event type' }), 'run_failed')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Category' }), 'Agent runs')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Outcome' }), 'Failure')
    await user.type(screen.getByLabelText('From (inclusive)'), '2026-09-01')
    await user.type(screen.getByLabelText('To (inclusive)'), '2026-12-31')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))

    expect(await screen.findByText('Showing 1–1 of 1 event')).toBeInTheDocument()
    expect(screen.getByText('Run failed in A')).toBeInTheDocument()
    const href = currentHref(router)
    expect(href).toContain(`projectId=${a.id}`)
    expect(href).toContain('type=RUN_FAILED')
    expect(href).toContain('category=run')
    expect(href).toContain('outcome=failure')
    expect(href).toContain('from=2026-09-01')
    expect(href).toContain('to=2026-12-31')
    const q = lastRequest()
    expect(q?.get('type')).toBe('RUN_FAILED')
    expect(q?.get('projectId')).toBe(a.id)
    expect(q?.get('from')).toBe(new Date('2026-09-01T00:00:00').toISOString())
    expect(q?.get('to')).toBe(new Date('2026-12-31T23:59:59.999').toISOString())

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(await screen.findByText('Showing 1–3 of 3 events')).toBeInTheDocument()
    expect(currentHref(router)).toBe('/admin/events')
    expect(screen.getByRole('textbox', { name: 'Event type' })).toHaveValue('')
  })

  it('FR51_ filters: an invalid type or an inverted date range is explained and not sent', async () => {
    signInAdmin()
    seedAdminEvent()
    const { user, router } = await open('/admin/events', 'Events')
    const sent = adminDb.requests.length

    await user.type(screen.getByRole('textbox', { name: 'Event type' }), 'run failed!')
    await user.type(screen.getByLabelText('From (inclusive)'), '2026-10-02')
    await user.type(screen.getByLabelText('To (inclusive)'), '2026-10-01')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))

    expect(await screen.findByText(/Enter an event type such as RUN_FAILED/)).toBeInTheDocument()
    expect(screen.getByText('The end date must be on or after the start date.')).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Event type' })).toHaveAttribute('aria-invalid', 'true')
    expect(currentHref(router)).toBe('/admin/events')
    expect(adminDb.requests).toHaveLength(sent)
  })

  it('FR51_ paging: filters and page come from the URL; invalid params are dropped', async () => {
    signInAdmin()
    for (let i = 0; i < 25; i += 1) {
      seedAdminEvent({ type: 'RUN_FAILED', category: 'run', outcome: 'failure', summary: `Failure ${String(i + 1)}` })
    }
    seedAdminEvent({ summary: 'An OK event' })
    const { user, router } = await open('/admin/events?outcome=failure&category=nope&page=2', 'Events')

    expect(screen.getByText('Showing 21–25 of 25 events')).toBeInTheDocument()
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Outcome' })).toHaveValue('failure')
    expect(lastRequest()?.get('category')).toBeNull()

    await user.click(screen.getByRole('button', { name: 'Previous page' }))
    expect(await screen.findByText('Showing 1–20 of 25 events')).toBeInTheDocument()
    expect(currentHref(router)).toBe('/admin/events?outcome=failure')
  })

  it('FR51_ paging: impossible calendar dates in the URL (2026-13-45, 2026-02-30) are ignored, not sent or crashing', async () => {
    signInAdmin()
    seedAdminEvent({ summary: 'An OK event' })
    await open('/admin/events?from=2026-13-45&to=2026-02-30', 'Events')

    expect(screen.getByText('Showing 1–1 of 1 event')).toBeInTheDocument()
    expect(screen.getByLabelText('From (inclusive)')).toHaveValue('')
    expect(screen.getByLabelText('To (inclusive)')).toHaveValue('')
    expect(lastRequest()?.get('from')).toBeNull()
    expect(lastRequest()?.get('to')).toBeNull()

    expect(adminEventsSearchSchema.parse({ from: '2026-02-29', to: '2028-02-29' })).toEqual({ to: '2028-02-29' })
    expect(isCalendarDate('2024-02-29')).toBe(true)
    expect(startOfDay('2026-13-45')).toBeUndefined()
    expect(endOfDay('2026-02-30')).toBeUndefined()
  })

  it('FR51_ states: empty, filtered empty and error', async () => {
    signInAdmin()
    const { user } = await open('/admin/events', 'Events')
    expect(screen.getByText('No events yet')).toBeInTheDocument()

    await user.selectOptions(screen.getByRole('combobox', { name: 'Outcome' }), 'Failure')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))
    expect(await screen.findByText('No events match these filters')).toBeInTheDocument()

    server.use(http.get('*/api/admin/events', () => problem(403, 'FORBIDDEN', 'Only administrators can do that.')))
    // The filtered empty state offers its own Clear filters button.
    const filterForm = screen.getByRole('search', { name: 'Filter events' })
    await user.click(within(filterForm).getByRole('button', { name: 'Clear filters' }))
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("The events couldn't load")
    expect(alert).toHaveTextContent("You don't have permission to do that.")
  })
})

describe('Admin agent runs (FR-51, FR-23)', () => {
  it('FR51_ runs: lists runs of every project, filters by status and provider, and opens a run in the drawer', async () => {
    signInAdmin()
    const a = seedProject({ name: 'Inventory tracker' })
    const b = seedProject({ name: 'Billing service' })
    const detail = seedRun(a, { agent: 'spec_generator' })
    seedAdminRun({ ...detail, projectId: a.id, projectName: a.name })
    seedAdminRun({ projectId: b.id, projectName: b.name, agent: 'plan_generator', phase: 'planning', status: 'failed', provider: 'openai', model: 'gpt-5', error: 'PROVIDER_ERROR: 500' })
    const { user, router } = await open('/admin/runs', 'Agent runs')

    expect(screen.getByText('Showing 1–2 of 2 runs')).toBeInTheDocument()
    const t = table(/Agent runs of every project/)
    expect(within(t).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Agent',
      'Project',
      'Model',
      'Tokens in / out',
      'Est. cost',
      'Duration',
      'Status',
      'Started',
    ])

    await user.selectOptions(screen.getByRole('combobox', { name: 'Status' }), 'Failed')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Provider' }), 'OpenAI')
    await user.type(screen.getByRole('textbox', { name: 'Model' }), 'gpt-5')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))
    expect(await screen.findByText('Showing 1–1 of 1 run')).toBeInTheDocument()
    expect(within(table(/Agent runs of every project/)).getByRole('link', { name: 'Billing service' })).toBeInTheDocument()
    expect(currentHref(router)).toBe('/admin/runs?status=failed&provider=openai&model=gpt-5')

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    await user.click(await screen.findByRole('button', { name: /^Spec generator run in Inventory tracker/ }))
    const drawer = await screen.findByRole('dialog', { name: 'Spec generator run · Inventory tracker' })
    expect(within(drawer).getByRole('button', { name: 'Close' })).toHaveFocus()
    expect(within(drawer).getByRole('link', { name: 'Open in the project’s runs' })).toHaveAttribute(
      'href',
      `/projects/${a.id}/runs?run=${detail.id}`,
    )
    expect(await within(drawer).findByLabelText('Run output')).toHaveTextContent(/Inventory tracker: technical specification/)

    await user.keyboard('{Escape}')
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(currentHref(router)).toBe('/admin/runs')
  })
})

describe('Admin dead letters (FR-51)', () => {
  it('FR51_ dead letters: shows each dead letter with its last error, and Retry (confirmed) re-queues the job', async () => {
    signInAdmin()
    const a = seedProject({ name: 'Inventory tracker' })
    const dead = seedDeadLetter({ projectId: a.id, projectName: a.name, type: 'spec_generation', lastError: 'PROVIDER_ERROR: overloaded' })
    seedDeadLetter({ type: 'notification', lastError: 'SMTP 550 mailbox unavailable' })
    const { user } = await open('/admin/dead-letters', 'Dead letters')

    const t = table(/Dead-lettered jobs/)
    expect(within(t).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Job',
      'Project',
      'Status',
      'Attempts',
      'Last error',
      'Dead-lettered',
      'Action',
    ])
    expect(within(t).getByText('PROVIDER_ERROR: overloaded')).toBeInTheDocument()
    expect(within(t).getByText('SMTP 550 mailbox unavailable')).toBeInTheDocument()

    const retryButton = screen.getByRole('button', { name: `Retry Specification generation job ${dead.id}` })
    await user.click(retryButton)
    const dialog = screen.getByRole('alertdialog', { name: 'Retry this dead letter?' })
    expect(dialog).toHaveTextContent('A new specification generation job for Inventory tracker is queued with the same payload')
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(adminDb.deadLetters[0]?.status).toBe('dead')

    await user.click(retryButton)
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Retry job' }))

    expect(await screen.findByText(/The specification generation job was queued again as job/)).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(adminDb.deadLetters.find((d) => d.id === dead.id)?.status).toBe('retried')
    expect(adminDb.events.map((e) => e.type)).toContain('DEAD_LETTER_RETRIED')
    await waitFor(() => {
      expect(rows(/Dead-lettered jobs/)).toHaveLength(1)
    })
    expect(screen.getByText('Showing 1–1 of 1 dead letter')).toBeInTheDocument()
  })

  it('FR51_ retry 409: an execution dead letter whose project already has an execution job explains the conflict', async () => {
    signInAdmin()
    const a = seedProject({ name: 'Inventory tracker' })
    const dead = seedDeadLetter({ projectId: a.id, projectName: a.name, type: 'execution' })
    adminDb.activeExecutionProjects.push(a.id)
    const { user } = await open('/admin/dead-letters', 'Dead letters')

    await user.click(screen.getByRole('button', { name: `Retry Execution job ${dead.id}` }))
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Retry job' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The retry didn’t go through')
    expect(alert).toHaveTextContent(contextualErrorMessages.retryDeadLetter.CONFLICT)
    expect(adminDb.deadLetters[0]?.status).toBe('dead')
  })

  it('FR51_ retry 404: a dead letter someone else already retried says so and the list refreshes', async () => {
    signInAdmin()
    const dead = seedDeadLetter({ type: 'review' })
    const { user } = await open('/admin/dead-letters', 'Dead letters')
    dead.status = 'retried'

    await user.click(screen.getByRole('button', { name: `Retry Review job ${dead.id}` }))
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Retry job' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(contextualErrorMessages.retryDeadLetter.NOT_FOUND)
    expect(await screen.findByText('No dead letters')).toBeInTheDocument()
  })

  it('FR51_ filters: job type, project and "include retried" go to the URL', async () => {
    signInAdmin()
    seedDeadLetter({ type: 'notification', status: 'retried' })
    seedDeadLetter({ type: 'review' })
    const { user, router } = await open('/admin/dead-letters', 'Dead letters')
    expect(screen.getByText('Showing 1–1 of 1 dead letter')).toBeInTheDocument()

    await user.click(screen.getByRole('checkbox', { name: 'Include dead letters already retried' }))
    await user.selectOptions(screen.getByRole('combobox', { name: 'Job type' }), 'Notification')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))

    expect(await screen.findByText('Already retried')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Retry / })).not.toBeInTheDocument()
    expect(currentHref(router)).toBe('/admin/dead-letters?type=notification&includeRetried=true')
    expect(lastRequest()?.get('includeRetried')).toBe('true')
  })
})

describe('Admin users (FR-3)', () => {
  it('FR3_ role change: an Admin makes another user an Admin after confirming', async () => {
    signInAdmin()
    const bob = seedUser({ email: 'bob@example.com', name: 'Bob Builder', systemRole: 'user' })
    adminDb.userCreatedAt[bob.id] = '2026-09-05T10:00:00Z'
    const { user } = await open('/admin/users', 'Users')

    const t = table(/Users, newest first/)
    expect(within(t).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Name',
      'Email',
      'System role',
      'GitHub',
      'Joined',
      'Action',
    ])
    expect(rows(/Users, newest first/).map((r) => within(r).getByRole('rowheader').textContent)).toEqual([
      'Bob Builder',
      'Ada Lovelace(you)',
    ])

    await user.click(screen.getByRole('button', { name: 'Make Bob Builder an Admin' }))
    const dialog = screen.getByRole('alertdialog', { name: 'Make Bob Builder an Admin?' })
    await user.click(within(dialog).getByRole('button', { name: 'Make Admin' }))

    expect(await screen.findByText('Bob Builder is now an Admin.')).toBeInTheDocument()
    expect(authDb.users.find((u) => u.id === bob.id)?.systemRole).toBe('admin')
    expect(await screen.findByRole('button', { name: 'Make Bob Builder a User' })).toBeInTheDocument()
  })

  it('FR3_ last admin: demoting yourself as the only Admin is refused and the reason shown', async () => {
    signInAdmin()
    seedUser({ email: 'bob@example.com', name: 'Bob Builder', systemRole: 'user' })
    const { user } = await open('/admin/users', 'Users')

    await user.click(screen.getByRole('button', { name: 'Make Ada Lovelace a User' }))
    const dialog = screen.getByRole('alertdialog', { name: 'Make Ada Lovelace a User?' })
    expect(dialog).toHaveTextContent('You will lose access to the admin area')
    await user.click(within(dialog).getByRole('button', { name: 'Make User' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(contextualErrorMessages.changeSystemRole.CONFLICT)
    expect(authDb.users[0]?.systemRole).toBe('admin')
    expect(screen.getByRole('heading', { level: 1, name: 'Admin' })).toBeInTheDocument()
  })

  it('FR3_ self demotion: when another Admin exists, demoting yourself closes the admin area', async () => {
    seedUser({ email: 'root@example.com', name: 'Root', systemRole: 'admin' })
    signInAdmin()
    const { user } = await open('/admin/users', 'Users')

    await user.click(screen.getByRole('button', { name: 'Make Ada Lovelace a User' }))
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Make User' }))

    expect(await screen.findByRole('heading', { level: 1, name: "You don't have access to this page" })).toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Main' })).queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument()
  })

  it('FR3_ search: searching by name or email goes to the URL and filters the list', async () => {
    signInAdmin()
    seedUser({ email: 'bob@example.com', name: 'Bob Builder', systemRole: 'user' })
    const { user, router } = await open('/admin/users', 'Users')

    await user.type(screen.getByRole('searchbox', { name: 'Name or email' }), 'bob@')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))

    expect(await screen.findByText('Showing 1–1 of 1 user')).toBeInTheDocument()
    expect(currentHref(router)).toBe('/admin/users?search=bob%40')
    expect(screen.getByRole('search', { name: 'Search users' })).toBeInTheDocument()
  })
})
