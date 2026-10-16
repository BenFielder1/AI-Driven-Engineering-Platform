import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { ApiError, restoreNotAllowedMessage } from '../../lib/errors'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { openArtifact, openArtifacts, seedProjectAs, specV1, specV2, specV3 } from '../../test/artifacts'
import { requestsTo } from '../../test/project-settings'
import { restoreBlockReason } from '../stages/stage-model'
import { restoreBlockMessage, restoreErrorMessage } from './restore-copy'

/** FR-58 example: spec v2 (locked) and v3 (locked, current), the project still in Specification. */
function specHistory(role: 'owner' | 'contributor' | 'viewer', setup?: (p: StoredProject) => void) {
  return seedProjectAs(role, (p) => {
    seedArtifact(p, { type: 'technical_specification', content: specV1, status: 'locked' })
    seedArtifact(p, { type: 'technical_specification', content: specV2, status: 'locked' })
    seedArtifact(p, { type: 'technical_specification', content: specV3, status: 'locked' })
    setup?.(p)
  })
}

describe('Restore an artifact version (FR-58)', () => {
  it('FR58_writer_restores_v2_as_draft_v4_after_a_diff_preview', async () => {
    const project = specHistory('contributor')
    const { user, router } = await openArtifact(project, 'technical_specification', 2)

    await user.click(screen.getByRole('button', { name: 'Restore this version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Restore technical specification v2?' })
    expect(dialog).toHaveTextContent('A new draft (v4) is created with v2\'s content, based on the latest version v3.')
    // The preview diffs the latest (v3) against the chosen version (v2).
    expect(await within(dialog).findByRole('table', { name: /^Changes from v3 to v2/ })).toBeInTheDocument()

    await user.click(within(dialog).getByRole('button', { name: 'Restore v2' }))
    expect(await screen.findByText('Restored v2 as draft v4. It now needs approval.')).toBeInTheDocument()
    expect(requestsTo('POST', '/artifacts/technical_specification/versions/2/restore')).toHaveLength(1)

    // The new draft is shown: v2's content and hash, parent v3, restored from v2.
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ version: 4 })
    })
    const article = await screen.findByRole('article', { name: 'Technical specification v4' })
    expect(article).toHaveTextContent('GET /items/{id}')
    const v4 = project.artifacts.find((a) => a.type === 'technical_specification' && a.version === 4)
    const v2 = project.artifacts.find((a) => a.type === 'technical_specification' && a.version === 2)
    expect(v4).toMatchObject({ status: 'draft', parentVersion: 3, restoredFromVersion: 2, contentHash: v2?.contentHash })
    expect(screen.getByText('Restored from').nextElementSibling).toHaveTextContent('v2')
    // History marks it too.
    const history = screen.getByRole('table', { name: 'Version history, newest first' })
    expect(within(history).getByText('restored from v2')).toBeInTheDocument()
  })

  it('FR58_stage_completed_hides_the_action_and_explains', async () => {
    const project = specHistory('owner', (p) => {
      p.currentPhase = 'planning'
    })
    await openArtifact(project, 'technical_specification', 2)

    expect(screen.queryByRole('button', { name: 'Restore this version' })).not.toBeInTheDocument()
    expect(screen.getByText('Specification is complete. Roll back the project to change it.')).toBeInTheDocument()
  })

  it('FR58_latest_version_and_viewers_have_no_restore', async () => {
    const latest = specHistory('owner')
    const { unmount } = await openArtifact(latest, 'technical_specification', 3)
    expect(screen.queryByRole('button', { name: 'Restore this version' })).not.toBeInTheDocument()
    unmount()

    const viewer = specHistory('viewer')
    await openArtifact(viewer, 'technical_specification', 2)
    expect(screen.queryByRole('button', { name: 'Restore this version' })).not.toBeInTheDocument()
    expect(screen.queryByText(/can't be restored/)).not.toBeInTheDocument()
  })

  it('FR58_server_refusal_shows_the_reason_copy', async () => {
    const project = specHistory('owner')
    const { user } = await openArtifact(project, 'technical_specification', 1)
    // The release started after the page loaded: the API refuses with reason `releasing`.
    project.status = 'releasing'

    await user.click(screen.getByRole('button', { name: 'Restore this version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Restore technical specification v1?' })
    await user.click(within(dialog).getByRole('button', { name: 'Restore v1' }))
    expect(await within(dialog).findByText(restoreNotAllowedMessage('releasing', 'Specification'))).toBeInTheDocument()
  })

  it('FR58_restore_rules_and_reason_copy', () => {
    const project = { currentPhase: 'specification', status: 'active', developmentVersion: '1.2.0' } as const
    const version = { integrity: 'ok', releaseVersion: '1.2.0' } as const
    expect(restoreBlockReason({ type: 'technical_specification', project, readOnly: false, version })).toBeNull()
    expect(restoreBlockReason({ type: 'technical_specification', project: { ...project, currentPhase: 'planning' }, readOnly: false, version })).toBe(
      'stage_completed',
    )
    expect(restoreBlockReason({ type: 'technical_specification', project, readOnly: false, version: { ...version, releaseVersion: '1.1.0' } })).toBe(
      'other_release_version',
    )
    expect(restoreBlockReason({ type: 'technical_specification', project, readOnly: true, version })).toBe('other_release_version')
    expect(restoreBlockReason({ type: 'technical_specification', project: { ...project, status: 'releasing' }, readOnly: false, version })).toBe(
      'releasing',
    )
    expect(restoreBlockReason({ type: 'technical_specification', project, readOnly: false, version: { ...version, integrity: 'corrupted' } })).toBe(
      'corrupted',
    )
    expect(restoreBlockReason({ type: 'optimization_plan', project: { ...project, currentPhase: 'review' }, readOnly: false, version })).toBeNull()
    // Before the producing phase: stage_not_reached; the codebase summary is never restorable.
    expect(restoreBlockReason({ type: 'implementation_plan', project, readOnly: false, version })).toBe('stage_not_reached')
    expect(restoreBlockReason({ type: 'codebase_summary', project: { ...project, currentPhase: 'refinement' }, readOnly: false, version })).toBe(
      'not_restorable',
    )
    expect(restoreBlockMessage('stage_not_reached', 'implementation_plan')).toBe("Planning hasn't been reached yet in this version.")
    expect(restoreBlockMessage('not_restorable', 'codebase_summary')).toMatch(/generated by the Repository Analyst and locked automatically/)

    const refused = new ApiError({ status: 409, errorCode: 'RESTORE_NOT_ALLOWED', reason: 'stage_completed' })
    expect(restoreErrorMessage(refused, 'implementation_plan')).toBe('Planning is complete. Roll back the project to change it.')
    const early = new ApiError({ status: 409, errorCode: 'RESTORE_NOT_ALLOWED', reason: 'stage_not_reached' })
    expect(restoreErrorMessage(early, 'implementation_plan')).toBe("Planning hasn't been reached yet in this version.")
    const latest = new ApiError({ status: 400, errorCode: 'VALIDATION' })
    expect(restoreErrorMessage(latest, 'implementation_plan')).toBe('This is already the latest version, so there is nothing to restore.')
  })
})

/** FR-59 example: a project at 1.2.0 with a spec from 1.1.0. */
function iteratedProject() {
  const owner = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
  const p = seedProject({ name: 'Shop', currentPhase: 'specification', members: [{ userId: owner.id, role: 'owner' }] })
  const rows = seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
  const past = rows.find((v) => v.version === '1.1.0')
  const current = rows.find((v) => v.version === '1.2.0')
  if (!past || !current) {
    throw new Error('seed')
  }
  seedArtifact(p, { type: 'technical_specification', content: specV1, status: 'locked', releaseVersionId: past.id })
  seedArtifact(p, { type: 'technical_specification', content: specV3, releaseVersionId: current.id })
  return p
}

describe('Artifacts across release versions (FR-59)', () => {
  it('FR59_cards_show_the_selected_release_version_and_the_filter_switches_it', async () => {
    const project = iteratedProject()
    const { user, router } = await openArtifacts(project)

    expect(screen.getByText(/The latest version of each artifact in release version/)).toHaveTextContent('1.2.0')
    const spec = screen.getByRole('region', { name: 'Technical specification' })
    expect(spec).toHaveTextContent('Version 2')

    await user.selectOptions(screen.getByLabelText('Release version'), screen.getByRole('option', { name: /^1\.1\.0/ }))
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ release: '1.1.0' })
    })
    expect(await screen.findByTestId('read-only-banner')).toHaveTextContent('Viewing 1.1.0')
    await waitFor(() => {
      expect(screen.getByRole('region', { name: 'Technical specification' })).toHaveTextContent('Version 1')
    })
  })

  it('FR59_detail_names_each_versions_release_version_and_diffs_across_release_versions', async () => {
    const project = iteratedProject()
    const { user } = await openArtifact(project, 'technical_specification')

    expect(screen.getByText('Release version 1.2.0')).toBeInTheDocument()
    const history = screen.getByRole('table', { name: 'Version history, newest first' })
    expect(within(history).getByRole('row', { name: /^v2/ })).toHaveTextContent('1.2.0')

    // The compare picker offers versions of every release version (allReleaseVersions).
    const compare = screen.getByRole('form', { name: 'Compare versions' })
    const from = within(compare).getByLabelText('From version')
    await waitFor(() => {
      expect(within(from).getByRole('option', { name: 'v1 · Approved · 1.1.0' })).toBeInTheDocument()
    })
    await user.selectOptions(from, '1')
    await user.selectOptions(within(compare).getByLabelText('To version'), '2')
    await user.click(within(compare).getByRole('button', { name: 'Compare' }))
    const diff = await screen.findByRole('table', { name: /^Changes from v1 to v2/ })
    expect(diff).toHaveTextContent('Track stock levels across warehouses.')
  })
})
