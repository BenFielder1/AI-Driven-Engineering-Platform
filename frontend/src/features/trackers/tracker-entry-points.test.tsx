import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { describeRunError } from '../runs/run-format'
import { describeActivity } from '../activity/activity-summary'
import { errorMessage, errorMessages, ApiError, trackerCopy } from '../../lib/errors'
import { signInAs } from '../../mocks/handlers/auth'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { seedProject } from '../../mocks/handlers/projects'
import { requestsTo } from '../../test/project-settings'
import { renderRoute, renderWithProviders } from '../../test/render'
import { hasWizardDraft, readWizardDraft } from '../projects/wizard-draft'
import { StartVersionDialog } from '../versions/start-version-dialog'
import { IDEA_REQUIRED_WITH_ITEMS, parsePicks, removePickedItem, type PickedImport } from './tracker-picks'

type User = ReturnType<typeof userEvent.setup>

async function openWizardIdea() {
  const me = signInAs({ systemRole: 'user' })
  await renderRoute('/projects/new')
  const user = userEvent.setup()
  await screen.findByRole('heading', { level: 2, name: /Step 1 of 8: Source/ })
  await user.click(screen.getByRole('button', { name: 'Next' }))
  await screen.findByRole('heading', { level: 2, name: /Step 2 of 8: Project idea/ })
  return { me, user }
}

async function pickProj1(user: User) {
  await user.click(screen.getByRole('button', { name: 'Import from tracker' }))
  const dialog = await screen.findByRole('dialog', { name: 'Import from tracker' })
  const select = await within(dialog).findByLabelText('Choose a project')
  await within(select).findByRole('option', { name: /Project One/ })
  await user.selectOptions(select, 'PROJ')
  await user.click(await within(dialog).findByRole('checkbox', { name: /PROJ-1 Login/ }))
  await user.click(within(dialog).getByRole('button', { name: 'Add 1 item' }))
  await waitFor(() => {
    expect(screen.queryByRole('dialog', { name: 'Import from tracker' })).not.toBeInTheDocument()
  })
}

const pick = (keys: string[], selection: PickedImport['selection'] = { keys, containerId: 'PROJ' }): PickedImport => ({
  provider: 'jira',
  connectionId: null,
  selection,
  options: { includeChildren: true, includeComments: false, includeAssignees: false },
  items: keys.map((key) => ({ key, title: key, url: `https://acme.atlassian.net/browse/${key}` })),
})

