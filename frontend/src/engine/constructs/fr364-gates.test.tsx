import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, onTestFinished } from 'vitest'

import type { ProjectRole } from '../../lib/permissions'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import {
  addExternalActionGate,
  addToolApprovalGate,
  addVariantSelectGate,
  seedCallsimRun,
  seedVariantsRun,
} from '../../mocks/handlers/engine-constructs'
import { seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { axeViolations } from '../../test/axe'
import { renderRoute } from '../../test/render'

/** A project where the signed-in user is `role` (an Owner besides, so gates can be decided by someone). */
function seedAs(role: ProjectRole) {
  const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  const members = [{ userId: me.id, role }, ...(role === 'owner' ? [] : [{ userId: owner.id, role: 'owner' as const }])]
  return seedProject({ name: 'On-call', members })
}

/** The `i`th element, failing the test when there isn't one. */
function nth(elements: readonly HTMLElement[], i: number): HTMLElement {
  const el = elements[i]
  if (!el) {
    throw new Error(`No element ${String(i)}`)
  }
  return el
}

async function openInbox() {
  const utils = await renderRoute('/gates')
  await screen.findByRole('heading', { level: 1, name: 'Gates' })
  return { ...utils, user: userEvent.setup() }
}

const card = async (title: string) => (await screen.findByRole('heading', { level: 2, name: title })).closest('li') as HTMLElement

/** The JSON bodies of the gate decision requests sent during the test. */
function decisionBodies() {
  const bodies: unknown[] = []
  const record = ({ request }: { request: Request }) => {
    if (/\/gates\/[^/]+\/(approve|reject)$/.test(new URL(request.url).pathname)) {
      void request.clone().json().then((b: unknown) => bodies.push(b))
    }
  }
  server.events.on('request:start', record)
  onTestFinished(() => {
    server.events.removeListener('request:start', record)
  })
  return bodies
}

describe('FR-364: gate cards for tool approval, external action and variant select', () => {
  it('FR364_tool_approval_owner: an Owner sees the tool, its tier, the reason and the redacted arguments, with approve and reject', async () => {
    const project = seedAs('owner')
    addToolApprovalGate(seedCallsimRun(project))
    await openInbox()
    const row = within(await card('Tool approval'))
    const payload = within(row.getByTestId('gate-payload-tool-call'))
    expect(payload.getByText('restart-pod')).toBeInTheDocument()
    expect(payload.getByText('Destructive')).toBeInTheDocument()
    expect(payload.getByText('The pod is crash-looping; a restart clears the stuck connection pool.')).toBeInTheDocument()
    const args = payload.getByRole('group', { name: 'Arguments (redacted)' })
    expect(args).toHaveTextContent('"pod": "api-7f9c"')
    expect(args).toHaveTextContent('"token": "[redacted]"')
    expect(await row.findByRole('button', { name: 'Approve' })).toBeInTheDocument()
    expect(row.getByRole('button', { name: 'Reject' })).toBeInTheDocument()
    expect(await axeViolations(document.body)).toEqual([])
  })

  it('FR364_tool_approval_contributor: a Contributor sees the card without approve', async () => {
    const project = seedAs('contributor')
    addToolApprovalGate(seedCallsimRun(project))
    await openInbox()
    const row = within(await card('Tool approval'))
    expect(row.getByText('restart-pod')).toBeInTheDocument()
    expect(row.getByRole('group', { name: 'Arguments (redacted)' })).toBeInTheDocument()
    expect(await row.findByText(/Owner/)).toBeInTheDocument()
    expect(row.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(row.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument()
  })

  it('FR364_tool_approval_dialog: the approve dialog repeats the tool call, and approving sends no choice', async () => {
    const project = seedAs('owner')
    addToolApprovalGate(seedCallsimRun(project))
    const bodies = decisionBodies()
    const { user } = await openInbox()
    await user.click(await within(await card('Tool approval')).findByRole('button', { name: 'Approve' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Approve tool approval?' }))
    expect(dialog.getByText('restart-pod')).toBeInTheDocument()
    expect(dialog.getByRole('group', { name: 'Arguments (redacted)' })).toBeInTheDocument()
    await user.click(dialog.getByRole('button', { name: 'Approve' }))
    expect(await screen.findByText('Tool approval approved.')).toBeInTheDocument()
    await waitFor(() => {
      expect(bodies).toEqual([{ comment: null }])
    })
  })

  it('FR364_external_handler_payload: the external action card shows the handler and the exact payload it sends', async () => {
    const project = seedAs('owner')
    addExternalActionGate(seedCallsimRun(project), '{"channel":"#ops","text":"Restarting api-7f9c","ticket":{"id":42}}')
    await openInbox()
    const row = within(await card('External action'))
    const payload = within(row.getByTestId('gate-payload-external'))
    expect(payload.getByText('post-message')).toBeInTheDocument()
    expect(await payload.findByText('Simulate the call')).toBeInTheDocument()
    const sent = payload.getByRole('group', { name: 'Payload to send' })
    expect(sent.textContent).toBe(JSON.stringify({ channel: '#ops', text: 'Restarting api-7f9c', ticket: { id: 42 } }, null, 2))
    expect(sent).toHaveAttribute('tabindex', '0')
    expect(await row.findByRole('button', { name: 'Approve' })).toBeInTheDocument()
  })

  it('FR364_variant_select_pick: the variants show side by side, and picking one approves the gate with that choice', async () => {
    const project = seedAs('owner')
    addVariantSelectGate(seedVariantsRun(project))
    const bodies = decisionBodies()
    const { user } = await openInbox()
    const row = within(await card('Pick a headline'))
    const variants = within(row.getByRole('list', { name: 'Variants' }))
    const items = variants.getAllByRole('listitem')
    expect(items.map((i) => i.getAttribute('aria-labelledby') && within(i).getByText(/^v\d$/).textContent)).toEqual(['v1', 'v2', 'v3'])
    expect(await within(nth(items, 1)).findByText('Squeezed this morning')).toBeInTheDocument()
    expect(await within(nth(items, 2)).findByText('Juice, done right')).toBeInTheDocument()
    // Approve is the pick; the action area keeps Reject.
    expect(await row.findByRole('button', { name: 'Reject' })).toBeInTheDocument()
    expect(row.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()

    await user.click(row.getByRole('button', { name: 'Pick v2' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Pick v2 for pick a headline?' }))
    await user.click(dialog.getByRole('button', { name: 'Pick v2' }))
    expect(await screen.findByText('v2 picked for pick a headline.')).toBeInTheDocument()
    await waitFor(() => {
      expect(bodies).toEqual([{ comment: null, choice: 'v2' }])
    })
  })

  it('FR364_variant_select_contributor: a Contributor sees the variants without a pick', async () => {
    const project = seedAs('contributor')
    addVariantSelectGate(seedVariantsRun(project))
    await openInbox()
    const row = within(await card('Pick a headline'))
    expect(row.getAllByRole('listitem')).toHaveLength(3)
    expect(await row.findByText(/Owner/)).toBeInTheDocument()
    expect(row.queryByRole('button', { name: /^Pick/ })).not.toBeInTheDocument()
  })
})
