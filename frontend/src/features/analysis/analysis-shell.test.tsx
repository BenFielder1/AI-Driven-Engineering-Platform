import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedInference } from '../../mocks/handlers/analysis'
import { makeReleaseVersion, projectsDb, seedProject } from '../../mocks/handlers/projects'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { seedProjectAs } from '../../test/artifacts'
import { renderRoute } from '../../test/render'
import { describeActivity } from '../activity/activity-summary'
import { projectSections } from '../shell/nav-config'
import { stageStatuses } from '../shell/stage-states'
import { adjacentStages } from '../lifecycle/stage-nav'

const lifecycleLabels = (analysisEnabled: boolean) => projectSections({ analysisEnabled })[0]?.items.map((i) => i.label)

describe('Shell: Analysis in the nav and the lifecycle tracker (FR-182, §4.27)', () => {
  it('FR182_nav_lists_analysis_first_only_for_a_version_with_existing_code', () => {
    expect(lifecycleLabels(true)).toEqual(['Analysis', 'Refinement', 'Specification', 'Planning', 'Execution', 'Review', 'Release'])
    expect(lifecycleLabels(false)).not.toContain('Analysis')
  })

  it('FR179_tracker_shows_analysis_only_on_inference_versions_and_it_comes_first', () => {
    const state = { phase: 'analysis' as const, status: 'active' as const, pendingGates: [], artifacts: [] }
    const inference = stageStatuses(state, { analysis: true })
    expect(inference.map((s) => s.stage)).toEqual(['analysis', 'refinement', 'specification', 'planning', 'execution', 'review', 'release'])
    expect(inference[0]).toMatchObject({ stage: 'analysis', state: 'current' })
    expect(inference[1]).toMatchObject({ state: 'upcoming' })
    expect(stageStatuses({ ...state, phase: 'refinement' }, { analysis: true })[0]).toMatchObject({ stage: 'analysis', state: 'done' })
    expect(stageStatuses({ ...state, phase: 'refinement' }).map((s) => s.stage)).not.toContain('analysis')
    // A stack_profile draft counts as Analysis's pending work.
    const drafted = stageStatuses(
      { ...state, artifacts: [{ type: 'stack_profile', latestVersion: 1, latestStatus: 'draft', currentLockedVersion: null }] },
      { analysis: true },
    )
    expect(drafted[0]?.needsAction).toBe(1)
    expect(adjacentStages('refinement', { analysis: true }).previous).toBe('analysis')
    expect(adjacentStages('refinement').previous).toBeNull()
  })

  it('FR182_sidebar_and_stepper_show_analysis_for_an_inference_version', async () => {
    const project = seedProjectAs('owner', (p) => {
      seedInference(p)
    })
    await renderRoute(`/projects/${project.id}/analysis`)
    await screen.findByRole('heading', { level: 2, name: 'Analysis' })

    const nav = screen.getByRole('navigation', { name: 'Project sections' })
    expect(await within(nav).findByRole('link', { name: 'Analysis' })).toHaveAttribute('href', `/projects/${project.id}/analysis`)
    const stepper = await screen.findByTestId('stage-stepper')
    await waitFor(() => {
      expect(within(stepper).getAllByRole('link')[0]).toHaveTextContent('Analysis')
    })
    expect(within(stepper).getAllByRole('link')[0]).toHaveAttribute('aria-current', 'page')
  })

  it('FR182_a_non_inference_version_with_code_has_the_nav_item_but_no_tracker_step', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'refinement'
      p.releaseVersions = [
        makeReleaseVersion({ version: '1.1.0', status: 'released' }),
        makeReleaseVersion({ version: '1.2.0', baseTag: 'v1.1.0', baseCommitSha: 'abc1234def5678' }),
      ]
    })
    await renderRoute(`/projects/${project.id}/refinement`)
    await screen.findByRole('heading', { level: 2, name: 'Refinement' })

    const nav = screen.getByRole('navigation', { name: 'Project sections' })
    expect(await within(nav).findByRole('link', { name: 'Analysis' })).toBeInTheDocument()
    const stepper = await screen.findByTestId('stage-stepper')
    expect(within(stepper).queryByRole('link', { name: /^Analysis/ })).not.toBeInTheDocument()
  })

  it('FR182_a_version_without_code_has_no_analysis_item', async () => {
    const project = seedProjectAs('owner', (p) => {
      p.currentPhase = 'refinement'
    })
    await renderRoute(`/projects/${project.id}/refinement`)
    await screen.findByRole('heading', { level: 2, name: 'Refinement' })
    const nav = screen.getByRole('navigation', { name: 'Project sections' })
    await within(nav).findByRole('link', { name: 'Refinement' })
    expect(within(nav).queryByRole('link', { name: 'Analysis' })).not.toBeInTheDocument()
  })
})

describe('Start new version → Infer (FR-186)', () => {
  it('FR186_infer_from_the_repository_starts_the_version_in_analysis', async () => {
    signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const p = seedProject({ name: 'Shop' })
    seedReleaseVersions(p, { released: '1.1.0', history: ['1.0.0'] })
    await renderRoute(`/projects/${p.id}/release`)
    await screen.findAllByRole('heading', { level: 2, name: 'Release' })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Start new version' }))
    const dialog = await screen.findByRole('dialog', { name: 'Start a new version' })
    await user.type(within(dialog).getByLabelText('What should this version add or fix?'), 'Move to React')
    await user.click(await within(dialog).findByRole('button', { name: 'Infer from the repository' }))
    expect(within(dialog).getByTestId('start-version-stack')).toHaveTextContent('Inferred from the repository on the Analysis page')
    expect(within(dialog).getByRole('region', { name: 'What happens' })).toHaveTextContent('The project goes to Analysis at checkpoint 1')
    await user.click(within(dialog).getByRole('button', { name: 'Start development' }))

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Start a new version' })).not.toBeInTheDocument()
    })
    const body = projectsDb.requests.find((r) => r.method === 'POST' && r.path.endsWith('/versions'))?.body
    expect(body).toEqual({ version: '1.2.0', request: 'Move to React', stackSource: 'infer' })
    expect(p.currentPhase).toBe('analysis')
  })
})

describe('Activity copy (FR-193)', () => {
  it('FR180_FR183_stack_profile_events_are_worded', () => {
    const d = (type: string, payload: Record<string, unknown>) => describeActivity({ type, payload, summary: 'server' })
    expect(d('STACK_PROFILE_CREATED', { version: 1, unresolvedLayers: ['backend'] })).toBe(
      'Stack profile v1 drafted by the Stack Analyst; needs input on Backend',
    )
    expect(d('STACK_PROFILE_CREATED', { version: 2, unresolvedLayers: [] })).toBe('Stack profile v2 drafted by the Stack Analyst')
    expect(d('STACK_PROFILE_APPROVED', { version: 2, generalist: false })).toBe('Stack profile v2 approved; the stack is pinned')
    expect(d('STACK_PROFILE_APPROVED', { version: 3, generalist: true })).toBe('Stack profile v3 approved with No stack (general)')
    expect(d('STACK_PROFILE_REJECTED', { version: 1, rerun: true })).toBe('Stack profile v1 rejected; the Stack Analyst runs again')
    expect(d('PHASE_CHANGED', { from: 'analysis', to: 'refinement', checkpoint: 2 })).toContain('Analysis')
  })
})
