import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { messageFor } from '../../lib/errors'
import { authDb, seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedGate } from '../../mocks/handlers/lifecycle'
import { seedApprovalLink } from '../../mocks/handlers/notifications'
import { problem } from '../../mocks/handlers/problem'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { requestsTo } from '../../test/project-settings'
import { currentHref, renderRoute } from '../../test/render'

/** An Owner (non-admin) with a pending spec gate and a valid approve link. */
function setup(
  opts: { role?: 'owner' | 'viewer'; decision?: 'approve' | 'reject'; expiresAt?: string; usedAt?: string | null } = {},
) {
  const admin = seedUser({ email: 'root@example.com', systemRole: 'admin' })
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  const project: StoredProject = seedProject({
    name: 'Inventory tracker',
    currentPhase: 'specification',
    members: [
      { userId: admin.id, role: 'owner' },
      { userId: me.id, role: opts.role ?? 'owner' },
    ],
  })
  const gate = seedGate(project, { type: 'spec_approval', artifactType: 'technical_specification', artifactVersion: 3 })
  const link = seedApprovalLink({
    gateId: gate.id,
    projectId: project.id,
    decision: opts.decision ?? 'approve',
    ...(opts.expiresAt ? { expiresAt: opts.expiresAt } : {}),
    usedAt: opts.usedAt ?? null,
  })
  return { project, gate, link, me }
}