describe('Where imports start (FR-171)', () => {
  it('FR171_wizard_empty_idea_with_items_shows_the_spec_error', async () => {
    const { me, user } = await openWizardIdea()
    seedPlatformConnection({ userId: me.id, provider: 'jira', label: 'Acme Jira' })
    await user.type(screen.getByLabelText('Project name'), 'Inventory')
    await pickProj1(user)
    const chips = screen.getByRole('list', { name: 'Items to import' })
    expect(within(chips).getByText('PROJ-1')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Next' }))
    expect(await screen.findAllByText(IDEA_REQUIRED_WITH_ITEMS)).not.toHaveLength(0)
    expect(screen.getByRole('heading', { level: 2, name: /Step 2 of 8/ })).toBeInTheDocument()
  })

  it('FR171_wizard_chips_are_removable', async () => {
    const { me, user } = await openWizardIdea()
    seedPlatformConnection({ userId: me.id, provider: 'jira', label: 'Acme Jira' })
    await pickProj1(user)
    await user.click(screen.getByRole('button', { name: 'Remove PROJ-1' }))
    expect(screen.queryByRole('list', { name: 'Items to import' })).not.toBeInTheDocument()
  })

  it('FR169_no_connection_from_the_wizard_keeps_its_state', async () => {
    const { user } = await openWizardIdea()
    await user.type(screen.getByLabelText('Project name'), 'Inventory')
    await user.click(screen.getByRole('button', { name: 'Import from tracker' }))
    const dialog = await screen.findByRole('dialog', { name: 'Import from tracker' })
    await user.click(await within(dialog).findByRole('link', { name: 'Go to Connections' }))
    expect(hasWizardDraft()).toBe(true)
    expect(readWizardDraft()?.values.name).toBe('Inventory')
  })

  it('FR171_new_version_sends_source_imports_and_keeps_the_request_required', async () => {
    const me = signInAs({ systemRole: 'user' })
    seedPlatformConnection({ userId: me.id, provider: 'jira', label: 'Acme Jira' })
    const project = seedProject({ currentPhase: 'release', members: [{ userId: me.id, role: 'owner' }] })
    const onStarted = vi.fn()
    renderWithProviders(<StartVersionDialog projectId={project.id} released={null} suggested="9.0.0" versions={[]} onClose={vi.fn()} onStarted={onStarted} />)
    const user = userEvent.setup()
    await pickProj1(user)

    await user.click(screen.getByRole('button', { name: 'Start development' }))
    expect(await screen.findAllByText(IDEA_REQUIRED_WITH_ITEMS)).not.toHaveLength(0)
    expect(requestsTo('POST', '/versions')).toHaveLength(0)

    await user.type(screen.getByLabelText('What should this version add or fix?'), 'Add login.')
    await user.click(screen.getByRole('button', { name: 'Start development' }))
    await waitFor(() => {
      expect(requestsTo('POST', '/versions')[0]?.body).toMatchObject({
        request: 'Add login.',
        sourceImports: [{ provider: 'jira', selection: { keys: ['PROJ-1'], containerId: 'PROJ' } }],
      })
    })
  })
})

describe('Picks (FR-171)', () => {
  it('FR171_removing_a_chip_from_select_all_turns_it_into_the_remaining_keys', () => {
    const next = removePickedItem([pick(['A-1', 'A-2'], { query: 'project = A', containerId: null })], 0, 'A-1')
    expect(next[0]?.selection).toEqual({ keys: ['A-2'], containerId: null })
    expect(removePickedItem(next, 0, 'A-2')).toEqual([])
  })

  it('FR171_a_draft_that_does_not_parse_counts_as_no_picks', () => {
    expect(parsePicks('not json')).toEqual([])
    expect(parsePicks('[{"provider":"nope"}]')).toEqual([])
  })
})

describe('Tracker copy (§6, FR-193)', () => {
  it('FR165_spec_copy_is_in_the_one_map', () => {
    expect(errorMessages.TRACKER_SITE_NOT_ALLOWED).toBe('Enter your Jira Cloud site, like acme.atlassian.net.')
    expect(errorMessages.TRACKER_PERMISSION_MISSING).toBe('The AIDE GitHub App needs the Issues permission. Update it in GitHub settings.')
    expect(trackerCopy('TRACKER_NOT_CONNECTED', 'Jira')).toBe('Connect Jira to do this.')
    expect(trackerCopy('TRACKER_QUERY_INVALID', 'Jira', 'bad')).toBe("Jira couldn't run that query: bad")
    expect(trackerCopy('TRACKER_RATE_LIMITED', 'Linear')).toBe('Linear is rate limiting requests. AIDE will retry.')
    expect(trackerCopy('TRACKER_IMPORT_FAILED', 'Jira', 'no access')).toBe('The import from Jira failed: no access')
  })

  it('FR170_no_placeholder_ever_reaches_the_screen', () => {
    const bare = new ApiError({ status: 400, errorCode: 'SOURCE_ITEMS_TOO_LARGE' })
    expect(errorMessage(bare)).toBe('These items are too large for the AI model. Remove some items or choose a larger model.')
    const titled = new ApiError({ status: 400, errorCode: 'SOURCE_ITEMS_TOO_LARGE', title: 'These items are too large for the AI model (2 MB of 1 MB).' })
    expect(errorMessage(titled)).toContain('2 MB of 1 MB')
    expect(errorMessage(new ApiError({ status: 409, errorCode: 'TRACKER_NOT_CONNECTED' }))).toBe('Connect this tracker to do this.')
    expect(describeRunError('SOURCE_ITEMS_TOO_LARGE: Too large (2 of 1).')).toEqual({ title: null, message: 'Too large (2 of 1).' })
  })

  it('FR193_activity_copy_of_the_tracker_events', () => {
    const d = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 'server' })
    expect(d('TRACKER_IMPORT_COMPLETED', { provider: 'jira', itemCount: 12, version: 2 })).toContain('Imported 12 items from Jira (source items v2)')
    expect(d('TRACKER_IMPORT_FAILED', { provider: 'linear' })).toContain('The import from Linear failed')
    expect(d('SOURCE_ITEMS_REMOVED', { provider: 'github', itemCount: 1, version: null })).toContain('Removed 1 item imported from GitHub Issues')
  })
})
