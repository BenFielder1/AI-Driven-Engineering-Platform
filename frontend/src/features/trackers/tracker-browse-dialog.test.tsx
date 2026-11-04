import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { trackerItem, trackersDb, seedTrackerStatus } from '../../mocks/handlers/trackers'
import { renderWithProviders } from '../../test/render'
import { TrackerBrowseDialog } from './tracker-browse-dialog'
import type { PickedImport } from './tracker-picks'

function setup({ capacity = 200, connect = true }: { capacity?: number; connect?: boolean } = {}) {
  const me = signInAs({ systemRole: 'user' })
  if (connect) {
    seedPlatformConnection({ userId: me.id, provider: 'jira', label: 'Acme Jira' })
  }
  const onAdd = vi.fn<(pick: PickedImport) => void>()
  const onOpenConnections = vi.fn<() => void>()
  renderWithProviders(<TrackerBrowseDialog capacity={capacity} onClose={vi.fn()} onAdd={onAdd} onOpenConnections={onOpenConnections} />)
  return { onAdd, onOpenConnections, user: userEvent.setup() }
}

async function chooseProject(user: ReturnType<typeof userEvent.setup>) {
  const select = await screen.findByLabelText('Choose a project')
  await within(select).findByRole('option', { name: /Project One/ })
  await user.selectOptions(select, 'PROJ')
}

const result = (name: RegExp) => screen.findByRole('checkbox', { name })

describe('Import from tracker dialog (FR-169)', () => {
  it('FR169_no_connection_links_to_connections_and_saves_the_callers_state_first', async () => {
    const { onOpenConnections, user } = setup({ connect: false })
    expect(await screen.findByRole('heading', { name: 'No issue tracker is connected' })).toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: 'Go to Connections' }))
    expect(onOpenConnections).toHaveBeenCalledOnce()
  })

  it('FR169_ticked_items_become_a_keys_selection_with_the_default_options', async () => {
    const { onAdd, user } = setup()
    await chooseProject(user)
    await user.click(await result(/PROJ-1 Login/))
    await user.click(screen.getByRole('button', { name: 'Add 1 item' }))

    expect(onAdd).toHaveBeenCalledOnce()
    expect(onAdd.mock.calls[0]?.[0]).toMatchObject({
      provider: 'jira',
      selection: { keys: ['PROJ-1'], containerId: 'PROJ' },
      options: { includeChildren: true, includeComments: false, includeAssignees: false },
    })
  })

  it('FR169_done_items_are_hidden_unless_the_filter_asks_for_them', async () => {
    const { user } = setup()
    await chooseProject(user)
    await result(/PROJ-1/)
    expect(screen.queryByRole('checkbox', { name: /PROJ-3/ })).not.toBeInTheDocument()

    await user.click(screen.getByRole('checkbox', { name: 'Done' }))
    await user.click(screen.getByRole('button', { name: 'Search' }))
    expect(await result(/PROJ-3 Old work/)).toBeInTheDocument()
  })

  it('FR169_invalid_jql_shows_the_trackers_message_on_the_field', async () => {
    const { user } = setup()
    await screen.findByLabelText('Choose a project')
    await user.click(screen.getByRole('radio', { name: 'JQL' }))
    await user.type(screen.getByRole('textbox', { name: 'JQL' }), 'invalid = 1')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: 'JQL' })).toHaveAccessibleDescription(expect.stringContaining("Jira couldn't run that query: Field 'nope' does not exist."))
    })
  })

  it('FR169_linear_and_gitlab_offer_structured_filters_only', async () => {
    const me = signInAs({ systemRole: 'user' })
    seedPlatformConnection({ userId: me.id, provider: 'linear', label: 'Key' })
    renderWithProviders(<TrackerBrowseDialog capacity={200} onClose={vi.fn()} onAdd={vi.fn()} />)
    await screen.findByLabelText('Choose a team or project')
    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Title contains')).toBeInTheDocument()
  })

  it('FR169_results_page_with_the_cursor', async () => {
    trackersDb.pageSize = 1
    const { user } = setup()
    await chooseProject(user)
    await result(/PROJ-1/)
    expect(screen.queryByRole('checkbox', { name: /PROJ-2/ })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Load more' }))
    expect(await result(/PROJ-2/)).toBeInTheDocument()
  })

  it('FR169_select_all_matching_within_the_cap_repeats_the_search_on_import', async () => {
    trackersDb.pageSize = 1
    const { onAdd, user } = setup()
    await chooseProject(user)
    await result(/PROJ-1/)
    await user.click(screen.getByRole('button', { name: 'Select all matching' }))
    await waitFor(() => {
      expect(screen.getByTestId('tracker-selection-status')).toHaveTextContent('2 selected: all matching items.')
    })
    await user.click(screen.getByRole('button', { name: 'Add 2 items' }))
    const pick = onAdd.mock.calls[0]?.[0]
    expect(pick?.selection.keys).toBeUndefined()
    expect(pick?.selection.containerId).toBe('PROJ')
    expect(pick?.items.map((i) => i.key)).toEqual(['PROJ-1', 'PROJ-2'])
  })

  it('FR169_select_all_matching_stops_at_the_cap_and_says_so', async () => {
    trackersDb.items = Array.from({ length: 5 }, (_, i) => trackerItem(`PROJ-${String(i + 1)}`))
    trackersDb.pageSize = 2
    const { onAdd, user } = setup({ capacity: 3 })
    await chooseProject(user)
    await result(/PROJ-1/)
    await user.click(screen.getByRole('button', { name: 'Select all matching' }))
    await waitFor(() => {
      expect(screen.getByTestId('tracker-selection-status')).toHaveTextContent("3 selected: the first 3 matching items. That's the most")
    })
    await user.click(screen.getByRole('button', { name: 'Add 3 items' }))
    expect(onAdd.mock.calls[0]?.[0].selection.keys).toEqual(['PROJ-1', 'PROJ-2', 'PROJ-3'])
  })

  it('FR167_a_missing_issues_permission_shows_the_spec_copy_and_the_settings_link', async () => {
    seedTrackerStatus('github', { ready: true, reason: null, connections: [{ id: 'g1', label: 'ada', status: 'valid', accountName: 'ada', detail: null }] })
    trackersDb.permissionMissing = 'https://github.com/apps/aide'
    signInAs({ systemRole: 'user' })
    renderWithProviders(<TrackerBrowseDialog capacity={200} onClose={vi.fn()} onAdd={vi.fn()} />)
    expect(await screen.findByText('The AIDE GitHub App needs the Issues permission. Update it in GitHub settings.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open the GitHub App settings/ })).toHaveAttribute('href', 'https://github.com/apps/aide')
  })

  it('FR169_item_links_open_safely_in_a_new_tab_and_unsafe_urls_stay_text', async () => {
    trackersDb.items = [trackerItem('PROJ-1'), trackerItem('PROJ-2', { url: 'javascript:alert(1)' })]
    const { user } = setup()
    await chooseProject(user)
    const link = await screen.findByRole('link', { name: /PROJ-1/ })
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
    expect(link).toHaveAttribute('target', '_blank')
    expect(screen.queryByRole('link', { name: /PROJ-2/ })).not.toBeInTheDocument()
  })
})
