import { act, screen, waitFor, within } from '@testing-library/react'
import { focusManager } from '@tanstack/react-query'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { GATE_INBOX_PAGE_SIZE, GATE_INBOX_REFRESH_MS } from '../../engine/api/gate-hooks'
import { engineKeys } from '../../engine/api/keys'
import { defaultGateEffects } from '../../engine/components/gate-decision'
import { contextualErrorMessages, errorMessages } from '../../lib/errors'
import type { ProjectRole } from '../../lib/permissions'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedFixtureRun, seedSdlcRun } from '../../mocks/handlers/pipeline-seeds'
import { addPipelineGate, pipelinesDb } from '../../mocks/handlers/pipeline-store'
import { problem } from '../../mocks/handlers/problem'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { axeViolations, setTheme, THEMES } from '../../test/axe'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import { approveEffect, rejectEffect } from './gate-effects'
import { CANNOT_DECIDE_NOTE } from './gate-inbox-page'

/**
 * Two projects: "Inventory tracker" (the user is `role`) with an SDLC run waiting on its spec approval, a fixture run
 * whose report approval is overdue, and an escalation; and "Secret project", which the user can't see.
 */
function seedInbox(role: ProjectRole = 'owner', { sdlc: withSdlc = true }: { sdlc?: boolean } = {}) {
  const grace = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  const members = [{ userId: me.id, role }, ...(role === 'owner' ? [] : [{ userId: grace.id, role: 'owner' as const }])]
  const project = seedProject({ name: 'Inventory tracker', members })
  const hidden = seedProject({ name: 'Secret project', members: [{ userId: grace.id, role: 'owner' }] })
  const sdlc = withSdlc ? seedSdlcRun(project) : undefined
  const fixture = seedFixtureRun(project)
  const report = fixture.gates[0]
  if (report) {
    report.overdue = true
  }
  const escalation = sdlc ? addPipelineGate(sdlc, { type: 'escalation', title: 'Escalation' }) : undefined
  seedSdlcRun(hidden)
  return { me, project, sdlc, fixture, escalation }
}

async function openInbox() {
  const utils = await renderRoute('/gates')
  await screen.findByRole('heading', { level: 1, name: 'Gates' })
  return { ...utils, user: userEvent.setup() }
}

const row = async (title: string) =>
  (await screen.findByRole('heading', { level: 2, name: title })).closest('li') as HTMLElement

/** The row's buttons once its definition has loaded (the action area shows a placeholder until then). */
const button = async (title: string, name: 'Approve' | 'Reject') => within(await row(title)).findByRole('button', { name })

