import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { errorMessages } from '../../lib/errors'
import { lockArtifact, seedGate } from '../../mocks/handlers/lifecycle'
import { problem } from '../../mocks/handlers/problem'
import { signInAs } from '../../mocks/handlers/auth'
import { projectsDb, seedProject } from '../../mocks/handlers/projects'
import { server } from '../../mocks/server'
import { currentTimelineStep, openOverviewAs } from '../../test/overview'
import { requestsTo } from '../../test/project-settings'
import { renderRoute } from '../../test/render'

describe('Overview → Lifecycle (FR-11, FR-12)', () => {
  it('NFR-A11Y: the phase timeline is an ordered list of the six phases with the current one marked', async () => {
    await openOverviewAs('owner', { currentPhase: 'planning', projectVersion: 3 })

    const timeline = screen.getByRole('list', { name: 'Phase timeline' })
    expect(timeline.tagName).toBe('OL')
    const steps = within(timeline).getAllByRole('listitem')
    expect(steps.map((s) => s.textContent)).toEqual([
      '1. CompletedRefinement',
      '2. CompletedSpecification',
      '3. CurrentPlanning',
      '4. UpcomingExecution',
      '5. UpcomingReview',
      '6. UpcomingRelease',
    ])
    expect(steps.filter((s) => s.getAttribute('aria-current') === 'step')).toHaveLength(1)
    // Only one timeline on the Overview: the layout's compact one is hidden here.
    expect(screen.queryByRole('list', { name: 'Lifecycle phases' })).not.toBeInTheDocument()
    expect(currentTimelineStep()).toHaveTextContent('Planning')

    const lifecycle = screen.getByRole('region', { name: 'Lifecycle' })
    expect(within(lifecycle).getByText('Version').nextElementSibling).toHaveTextContent('3')
    expect(within(lifecycle).getByText('Status').nextElementSibling).toHaveTextContent('Active')
  })

  it('NFR-A11Y: other project sections keep the compact header timeline', async () => {
    signInAs({ email: 'ada@example.com', systemRole: 'user' })
    const project = seedProject({ currentPhase: 'execution' })
    renderRoute(`/projects/${project.id}/runs`)

    const compact = await screen.findByRole('list', { name: 'Lifecycle phases' }, { timeout: 5000 })
    expect(compact.querySelector('[aria-current="step"]')).toHaveTextContent('Execution')
  })

  it('FR-11: a failed precondition shows the reason from the server, and nothing changes', async () => {
    const { user, project } = await openOverviewAs('owner', { currentPhase: 'refinement' })

    await user.click(screen.getByRole('button', { name: 'Advance to Specification' }))

    const title = await screen.findByText(errorMessages.PHASE_PRECONDITION_FAILED)
    const alert = title.closest('[role="alert"]')
    expect(alert).not.toBeNull()
    expect(within(alert as HTMLElement).getByRole('listitem')).toHaveTextContent('The project request must be approved.')
    expect(currentTimelineStep()).toHaveTextContent('Refinement')
    expect(project.currentPhase).toBe('refinement')
    expect(project.projectVersion).toBe(1)
  })

  it('FR-12: in advisory mode, advancing from specification creates a pending spec gate and the phase is unchanged', async () => {
    const { user, project } = await openOverviewAs('contributor', { currentPhase: 'specification', approvalMode: 'advisory' }, (p) => {
      lockArtifact(p, 'technical_specification', 2)
    })

    const advance = screen.getByRole('button', { name: 'Advance to Planning' })
    await user.click(advance)

    expect(await screen.findByText('Approval required')).toBeInTheDocument()
    const pending = await screen.findByRole('list', { name: 'Pending gates' })
    expect(within(pending).getByRole('heading', { name: 'Specification approval' })).toBeInTheDocument()
    expect(within(pending).getByRole('link', { name: 'Technical specification v2' })).toBeInTheDocument()
    expect(currentTimelineStep()).toHaveTextContent('Specification')
    expect(project.currentPhase).toBe('specification')
    expect(project.gates).toHaveLength(1)
    expect(project.gates[0]?.status).toBe('pending')
    expect(requestsTo('POST', '/phase/advance')).toHaveLength(1)
  })

  it('FR-12: with no gate required (autonomous), advancing moves the phase and bumps the version', async () => {
    const { user, project } = await openOverviewAs('owner', { currentPhase: 'specification', approvalMode: 'autonomous' }, (p) => {
      lockArtifact(p, 'technical_specification')
    })

    await user.click(screen.getByRole('button', { name: 'Advance to Planning' }))

    expect(await screen.findByText('The project moved to Planning (version 2).')).toBeInTheDocument()
    await waitFor(() => {
      expect(currentTimelineStep()).toHaveTextContent('Planning')
    })
    expect(screen.getByRole('button', { name: 'Advance to Execution' })).toBeInTheDocument()
    // The layout header (GET /projects/:id) is refreshed too.
    expect(await screen.findByText('Version 2')).toBeInTheDocument()
    expect(project.projectVersion).toBe(2)
  })

  it('FR-12: GATE_PENDING is mapped when a gate is already waiting', async () => {
    const { user } = await openOverviewAs('owner', { currentPhase: 'specification' }, (p) => {
      lockArtifact(p, 'technical_specification')
      seedGate(p, { type: 'spec_approval' })
    })

    await user.click(screen.getByRole('button', { name: 'Advance to Planning' }))

    expect(await screen.findByText(errorMessages.GATE_PENDING)).toBeInTheDocument()
  })

  it('FR-11: a concurrent change (CONFLICT) is mapped, and the button shows a pending state meanwhile', async () => {
    let release: () => void = () => undefined
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.post('*/api/projects/:id/phase/advance', async () => {
        await gate
        return problem(409, 'CONFLICT', 'The project was changed by someone else.')
      }),
    )
    const { user } = await openOverviewAs('owner')

    await user.click(screen.getByRole('button', { name: 'Advance to Specification' }))
    const pendingButton = await screen.findByRole('button', { name: 'Advancing…' })
    expect(pendingButton).toBeDisabled()
    expect(pendingButton).toHaveAttribute('aria-busy', 'true')

    release()
    expect(await screen.findByText(errorMessages.CONFLICT)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Advance to Specification' })).toBeEnabled()
  })

  it('FR-12: viewers see the timeline but no advance button', async () => {
    await openOverviewAs('viewer')

    expect(screen.getByRole('list', { name: 'Phase timeline' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Advance to/ })).not.toBeInTheDocument()
  })

  it('FR-12: in the release phase there is nothing to advance to', async () => {
    await openOverviewAs('owner', { currentPhase: 'release', status: 'releasing' })

    expect(screen.queryByRole('button', { name: /^Advance to/ })).not.toBeInTheDocument()
    expect(screen.getByText('Release is the last phase.')).toBeInTheDocument()
    expect(projectsDb.requests).toHaveLength(0)
  })
})
