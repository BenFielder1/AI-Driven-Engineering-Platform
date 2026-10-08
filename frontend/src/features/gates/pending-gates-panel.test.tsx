import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { errorMessages } from '../../lib/errors'
import { lockArtifact, seedGate } from '../../mocks/handlers/lifecycle'
import { problem } from '../../mocks/handlers/problem'
import { server } from '../../mocks/server'
import { currentTimelineStep, openOverviewAs } from '../../test/overview'
import { requestsTo } from '../../test/project-settings'

function pendingGate(name: string) {
  const heading = within(screen.getByRole('list', { name: 'Pending gates' })).getByRole('heading', { name })
  return heading.closest('li') as HTMLElement
}

describe('Overview → Pending gates (FR-13, FR-15)', () => {
  it('FR-15: each pending gate shows its type, created time, overdue badge and artifact reference', async () => {
    await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      seedGate(p, {
        type: 'spec_approval',
        artifactType: 'technical_specification',
        artifactVersion: 3,
        artifactId: '00000000-0000-7000-8000-00000000abcd',
        createdAt: '2026-09-01T09:00:00Z',
        overdue: true,
      })
    })

    const gate = pendingGate('Specification approval')
    expect(within(gate).getByText('Overdue')).toBeInTheDocument()
    expect(within(gate).getByRole('link', { name: 'Technical specification v3' })).toHaveAttribute(
      'href',
      expect.stringMatching(/\/artifacts$/) as unknown,
    )
    const time = gate.querySelector('time')
    expect(time).toHaveAttribute('dateTime', '2026-09-01T09:00:00Z')
    expect(time?.getAttribute('title')).toBeTruthy()
  })

  it('FR-13: an Owner approves with a comment, and the phase and timeline update', async () => {
    const { user, project } = await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      lockArtifact(p, 'technical_specification')
      seedGate(p, { type: 'spec_approval' })
    })
    expect(currentTimelineStep()).toHaveTextContent('Specification')

    await user.click(within(pendingGate('Specification approval')).getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve specification approval?' })
    expect(dialog).toHaveAccessibleDescription('The project moves to Planning.')
    await user.type(within(dialog).getByLabelText('Comment (optional)'), 'Looks good')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))

    expect(await screen.findByText('Specification approval approved.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => {
      expect(currentTimelineStep()).toHaveTextContent('Planning')
    })
    expect(screen.getByText('No gates are waiting for a decision')).toBeInTheDocument()
    expect(requestsTo('POST', '/approve')[0]?.body).toEqual({ comment: 'Looks good' })
    expect(project.currentPhase).toBe('planning')
  })

  it('FR-13: reject asks for confirmation and sends the feedback comment; the phase is unchanged', async () => {
    const { user, project } = await openOverviewAs('owner', { currentPhase: 'planning', approvalMode: 'strict' }, (p) => {
      seedGate(p, { type: 'plan_approval', artifactType: 'implementation_plan', artifactVersion: 1, artifactId: null })
    })

    await user.click(within(pendingGate('Plan approval')).getByRole('button', { name: 'Reject' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Reject plan approval?' })
    expect(dialog).toHaveAccessibleDescription(
      'The phase stays the same and the Plan generator runs again with your comment as feedback.',
    )
    expect(requestsTo('POST', '/reject')).toHaveLength(0)

    await user.type(within(dialog).getByLabelText('Feedback (optional)'), 'split step 4')
    await user.click(within(dialog).getByRole('button', { name: 'Reject' }))

    expect(await screen.findByText('Plan approval rejected.')).toBeInTheDocument()
    expect(requestsTo('POST', '/reject')[0]?.body).toEqual({ comment: 'split step 4' })
    expect(currentTimelineStep()).toHaveTextContent('Planning')
    expect(project.gates[0]?.status).toBe('rejected')

    // The decided gate moves to the gate history with its comment.
    await user.click(screen.getByRole('tab', { name: 'Gate history' }))
    const history = await screen.findByRole('list', { name: 'Gate history' })
    expect(within(history).getByText('Rejected')).toBeInTheDocument()
    expect(within(history).getByText('split step 4')).toBeInTheDocument()
    expect(within(history).getByText(/Ada Lovelace/)).toBeInTheDocument()
  })

  it('FR-13: cancelling the reject dialog sends nothing and returns focus to Reject', async () => {
    const { user } = await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      seedGate(p, { type: 'spec_approval' })
    })

    const reject = within(pendingGate('Specification approval')).getByRole('button', { name: 'Reject' })
    await user.click(reject)
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(reject).toHaveFocus()
    expect(requestsTo('POST', '/reject')).toHaveLength(0)
  })

  it.each(['contributor', 'viewer'] as const)('FR-13: a %s sees pending gates without actions', async (role) => {
    await openOverviewAs(role, { currentPhase: 'specification' }, (p) => {
      seedGate(p, { type: 'spec_approval' })
      seedGate(p, { type: 'escalation', escalationReason: 'max_cycles' })
    })

    const list = screen.getByRole('list', { name: 'Pending gates' })
    expect(within(list).getByRole('heading', { name: 'Specification approval' })).toBeInTheDocument()
    expect(within(list).getByRole('heading', { name: 'Escalation' })).toBeInTheDocument()
    expect(within(list).queryAllByRole('button')).toHaveLength(0)
    expect(screen.getByText('Only project Owners can decide gates.')).toBeInTheDocument()
  })

  it('FR-13: GATE_NOT_PENDING (decided elsewhere) is mapped in the dialog', async () => {
    server.use(
      http.post('*/api/projects/:id/gates/:gateId/approve', () =>
        problem(409, 'GATE_NOT_PENDING', 'Only pending gates can be decided.'),
      ),
    )
    const { user } = await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      seedGate(p, { type: 'spec_approval' })
    })

    await user.click(within(pendingGate('Specification approval')).getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }))

    expect(await within(dialog).findByText(errorMessages.GATE_NOT_PENDING)).toBeInTheDocument()
  })

  it('NFR-A11Y: after a successful decision, focus moves to the Pending gates heading (the opener is gone)', async () => {
    const { user } = await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      lockArtifact(p, 'technical_specification')
      seedGate(p, { type: 'spec_approval' })
    })

    const approve = within(pendingGate('Specification approval')).getByRole('button', { name: 'Approve' })
    await user.click(approve)
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Approve' }))

    await screen.findByText('Specification approval approved.')
    await waitFor(() => {
      expect(approve).not.toBeInTheDocument()
    })
    expect(screen.getByRole('heading', { name: 'Pending gates' })).toHaveFocus()
    expect(document.activeElement).not.toBe(document.body)
  })

  it('NFR-A11Y: the approve dialog is labelled, traps focus, and Escape returns focus to the opener', async () => {
    const { user } = await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      seedGate(p, { type: 'spec_approval' })
    })

    const approve = within(pendingGate('Specification approval')).getByRole('button', { name: 'Approve' })
    await user.click(approve)
    const dialog = await screen.findByRole('dialog', { name: 'Approve specification approval?' })
    expect(dialog).toHaveAttribute('aria-modal', 'true')

    const comment = within(dialog).getByLabelText('Comment (optional)')
    const cancel = within(dialog).getByRole('button', { name: 'Cancel' })
    const confirm = within(dialog).getByRole('button', { name: 'Approve' })
    expect(comment).toHaveFocus()
    await user.tab()
    expect(cancel).toHaveFocus()
    await user.tab()
    expect(confirm).toHaveFocus()
    await user.tab()
    expect(comment).toHaveFocus()
    await user.tab({ shift: true })
    expect(confirm).toHaveFocus()

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(approve).toHaveFocus()
  })
})