describe('Approval link page (FR-48)', () => {
  it('FR-48: shows the gate summary and confirming approves the gate', async () => {
    const { gate, link } = setup()
    await renderRoute(`/approvals/${link.token}`)
    const user = userEvent.setup()

    expect(await screen.findByRole('heading', { level: 1, name: 'Approve specification approval?' })).toBeInTheDocument()
    const summary = screen.getByRole('region', { name: 'Approval summary' })
    expect(within(summary).getByRole('link', { name: 'Inventory tracker' })).toBeInTheDocument()
    expect(within(summary).getByText('Specification approval')).toBeInTheDocument()
    expect(within(summary).getByText('Approve')).toBeInTheDocument()
    expect(within(summary).getByText(/Technical specification v3/)).toBeInTheDocument()
    expect(within(summary).getByText('Link expires')).toBeInTheDocument()
    expect(screen.getByText('The project moves to Planning.')).toBeInTheDocument()
    // The token never leaks through the Referer header.
    expect(document.head.querySelector('meta[name="referrer"]')).toHaveAttribute('content', 'no-referrer')

    await user.type(screen.getByLabelText('Comment (optional)'), 'Ship it')
    await user.click(screen.getByRole('button', { name: 'Confirm approval' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Specification approval approved' })).toHaveFocus()
    expect(screen.getByText(/This link can't be used again/)).toBeInTheDocument()
    expect(requestsTo('POST', `/api/approvals/${link.token}`)[0]?.body).toEqual({ comment: 'Ship it' })
    expect(gate.status).toBe('approved')
  })

  it('FR-48: a reject link asks for feedback and confirms the rejection', async () => {
    const { gate, link } = setup({ decision: 'reject' })
    await renderRoute(`/approvals/${link.token}`)
    const user = userEvent.setup()

    await screen.findByRole('heading', { level: 1, name: 'Reject specification approval?' })
    await user.click(screen.getByRole('button', { name: 'Confirm rejection' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Specification approval rejected' })).toBeInTheDocument()
    expect(requestsTo('POST', `/api/approvals/${link.token}`)[0]?.body).toEqual({})
    expect(gate.status).toBe('rejected')
  })

  it('FR-48: a used link says it was already used and nothing changed', async () => {
    const { gate, link } = setup({ usedAt: '2026-09-01T10:00:00Z' })
    await renderRoute(`/approvals/${link.token}`)

    expect(await screen.findByRole('heading', { level: 1, name: 'This link has already been used' })).toBeInTheDocument()
    expect(screen.getByText(messageFor('LINK_USED', 'approvalLink'))).toHaveTextContent(/nothing changed/)
    expect(screen.queryByRole('button', { name: /Confirm/ })).not.toBeInTheDocument()
    expect(gate.status).toBe('pending')
  })

  it('FR-48: a link used elsewhere while the page was open shows "already used" on confirm', async () => {
    const { link, gate } = setup()
    await renderRoute(`/approvals/${link.token}`)
    const user = userEvent.setup()

    const confirm = await screen.findByRole('button', { name: 'Confirm approval' })
    link.usedAt = '2026-09-01T10:00:00Z'
    await user.click(confirm)

    expect(await screen.findByRole('heading', { level: 1, name: 'This link has already been used' })).toBeInTheDocument()
    expect(screen.getByText(messageFor('LINK_USED', 'approvalLink'))).toBeInTheDocument()
    expect(gate.status).toBe('pending')
  })

  it('FR-48: an expired link says so and offers no confirmation', async () => {
    const { link } = setup({ expiresAt: '2026-01-01T00:00:00Z' })
    await renderRoute(`/approvals/${link.token}`)

    expect(await screen.findByRole('heading', { level: 1, name: 'This link has expired' })).toBeInTheDocument()
    expect(screen.getByText(messageFor('LINK_EXPIRED', 'approvalLink'))).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Confirm/ })).not.toBeInTheDocument()
  })

  it('FR-48: a non-Owner is forbidden', async () => {
    const { link, gate } = setup({ role: 'viewer' })
    await renderRoute(`/approvals/${link.token}`)

    expect(await screen.findByRole('heading', { level: 1, name: "You can't use this link" })).toBeInTheDocument()
    expect(screen.getByText(messageFor('FORBIDDEN', 'approvalLink'))).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Confirm/ })).not.toBeInTheDocument()
    expect(gate.status).toBe('pending')
  })

  it('FR-48: an unknown token shows not found', async () => {
    setup()
    await renderRoute('/approvals/tok_unknown')

    expect(await screen.findByRole('heading', { level: 1, name: 'Approval link not found' })).toBeInTheDocument()
  })

  it('FR-48: a gate decided elsewhere shows "already closed" without a confirm button', async () => {
    const { gate, link } = setup()
    gate.status = 'approved'
    await renderRoute(`/approvals/${link.token}`)

    expect(await screen.findByRole('heading', { level: 1, name: 'This gate is already closed' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open Inventory tracker' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Confirm/ })).not.toBeInTheDocument()
  })

  it('FR-48: GATE_NOT_PENDING on confirm switches to the closed state', async () => {
    const { link } = setup()
    server.use(
      http.post('*/api/approvals/:token', () => problem(409, 'GATE_NOT_PENDING', 'Only pending gates can be decided.')),
    )
    await renderRoute(`/approvals/${link.token}`)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Confirm approval' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'This gate is already closed' })).toBeInTheDocument()
  })

  it('FR-48: CONFLICT on confirm keeps the form and explains', async () => {
    const { link } = setup()
    server.use(http.post('*/api/approvals/:token', () => problem(409, 'CONFLICT', 'Concurrent change.')))
    await renderRoute(`/approvals/${link.token}`)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Confirm approval' }))
    expect(await screen.findByText(messageFor('CONFLICT', 'approvalLink'))).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirm approval' })).toBeInTheDocument()
  })

  it('FR-48: a signed-out user is sent to sign in and comes back to the link', async () => {
    const { link } = setup()
    authDb.currentUserId = null
    const { router } = await renderRoute(`/approvals/${link.token}`)
    const user = userEvent.setup()

    await waitFor(() => {
      expect(currentHref(router)).toBe(`/login?returnUrl=${encodeURIComponent(`/approvals/${link.token}`)}`)
    })
    await user.type(await screen.findByLabelText('Email'), 'ada@example.com')
    await user.type(screen.getByLabelText('Password'), 'password1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Approve specification approval?' })).toBeInTheDocument()
    expect(currentHref(router)).toBe(`/approvals/${link.token}`)
  })

  it('FR-48: a session that ended (401 from the API) redirects to sign in with the return path', async () => {
    const { link } = setup()
    server.use(http.get('*/api/approvals/:token', () => problem(401, 'UNAUTHENTICATED', 'Sign in.')))
    const { router } = await renderRoute(`/approvals/${link.token}`)

    await waitFor(() => {
      expect(currentHref(router)).toBe(`/login?returnUrl=${encodeURIComponent(`/approvals/${link.token}`)}`)
    })
  })
})
