import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { errorMessages } from '../../lib/errors'
import { authDb } from '../../mocks/handlers/auth'
import { seedArtifact } from '../../mocks/handlers/artifacts'
import { seedGate } from '../../mocks/handlers/lifecycle'
import { seedRun } from '../../mocks/handlers/runs'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import type { StoredProject } from '../../mocks/handlers/projects'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'
import { dependencyWaves } from './plan-waves'

async function openStage(project: StoredProject, stage: string, title: string) {
  const utils = renderRoute(`/projects/${project.id}/${stage}`)
  await screen.findByRole('heading', { level: 2, name: title })
  await waitFor(() => {
    expect(screen.queryByText(/^Loading the /)).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup() }
}

const stageArtifact = () => screen.getByRole('region', { name: /^(Technical specification|Implementation plan|Project request|Optimisation plan)/ })

const specWithChanges = [
  '# Shop spec',
  '',
  '## 1. Overview',
  'Sell things.',
  '',
  '## 3. Functional requirements',
  '',
  '### FR-1 [unchanged] Browse products',
  '### FR-7 [new] Wish lists',
  '### FR-4 [changed] Checkout',
  '### FR-2 [removed] Gift cards',
].join('\n')

describe('Stage pages (FR-55)', () => {
  it('FR55_spec_page_in_execution_shows_the_locked_spec_with_version_approver_and_run_and_no_actions', async () => {
    const runId = '0192a7e2-0000-7000-8000-00000000aaaa'
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'execution'
      p.status = 'building'
      const spec = seedArtifact(p, { type: 'technical_specification', content: '# Spec v1\n\nTrack stock.', status: 'locked' })
      spec.author = { user: null, agentRunId: runId }
      spec.approvedBy = { id: authDb.currentUserId, name: 'Ada Lovelace', isSystem: false }
    })
    await openStage(project, 'specification', 'Specification')

    expect(screen.getByTestId('stage-status')).toHaveTextContent('Done')
    const region = stageArtifact()
    expect(within(region).getByRole('article', { name: 'Technical specification v1' })).toHaveTextContent('Spec v1')
    expect(within(region).getByText('Current')).toBeInTheDocument()
    expect(within(region).getByText(/^Ada Lovelace,/)).toBeInTheDocument()
    expect(within(region).getByRole('link', { name: 'Agent run' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/runs?run=${runId}`,
    )
    expect(within(region).getByText(/This stage is complete/)).toBeInTheDocument()
    for (const name of ['Approve', 'Reject', 'Edit as new draft']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
    }
  })

  it('FR55_current_stage_draft_is_approved_in_place_with_latest_and_current_marked', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: 'technical_specification', content: '# Spec v1', status: 'locked' })
      seedArtifact(p, { type: 'technical_specification', content: '# Spec v2' })
    })
    const { user } = await openStage(project, 'specification', 'Specification')

    expect(screen.getByTestId('stage-status')).toHaveTextContent('Awaiting approval')
    const picker = screen.getByLabelText('Version')
    expect(within(picker).getByRole('option', { name: 'v2 · Draft (latest)' })).toBeInTheDocument()
    expect(within(picker).getByRole('option', { name: 'v1 · Approved (current)' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit as new draft' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Approve' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve technical specification v2?' })
    await user.click(within(dialog).getByRole('button', { name: 'Approve v2' }))
    expect(await screen.findByText('v2 approved and locked.')).toBeInTheDocument()
  })

  it('FR55_contributor_sees_the_draft_waiting_for_an_owner', async () => {
    const project = seedProjectAs('contributor', (p) => {
      seedArtifact(p, { type: 'technical_specification', content: '# Spec v1' })
    })
    await openStage(project, 'specification', 'Specification')
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(screen.getByText('This draft is waiting for a project Owner to approve or reject it.')).toBeInTheDocument()
  })

  it('FR55_a_stage_that_has_not_started_explains_what_produces_it_and_what_comes_first', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'refinement'
    })
    await openStage(project, 'planning', 'Planning')

    expect(screen.getByTestId('stage-status')).toHaveTextContent('Not started')
    expect(screen.getByRole('heading', { name: 'No implementation plan yet' })).toBeInTheDocument()
    expect(screen.getByText(/The Plan Generator writes the implementation plan/)).toHaveTextContent(
      'The specification has to be approved and the project advanced to Planning first.',
    )
  })

  it('FR55_spec_outline_links_to_sections_and_the_changes_filter_lists_tagged_requirements', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: 'technical_specification', content: specWithChanges })
    })
    const { user } = await openStage(project, 'specification', 'Specification')

    const outline = screen.getByRole('navigation', { name: 'Section outline' })
    const overview = within(outline).getByRole('link', { name: '1. Overview' })
    expect(overview).toHaveAttribute('href', '#md-1-overview')
    // The rendered heading carries the same id.
    expect(document.getElementById('md-1-overview')).toHaveTextContent('1. Overview')

    const changes = screen.getByRole('region', { name: 'Changes in this version' })
    const list = () => within(changes).getByRole('list', { name: 'Requirements in this filter' })
    // "All changes" leaves the unchanged requirements out.
    expect(within(list()).getAllByRole('link').map((l) => l.textContent)).toEqual(['FR-7 Wish lists', 'FR-4 Checkout', 'FR-2 Gift cards'])
    await user.click(within(changes).getByRole('button', { name: 'New (1)' }))
    expect(within(list()).getAllByRole('link').map((l) => l.textContent)).toEqual(['FR-7 Wish lists'])
    expect(within(list()).getByRole('link', { name: 'FR-7 Wish lists' })).toHaveAttribute('href', '#md-fr-7-new-wish-lists')
  })

  it('FR55_changes_filter_reads_list_item_and_paragraph_tags_with_counts (§4.13)', async () => {
    const content = [
      '# Spec',
      '',
      '## Functional Requirements',
      '',
      '- **FR-1** [unchanged] Browse products',
      '  - Given a shopper, when they browse, then products show.',
      '- **FR-3** [changed]: Export orders',
      '',
      '**FR-5** [new]: CSV import',
      '',
      '```',
      '- FR-9 [new] In a code block',
      '```',
      '',
      '### FR-6 [new] Wish lists',
    ].join('\n')
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: 'technical_specification', content })
    })
    const { user } = await openStage(project, 'specification', 'Specification')

    const changes = screen.getByRole('region', { name: 'Changes in this version' })
    const buttons = within(changes).getAllByRole('button').map((b) => b.textContent)
    expect(buttons).toEqual(['All changes (3)', 'New (2)', 'Changed (1)', 'Removed (0)', 'Unchanged (1)'])
    const links = () =>
      within(within(changes).getByRole('list', { name: 'Requirements in this filter' }))
        .getAllByRole('link')
        .map((l) => l.textContent)
    expect(links()).toEqual(['FR-3: Export orders', 'FR-5: CSV import', 'FR-6 Wish lists'])
    expect(within(changes).getByRole('link', { name: 'FR-3: Export orders' })).toHaveAttribute('href', '#md-req-fr-3')
    // The rendered list item and paragraph carry the link targets.
    expect(document.getElementById('md-req-fr-3')?.tagName).toBe('LI')
    expect(document.getElementById('md-req-fr-3')).toHaveTextContent('Export orders')
    expect(document.getElementById('md-req-fr-5')?.tagName).toBe('P')
    expect(document.getElementById('md-req-fr-9')).toBeNull()

    await user.click(within(changes).getByRole('button', { name: 'Unchanged (1)' }))
    expect(links()).toEqual(['FR-1 Browse products'])
  })

  it('FR55_spec_without_tags_has_no_changes_filter', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedArtifact(p, { type: 'technical_specification', content: '# Spec\n\n## Scope\nThings.' })
    })
    await openStage(project, 'specification', 'Specification')
    expect(screen.getByRole('navigation', { name: 'Section outline' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Changes in this version' })).not.toBeInTheDocument()
  })

  it('FR55_planning_shows_the_step_table_dependency_waves_and_coverage_warnings', async () => {
    const block = {
      format: 1,
      coverageWarnings: ['FR-9 is not covered by any step.'],
      steps: [
        { number: 1, title: 'API', agent: 'backend-engineer', covers: ['FR-1', 'FR-2'], files: [{ path: 'src/api.cs', action: 'create' }], dependencies: [] },
        { number: 2, title: 'UI', agent: 'frontend-engineer', covers: ['FR-3'], files: [{ path: 'src/ui.tsx', action: 'create' }], dependencies: [1] },
        { number: 3, title: 'Docs', files: [{ path: 'README.md', action: 'modify' }], dependencies: [1] },
      ],
    }
    const content = `# Plan\n\n\`\`\`aide-plan\n${JSON.stringify(block, null, 2)}\n\`\`\`\n`
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'planning'
      seedArtifact(p, { type: 'implementation_plan', content })
      seedGate(p, { type: 'plan_approval' })
      seedRun(p, { agent: 'plan_generator' })
    })
    await openStage(project, 'planning', 'Planning')

    const table = screen.getByRole('table', { name: /Plan steps/ })
    const row = within(table).getByRole('row', { name: /^1 API/ })
    expect(row).toHaveTextContent('backend-engineer')
    expect(row).toHaveTextContent('src/api.cs')
    expect(row).toHaveTextContent('FR-1, FR-2')
    expect(within(table).getByRole('row', { name: /^3 Docs/ })).toHaveTextContent('Code Generator')
    expect(within(screen.getByRole('list', { name: 'Dependency waves' })).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'Wave 1: step 1',
      'Wave 2: steps 2, 3 (after wave 1)',
    ])
    expect(screen.getByText('FR-9 is not covered by any step.')).toBeInTheDocument()

    // The stage's gate is decided in place, and its agent runs are listed.
    const gates = screen.getByRole('region', { name: 'Gates' })
    expect(within(gates).getByRole('listitem', { name: 'Plan approval' })).toBeInTheDocument()
    expect(within(gates).getByRole('button', { name: 'Approve' })).toBeInTheDocument()
    const runs = await screen.findByRole('region', { name: 'Agent runs' })
    expect(await within(runs).findByRole('link', { name: /^Plan generator run/ })).toBeInTheDocument()
  })

  it('FR76_planning_shows_the_uncovered_requirements_warning_with_its_ids (§4.13)', async () => {
    const block = { format: 1, steps: [{ number: 1, title: 'API', covers: ['FR-1'], files: [{ path: 'src/api.cs', action: 'create' }], dependencies: [] }] }
    const content = `# Plan\n\n\`\`\`aide-plan\n${JSON.stringify(block)}\n\`\`\`\n`
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'planning'
      seedArtifact(p, {
        type: 'implementation_plan',
        content,
        warnings: [{ code: 'UNCOVERED_REQUIREMENTS', message: 'FR-3, FR-5 are not covered.', ids: ['FR-3', 'FR-5'] }],
      })
    })
    await openStage(project, 'planning', 'Planning')

    expect(await screen.findByText('Uncovered requirements')).toBeInTheDocument()
    expect(screen.getByText(errorMessages.UNCOVERED_REQUIREMENTS)).toBeInTheDocument()
    const ids = screen.getByRole('list', { name: 'Requirements not covered by any step' })
    expect(within(ids).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['FR-3', 'FR-5'])
  })

  it('FR55_dependency_waves_follow_the_latest_dependency', () => {
    expect(
      dependencyWaves([
        { number: 1, dependencies: [] },
        { number: 2, dependencies: [1] },
        { number: 3, dependencies: [] },
        { number: 4, dependencies: [2, 3] },
      ]),
    ).toEqual([[1, 3], [2], [4]])
  })

  it('FR55_release_page_decides_the_pre_release_gate_in_place_and_shows_the_production_deployment', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'release'
      p.status = 'active'
      seedGate(p, { type: 'pre_release' })
    })
    await openStage(project, 'release', 'Release')

    expect(screen.getByTestId('stage-status')).toHaveTextContent('Awaiting approval')
    const gates = screen.getByRole('region', { name: 'Gates' })
    expect(within(gates).getByRole('listitem', { name: 'Release approval' })).toBeInTheDocument()
    expect(within(gates).getByRole('button', { name: 'Approve' })).toBeInTheDocument()
    // FR-84, FR-64, FR-91: merge readiness, the GitHub Release and the production deployment are real panels.
    expect(screen.getByRole('region', { name: 'Merge readiness' })).not.toHaveTextContent('Not available yet')
    expect(screen.getByRole('region', { name: 'GitHub Release' })).not.toHaveTextContent('Not available yet')
    expect(screen.getByRole('region', { name: 'Production deployment' })).toHaveTextContent("Deployment isn't set up")
    // FR-60: Owners get Start new version (disabled with the reason until released and merged).
    expect(screen.getByRole('button', { name: /Start new version/ })).toBeInTheDocument()
  })

  it('FR55_review_page_has_the_preview_deployment_panel', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'review'
      p.status = 'reviewing'
    })
    renderRoute(`/projects/${project.id}/review`)
    await screen.findByRole('heading', { level: 2, name: 'Review' })
    expect(await screen.findByRole('region', { name: 'Preview deployment' })).toHaveTextContent("Deployment isn't set up")
  })

  it('FR55_a_past_release_version_is_read_only_and_lists_only_its_own_gates_with_their_version', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'planning'
      const rows = seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
      const past = rows.find((v) => v.version === '1.1.0')
      const current = rows.find((v) => v.version === '1.2.0')
      if (!past || !current) {
        throw new Error('seed')
      }
      past.createdAt = '2026-01-01T00:00:00Z'
      current.createdAt = '2026-06-01T00:00:00Z'
      seedArtifact(p, { type: 'implementation_plan', content: '# Plan of 1.1.0', status: 'locked', releaseVersionId: past.id })
      seedGate(p, { type: 'plan_approval', status: 'approved', createdAt: '2026-03-01T00:00:00Z', decidedAt: '2026-03-02T00:00:00Z', releaseVersionId: past.id })
      seedGate(p, { type: 'plan_approval', createdAt: '2026-07-01T00:00:00Z', releaseVersionId: current.id })
    })
    renderRoute(`/projects/${project.id}/planning?release=1.1.0`)
    expect(await screen.findByTestId('read-only-banner')).toHaveTextContent('Viewing 1.1.0 (released). Read-only')
    expect(await screen.findByRole('article', { name: 'Implementation plan v1' })).toHaveTextContent('Plan of 1.1.0')
    expect(screen.getByTestId('stage-status')).toHaveTextContent('Read-only')
    expect(screen.getByText('Release version 1.1.0')).toBeInTheDocument()

    // Only 1.1.0's gate, decided, labelled with its release version; 1.2.0's pending gate isn't listed.
    const gates = screen.getByRole('region', { name: 'Gates' })
    const decided = await within(gates).findByRole('list', { name: 'Decided stage gates' })
    expect(within(decided).getAllByRole('listitem')).toHaveLength(1)
    expect(within(decided).getByTestId('gate-release-version')).toHaveTextContent('1.1.0')
    expect(within(gates).queryByRole('list', { name: 'Pending stage gates' })).not.toBeInTheDocument()
    for (const name of ['Approve', 'Reject', 'Edit as new draft']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
    }
  })

  it('FR55_the_current_version_lists_its_pending_gate_but_not_the_past_versions', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'planning'
      const rows = seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
      for (const v of rows) {
        v.createdAt = v.version === '1.1.0' ? '2026-01-01T00:00:00Z' : '2026-06-01T00:00:00Z'
      }
      const past = rows.find((v) => v.version === '1.1.0')
      seedGate(p, { type: 'plan_approval', status: 'approved', createdAt: '2026-03-01T00:00:00Z', decidedAt: '2026-03-02T00:00:00Z', releaseVersionId: past?.id ?? '' })
      seedGate(p, { type: 'plan_approval', createdAt: '2026-07-01T00:00:00Z' })
    })
    await openStage(project, 'planning', 'Planning')
    const gates = screen.getByRole('region', { name: 'Gates' })
    expect(await within(gates).findByRole('listitem', { name: 'Plan approval' })).toBeInTheDocument()
    expect(within(gates).getByRole('button', { name: 'Approve' })).toBeInTheDocument()
    expect(within(gates).queryByRole('list', { name: 'Decided stage gates' })).not.toBeInTheDocument()
  })
  it('FR55_gates_scoped_by_releaseVersionId_not_by_creation_time', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'planning'
      const rows = seedReleaseVersions(p, { released: '1.1.0', development: '1.2.0' })
      const past = rows.find((v) => v.version === '1.1.0')
      const current = rows.find((v) => v.version === '1.2.0')
      if (!past || !current) {
        throw new Error('seed')
      }
      past.createdAt = '2026-01-01T00:00:00Z'
      current.createdAt = '2026-06-01T00:00:00Z'
      // Created after 1.2.0 started, but recorded against 1.1.0: the id wins over the timestamp.
      seedGate(p, { type: 'plan_approval', status: 'approved', createdAt: '2026-08-01T00:00:00Z', decidedAt: '2026-08-02T00:00:00Z', releaseVersionId: past.id })
    })
    await openStage(project, 'planning', 'Planning')
    const gates = screen.getByRole('region', { name: 'Gates' })
    expect(await within(gates).findByText('No gates for this stage yet.')).toBeInTheDocument()
  })
})
