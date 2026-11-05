// FR-197: copy that names the product says Tevrin (it said AIDE before v6); the assertions below use the new copy.
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedArtifact } from '../../mocks/handlers/artifacts'
import { seedPlatformConnection } from '../../mocks/handlers/platform-connections'
import { authDb } from '../../mocks/handlers/auth'
import type { StoredProject } from '../../mocks/handlers/projects'
import { seedDrift, seedSourceImport, seedSourceState, trackerItem } from '../../mocks/handlers/trackers'
import { openArtifact, seedProjectAs } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'
import type { ProjectRole } from '../../lib/permissions'

function seed(role: ProjectRole, setup?: (p: StoredProject) => void, phase: StoredProject['currentPhase'] = 'refinement') {
  return seedProjectAs(role, (p) => {
    p.currentPhase = phase
    setup?.(p)
  })
}

async function open(project: StoredProject) {
  await renderRoute(`/projects/${project.id}/refinement`)
  const panel = await screen.findByRole('region', { name: 'Source items' })
  await waitFor(() => {
    expect(within(panel).queryByText('Loading the source items…')).not.toBeInTheDocument()
  })
  return { panel, user: userEvent.setup() }
}

describe('Refinement → Source items (FR-171, FR-173)', () => {
  it('FR173_opening_the_page_checks_drift_and_shows_it_per_item', async () => {
    const project = seed('owner', (p) => {
      const row = seedSourceImport(p.id, { items: [trackerItem('PROJ-1'), trackerItem('PROJ-2'), trackerItem('PROJ-3'), trackerItem('PROJ-4')] })
      seedDrift(p.id, row.id, 'PROJ-1', 'changed')
      seedDrift(p.id, row.id, 'PROJ-2', 'removed')
      seedDrift(p.id, row.id, 'PROJ-3', 'unknown')
    })
    const { panel } = await open(project)
    const items = await within(panel).findAllByTestId('source-item')
    const first = items[0]
    if (!first) {
      throw new Error('no items')
    }
    await within(first).findByText('Changed')
    expect(items[1]).toHaveTextContent('Removed')
    expect(items[2]).toHaveTextContent('Unknown')
    expect(items[3]).toHaveTextContent('Unchanged')
    expect(requestsTo('POST', '/sources/check')).toHaveLength(1)
  })

  it('FR171_a_failed_import_shows_why_and_retry_requeues_it', async () => {
    let id = ''
    const project = seed('contributor', (p) => {
      id = seedSourceImport(p.id, { status: 'failed', errorCode: 'TRACKER_IMPORT_FAILED', errorMessage: 'The import from Jira failed: no access.', items: [], itemCount: 0 }).id
    })
    const { panel, user } = await open(project)
    expect(within(panel).getByRole('alert')).toHaveTextContent('The import from Jira failed: no access.')
    await user.click(within(panel).getByRole('button', { name: 'Retry Jira import' }))
    await waitFor(() => {
      expect(requestsTo('POST', `/sources/${id}/reimport`)).toHaveLength(1)
    })
    expect(await within(panel).findByRole('listitem', { name: 'Jira import, Queued' })).toBeInTheDocument()
  })

  it('FR173_retry_is_gone_after_submitting_and_while_a_repeat_is_pending', async () => {
    const project = seed('owner', (p) => {
      seedSourceImport(p.id, { status: 'failed', errorCode: 'TRACKER_IMPORT_FAILED', items: [], itemCount: 0 })
    })
    const { panel, user } = await open(project)
    await user.click(within(panel).getByRole('button', { name: 'Retry Jira import' }))
    expect(await within(panel).findByText('Retry queued.')).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Retry Jira import' })).not.toBeInTheDocument()
    await waitFor(() => {
      expect(requestsTo('POST', '/reimport')).toHaveLength(1)
    })
  })

  it('FR173_an_import_with_a_pending_repeat_offers_no_retry_on_a_fresh_page', async () => {
    const selection = { keys: ['PROJ-1'], containerId: 'PROJ' }
    const project = seed('owner', (p) => {
      seedSourceImport(p.id, { status: 'failed', errorCode: 'TRACKER_IMPORT_FAILED', selection, items: [], itemCount: 0 })
      seedSourceImport(p.id, { status: 'running', selection, items: [], itemCount: 0, completedAt: null })
    })
    const { panel } = await open(project)
    expect(within(panel).getByText('Retry queued.')).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Retry Jira import' })).not.toBeInTheDocument()
  })

  it('FR173_a_refused_duplicate_retry_is_handled_quietly', async () => {
    const project = seed('owner', (p) => {
      seedSourceImport(p.id, { status: 'failed', errorCode: 'TRACKER_IMPORT_FAILED', errorMessage: 'Failed.', items: [], itemCount: 0 })
    })
    const { server } = await import('../../mocks/server')
    const { http } = await import('msw')
    const { problem } = await import('../../mocks/handlers/problem')
    server.use(http.post('*/api/projects/:id/sources/:importId/reimport', () => problem(409, 'CONFLICT', 'Already queued.')))
    const { panel, user } = await open(project)
    await user.click(within(panel).getByRole('button', { name: 'Retry Jira import' }))
    expect(await within(panel).findByText('Retry queued.')).toBeInTheDocument()
    expect(within(panel).queryByText("That didn't work")).not.toBeInTheDocument()
  })

  it('FR170_older_source_items_versions_offer_neither_restore_nor_edit', async () => {
    const project = seed('owner', (p) => {
      seedArtifact(p, { type: 'source_items', content: '# v1', status: 'superseded' })
      seedArtifact(p, { type: 'source_items', content: '# v2', status: 'locked' })
    })
    await openArtifact(project, 'source_items', 1)
    expect(await screen.findByText(/snapshots Tevrin writes and locks with each import/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Restore/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit as new draft' })).not.toBeInTheDocument()
  })

  it('FR70_the_codebase_summary_offers_no_edit_either', async () => {
    const project = seed('owner', (p) => {
      seedArtifact(p, { type: 'codebase_summary', content: '# Summary', status: 'locked' })
    })
    await openArtifact(project, 'codebase_summary')
    expect(screen.queryByRole('button', { name: 'Edit as new draft' })).not.toBeInTheDocument()
  })

  it('FR170_a_rate_limited_import_says_aide_will_retry', async () => {
    const project = seed('owner', (p) => {
      seedSourceImport(p.id, { provider: 'linear', status: 'queued', errorCode: 'TRACKER_RATE_LIMITED', items: [], itemCount: 0, completedAt: null })
    })
    const { panel } = await open(project)
    expect(within(panel).getByRole('status')).toHaveTextContent('Linear is rate limiting requests. Tevrin will retry.')
    expect(within(panel).queryByRole('button', { name: /Remove/ })).not.toBeInTheDocument()
  })

  it('FR173_remove_asks_first_and_says_earlier_snapshots_are_kept', async () => {
    let id = ''
    const project = seed('owner', (p) => {
      id = seedSourceImport(p.id).id
    })
    const { panel, user } = await open(project)
    await user.click(within(panel).getByRole('button', { name: 'Remove Jira import' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove this import?' })
    expect(dialog).toHaveTextContent('Earlier locked snapshots are kept.')
    await user.click(within(dialog).getByRole('button', { name: 'Remove import' }))
    await waitFor(() => {
      expect(requestsTo('DELETE', `/sources/${id}`)).toHaveLength(1)
    })
    await waitFor(() => {
      expect(within(panel).getByText('No tracker items were imported for this version.')).toBeInTheDocument()
    })
  })

  it('FR171_add_items_posts_the_selection', async () => {
    const project = seed('owner')
    const me = authDb.currentUserId
    if (!me) {
      throw new Error('signed in')
    }
    seedPlatformConnection({ userId: me, provider: 'jira', label: 'Acme Jira' })
    const { panel, user } = await open(project)
    await user.click(within(panel).getByRole('button', { name: 'Add items' }))
    const dialog = await screen.findByRole('dialog', { name: 'Import from tracker' })
    const select = await within(dialog).findByLabelText('Choose a project')
    await within(select).findByRole('option', { name: /Project One/ })
    await user.selectOptions(select, 'PROJ')
    await user.click(await within(dialog).findByRole('checkbox', { name: /PROJ-2 Sign up/ }))
    await user.click(within(dialog).getByRole('button', { name: 'Add 1 item' }))

    await waitFor(() => {
      expect(requestsTo('POST', '/sources')[0]?.body).toMatchObject({ provider: 'jira', selection: { keys: ['PROJ-2'] } })
    })
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Import from tracker' })).not.toBeInTheDocument()
    })
  })

  it('FR171_after_refinement_controls_are_gone_and_the_reason_is_written', async () => {
    const project = seed('owner', (p) => {
      seedSourceImport(p.id)
    }, 'specification')
    const { panel } = await open(project)
    expect(within(panel).queryByRole('button', { name: 'Add items' })).not.toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: /Re-import/ })).not.toBeInTheDocument()
    expect(within(panel).getByText(/can't change in this phase/)).toBeInTheDocument()
  })

  it('FR171_viewers_see_the_imports_without_controls_and_why', async () => {
    const project = seed('viewer', (p) => {
      seedSourceImport(p.id)
    })
    const { panel } = await open(project)
    expect(within(panel).queryByRole('button', { name: 'Add items' })).not.toBeInTheDocument()
    expect(within(panel).getByText('Only Contributors and Owners can change the source items.')).toBeInTheDocument()
  })

  it('FR173_a_new_snapshot_shows_the_banner_and_approving_the_old_draft_asks_first', async () => {
    let artifactId = ''
    const project = seed('owner', (p) => {
      const draft = seedArtifact(p, { type: 'project_request', content: '# Draft' })
      artifactId = draft.id
      seedSourceImport(p.id)
      seedSourceState(p.id, {
        currentSnapshot: { artifactId: 's2', version: 2, contentHash: 'abc', createdAt: '2026-10-01T09:00:00Z' },
        draft: { artifactId, version: draft.version, builtFromSnapshotVersion: 1, behindSnapshot: true },
      })
    })
    const { panel, user } = await open(project)
    expect(within(panel).getByText('Sources changed since this draft: run another round')).toBeInTheDocument()
    expect(within(panel).getByRole('link', { name: 'Read the snapshot' })).toHaveAttribute('href', expect.stringContaining('/artifacts/source_items'))

    await user.click(await screen.findByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: /Approve project request/ })
    expect(within(dialog).getByText('Sources changed since this draft')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Approve anyway' }))
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: /Approve project request/ })).not.toBeInTheDocument()
    })
    expect(requestsTo('POST', `/artifacts/${artifactId}/approve`).length + requestsTo('POST', '/approve').length).toBeGreaterThan(0)
  })

  it('FR173_a_failed_drift_check_leaves_the_page_working', async () => {
    const project = seed('owner', (p) => {
      seedSourceImport(p.id)
    })
    const { server } = await import('../../mocks/server')
    const { http } = await import('msw')
    const { problem } = await import('../../mocks/handlers/problem')
    server.use(http.post('*/api/projects/:id/sources/check', () => problem(404, 'NOT_FOUND', 'Gone.')))
    const { panel } = await open(project)
    expect(await within(panel).findByText(/couldn't check the items/)).toBeInTheDocument()
    expect(within(panel).getByRole('listitem', { name: 'Jira import, Imported' })).toBeInTheDocument()
  })
})

describe('Specification → uncovered items (FR-172)', () => {
  it('FR172_lists_the_uncovered_keys_linked_to_the_tracker', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'specification'
      seedArtifact(p, {
        type: 'technical_specification',
        content: '# Spec',
        warnings: [{ code: 'UNCOVERED_SOURCE_ITEMS', message: 'Not cited.', ids: ['PROJ-1', 'PROJ-9'] }],
      })
      seedSourceImport(p.id, { items: [trackerItem('PROJ-1')] })
    })
    await renderRoute(`/projects/${project.id}/specification`)
    const list = await screen.findByRole('list', { name: 'Imported items not cited by any requirement' })
    const link = await within(list).findByRole('link', { name: /PROJ-1/ })
    expect(link).toHaveAttribute('href', 'https://acme.atlassian.net/browse/PROJ-1')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
    expect(within(list).getByText('PROJ-9')).toBeInTheDocument()
  })
})