describe('Overview → Escalation decision (FR-14)', () => {
  async function openEscalation(projectVersion = 3) {
    const utils = await openOverviewAs('owner', { currentPhase: 'review', status: 'reviewing', projectVersion }, (p) => {
      p.phaseAtVersion[2] = 'planning'
      seedGate(p, { type: 'escalation', escalationReason: 'no_improvement', reason: 'Score 0.83 after 0.84.' })
    })
    const gate = pendingGate('Escalation')
    expect(within(gate).getByText('Reason: No improvement since the last review cycle')).toBeInTheDocument()
    expect(within(gate).queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument()
    await utils.user.click(within(gate).getByRole('button', { name: 'Decide' }))
    const dialog = await screen.findByRole('dialog', { name: 'Decide the escalation' })
    return { ...utils, dialog }
  }

  it('FR-14: rollback requires a target version below the current version, and confirms as a destructive action', async () => {
    const { user, dialog, project } = await openEscalation(3)

    await user.click(within(dialog).getByRole('button', { name: 'Record decision' }))
    expect(within(dialog).getAllByText('Choose a decision.').length).toBeGreaterThan(0)

    await user.click(within(dialog).getByLabelText('Roll back'))
    // §7: destructive, so the dialog becomes an alertdialog with a danger submit naming the version.
    expect(dialog).toHaveAttribute('role', 'alertdialog')
    expect(screen.getByRole('alertdialog', { name: 'Decide the escalation' })).toBe(dialog)
    const target = within(dialog).getByLabelText('Roll back to version')
    expect(target).toHaveAccessibleDescription('A version from 1 to 2 (the project is at version 3).')
    await user.click(within(dialog).getByRole('button', { name: 'Roll back' }))
    expect(target).toHaveAccessibleDescription(expect.stringContaining('Enter the version to roll back to.'))

    await user.type(target, '3')
    await user.click(within(dialog).getByRole('button', { name: 'Roll back to v3' }))
    expect(target).toHaveAccessibleDescription(
      expect.stringContaining('Enter a whole version number from 1 to 2.'),
    )
    expect(target).toHaveAttribute('aria-invalid', 'true')
    expect(requestsTo('POST', '/approve')).toHaveLength(0)

    await user.clear(target)
    await user.type(target, '2')
    const submit = within(dialog).getByRole('button', { name: 'Roll back to v2' })
    expect(submit.className).toMatch(/danger/)
    await user.click(submit)

    expect(await screen.findByText('Escalation decided: roll back to version 2.')).toBeInTheDocument()
    expect(requestsTo('POST', '/approve')[0]?.body).toEqual({ decision: 'rollback', comment: null, targetVersion: 2 })
    await waitFor(() => {
      expect(currentTimelineStep()).toHaveTextContent('Planning')
    })
    expect(project.projectVersion).toBe(2)
  })

  it('FR-14: switching away from rollback restores the normal dialog and submit', async () => {
    const { user, dialog } = await openEscalation()

    await user.click(within(dialog).getByLabelText('Roll back'))
    expect(dialog).toHaveAttribute('role', 'alertdialog')
    await user.click(within(dialog).getByLabelText('Continue'))
    expect(dialog).toHaveAttribute('role', 'dialog')
    expect(within(dialog).getByRole('button', { name: 'Record decision' })).toBeInTheDocument()
  })

  it('FR-14: continue sends the decision without a target version', async () => {
    const { user, dialog } = await openEscalation()

    await user.click(within(dialog).getByLabelText('Continue'))
    expect(within(dialog).queryByLabelText('Roll back to version')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Record decision' }))

    expect(await screen.findByText('Escalation decided: continue.')).toBeInTheDocument()
    expect(requestsTo('POST', '/approve')[0]?.body).toEqual({ decision: 'continue', comment: null, targetVersion: null })
    await waitFor(() => {
      expect(currentTimelineStep()).toHaveTextContent('Execution')
    })
  })

  it('FR-14: accept takes an optional comment, which is recorded', async () => {
    const { user, dialog } = await openEscalation()

    await user.click(within(dialog).getByLabelText('Accept'))
    await user.type(within(dialog).getByLabelText('Comment (optional)'), 'Known gaps are tracked separately')
    await user.click(within(dialog).getByRole('button', { name: 'Record decision' }))

    expect(await screen.findByText('Escalation decided: accept.')).toBeInTheDocument()
    expect(requestsTo('POST', '/approve')[0]?.body).toEqual({
      decision: 'accept',
      comment: 'Known gaps are tracked separately',
      targetVersion: null,
    })
  })

  it('FR-14: accept without a comment sends a null comment', async () => {
    const { user, dialog } = await openEscalation()

    await user.click(within(dialog).getByLabelText('Accept'))
    await user.click(within(dialog).getByRole('button', { name: 'Record decision' }))

    expect(await screen.findByText('Escalation decided: accept.')).toBeInTheDocument()
    expect(requestsTo('POST', '/approve')[0]?.body).toEqual({ decision: 'accept', comment: null, targetVersion: null })
  })

  it('FR-14: INVALID_DECISION from the server is mapped', async () => {
    server.use(
      http.post('*/api/projects/:id/gates/:gateId/approve', () =>
        problem(400, 'INVALID_DECISION', 'That decision is not valid.'),
      ),
    )
    const { user, dialog } = await openEscalation()

    await user.click(within(dialog).getByLabelText('Continue'))
    await user.click(within(dialog).getByRole('button', { name: 'Record decision' }))

    expect(await within(dialog).findByText(errorMessages.INVALID_DECISION)).toBeInTheDocument()
  })

  it('NFR-A11Y: the escalation dialog is labelled and keeps focus inside', async () => {
    const { user, dialog } = await openEscalation()

    expect(dialog).toHaveAccessibleDescription('Review needs a human decision before the project can continue.')
    expect(within(dialog).getByRole('group', { name: 'Decision' })).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Continue')).toHaveFocus()
    const submit = within(dialog).getByRole('button', { name: 'Record decision' })
    submit.focus()
    await user.tab()
    expect(dialog).toContainElement(document.activeElement as HTMLElement)
  })
})