describe('Gate inbox (FR-307)', () => {
  it('FR307_inbox_lists_only_visible_projects: rows show project, pipeline, gate, artifact link, age and overdue', async () => {
    const { project } = seedInbox()
    await openInbox()

    const list = await screen.findByRole('list', { name: 'Pending gates' })
    expect(within(list).getAllByTestId('gate-inbox-row')).toHaveLength(3)
    expect(within(list).queryByText('Secret project')).not.toBeInTheDocument()

    const spec = await row('Spec approval')
    expect(within(spec).getByText('Inventory tracker')).toBeInTheDocument()
    expect(within(spec).getByText('Software delivery')).toBeInTheDocument()
    expect(within(spec).getByRole('link', { name: 'Technical specification v2' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/artifacts/technical_specification?version=2`,
    )
    expect(spec.querySelector('time')?.getAttribute('title')).toBeTruthy()
    expect(within(spec).queryByText('Overdue')).not.toBeInTheDocument()
    expect(await within(spec).findByRole('button', { name: 'Approve' })).toBeInTheDocument()

    // Another pipeline's gate: titles from its definition, the artifact as text (no SDLC page), overdue.
    const report = await row('Report approval')
    expect(within(report).getByText('Fixture review')).toBeInTheDocument()
    expect(within(report).getByText('Check report v2')).toBeInTheDocument()
    expect(within(report).queryByRole('link', { name: 'Check report v2' })).not.toBeInTheDocument()
    expect(within(report).getByText('Overdue')).toBeInTheDocument()
    expect(document.title).toBe('Gates · Tevrin')
  })

  it('FR307_escalation_links_to_the_project: no approve or reject, an "Open project" link instead', async () => {
    const { project } = seedInbox()
    await openInbox()

    const escalation = await row('Escalation')
    expect(await within(escalation).findByRole('link', { name: /^Open project/ })).toHaveAttribute('href', `/projects/${project.id}`)
    expect(within(escalation).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(within(escalation).queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument()
  })

  it('FR307_can_decide: a Viewer sees the Owner-only note instead of the buttons; escalations still link', async () => {
    seedInbox('viewer')
    await openInbox()
    const spec = await row('Spec approval')
    expect(await within(spec).findByText(CANNOT_DECIDE_NOTE)).toBeInTheDocument()
    expect(within(spec).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(within(await row('Report approval')).getByText(CANNOT_DECIDE_NOTE)).toBeInTheDocument()
    expect(await within(await row('Escalation')).findByRole('link', { name: /^Open project/ })).toBeInTheDocument()
  })

  it('FR307_empty: "No gates waiting for you"', async () => {
    signInAs({ systemRole: 'user' })
    seedProject({ name: 'Quiet project' })
    await openInbox()
    expect(await screen.findByText('No gates waiting for you')).toBeInTheDocument()
  })

  it('FR307_error: a load error shows the shared error state with a retry', async () => {
    signInAs({ systemRole: 'user' })
    server.use(http.get('*/api/pipeline-gates', () => problem(500, 'UPSTREAM_ERROR', 'Boom')))
    await openInbox()
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("The gates couldn't load")
    expect(within(alert).getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('FR307_approve: an SDLC gate gets the project dialog copy; it leaves the list with a toast', async () => {
    const { sdlc } = seedInbox()
    const { user } = await openInbox()

    await user.click(await button('Spec approval', 'Approve'))
    const dialog = await screen.findByRole('dialog', { name: 'Approve specification approval?' })
    expect(dialog).toHaveAccessibleDescription(approveEffect.spec_approval)
    await user.type(within(dialog).getByLabelText('Comment (optional)'), 'Looks good')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))

    expect(await screen.findByText('Specification approval approved.')).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.queryByRole('heading', { level: 2, name: 'Spec approval' })).not.toBeInTheDocument()
    })
    const gateId = sdlc?.gates[0]?.id ?? ''
    expect(requestsTo('POST', `/api/pipeline-runs/${sdlc?.run.id ?? ''}/gates/${gateId}/approve`).map((r) => r.body)).toEqual([
      { comment: 'Looks good' },
    ])
  })

  it('FR307_reject: feedback is optional (as the project gates); descriptions come from the host or the engine', async () => {
    const { fixture, sdlc } = seedInbox()
    const { user } = await openInbox()

    await user.click(await button('Report approval', 'Reject'))
    const dialog = await screen.findByRole('alertdialog', { name: 'Reject report approval?' })
    expect(dialog).toHaveAccessibleDescription(defaultGateEffects.reject)
    expect(within(dialog).getByText(/The run doesn’t pass this gate\./)).toBeInTheDocument()
    await user.type(within(dialog).getByLabelText('Feedback (optional)'), 'Add the regional totals')
    await user.click(within(dialog).getByRole('button', { name: 'Reject' }))
    expect(await screen.findByText('Report approval rejected.')).toBeInTheDocument()
    const reportId = fixture.gates[0]?.id ?? ''
    expect(requestsTo('POST', `/api/pipeline-runs/${fixture.run.id}/gates/${reportId}/reject`).map((r) => r.body)).toEqual([
      { comment: 'Add the regional totals' },
    ])

    await user.click(await button('Spec approval', 'Reject'))
    const second = await screen.findByRole('alertdialog', { name: 'Reject specification approval?' })
    expect(second).toHaveAccessibleDescription(rejectEffect.spec_approval)
    await user.click(within(second).getByRole('button', { name: 'Reject' }))
    expect(await screen.findByText('Specification approval rejected.')).toBeInTheDocument()
    const specId = sdlc?.gates[0]?.id ?? ''
    expect(requestsTo('POST', `/api/pipeline-runs/${sdlc?.run.id ?? ''}/gates/${specId}/reject`).map((r) => r.body)).toEqual([
      { comment: null },
    ])
  })

  it('FR307_comment_max: a comment over 4000 characters is blocked before sending', async () => {
    const { fixture } = seedInbox()
    const { user } = await openInbox()
    await user.click(await button('Report approval', 'Reject'))
    const dialog = await screen.findByRole('alertdialog')
    await user.click(within(dialog).getByLabelText('Feedback (optional)'))
    await user.paste('x'.repeat(4001))
    await user.click(within(dialog).getByRole('button', { name: 'Reject' }))
    expect(await within(dialog).findAllByText('Comments are at most 4000 characters.')).not.toHaveLength(0)
    const gateId = fixture.gates[0]?.id ?? ''
    expect(requestsTo('POST', `/api/pipeline-runs/${fixture.run.id}/gates/${gateId}/reject`)).toHaveLength(0)
  })

  it('FR307_focus_after_emptying: deciding the last gate moves focus to the empty state', async () => {
    seedInbox('owner', { sdlc: false })
    const { user } = await openInbox()
    await user.click(await button('Report approval', 'Approve'))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    const empty = await screen.findByTestId('gate-inbox-empty')
    expect(empty).toHaveTextContent('No gates waiting for you')
    await waitFor(() => {
      expect(empty).toHaveFocus()
    })
  })

  it('FR307_forbidden: a role change after loading shows the Owner-only copy', async () => {
    const { me, project } = seedInbox()
    const { user } = await openInbox()
    await user.click(await button('Spec approval', 'Approve'))
    const dialog = await screen.findByRole('dialog')
    project.members = project.members.map((m) => (m.userId === me.id ? { ...m, role: 'contributor' } : m))
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(await within(dialog).findByText(contextualErrorMessages.pipelineGateDecision.FORBIDDEN)).toBeInTheDocument()
  })

  it('FR307_gate_not_pending: a gate decided elsewhere shows the stale-gate copy and the list refreshes', async () => {
    const { sdlc } = seedInbox()
    const { user } = await openInbox()
    await user.click(await button('Spec approval', 'Approve'))
    const gate = sdlc?.gates[0]
    if (gate) {
      gate.status = 'approved'
    }
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(await within(dialog).findByText(contextualErrorMessages.pipelineGateDecision.GATE_NOT_PENDING)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => {
      expect(screen.queryByRole('heading', { level: 2, name: 'Spec approval' })).not.toBeInTheDocument()
    })
  })

  it('FR307_definition_unavailable: the shared DEFINITION_UNAVAILABLE copy', async () => {
    const { fixture } = seedInbox()
    pipelinesDb.unavailableRuns.add(fixture.run.id)
    const { user } = await openInbox()
    await user.click(await button('Report approval', 'Approve'))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))
    expect(await within(dialog).findByText(errorMessages.DEFINITION_UNAVAILABLE)).toBeInTheDocument()
  })

  it('FR307_refresh: no live joins; the inbox refetches on window focus and polls every 30 s', async () => {
    const { escalation } = seedInbox()
    const { live, queryClient } = await openInbox()
    await row('Escalation')
    expect(live.connections).toHaveLength(0)
    const query = queryClient.getQueryCache().find({ queryKey: engineKeys.gateInbox(null) })
    expect(query?.observers[0]?.options.refetchInterval).toBe(GATE_INBOX_REFRESH_MS)

    if (escalation) {
      escalation.status = 'approved'
    }
    act(() => {
      focusManager.setFocused(false)
      focusManager.setFocused(true)
    })
    await waitFor(() => {
      expect(screen.queryByRole('heading', { level: 2, name: 'Escalation' })).not.toBeInTheDocument()
    })
    focusManager.setFocused(undefined)
  })

  it('FR307_load_more: follows the cursor to the next page', async () => {
    const { sdlc } = seedInbox()
    for (let i = 0; i < GATE_INBOX_PAGE_SIZE; i += 1) {
      if (sdlc) {
        addPipelineGate(sdlc, { type: 'escalation', title: `Older escalation ${String(i + 1)}`, createdAt: '2026-10-01T09:00:00.000Z' })
      }
    }
    const { user } = await openInbox()
    const list = await screen.findByRole('list', { name: 'Pending gates' })
    expect(within(list).getAllByTestId('gate-inbox-row')).toHaveLength(GATE_INBOX_PAGE_SIZE)
    await user.click(screen.getByRole('button', { name: 'Load more' }))
    await waitFor(() => {
      expect(within(list).getAllByTestId('gate-inbox-row')).toHaveLength(GATE_INBOX_PAGE_SIZE + 3)
    })
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
  })

  it.each(THEMES)('FR307_a11y: one h1 and no axe violations (%s theme)', async (theme) => {
    setTheme(theme)
    seedInbox()
    const { container } = await openInbox()
    await within(await row('Escalation')).findByRole('link', { name: /^Open project/ })
    await button('Spec approval', 'Approve')
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(await axeViolations(container)).toEqual([])
  })
})
