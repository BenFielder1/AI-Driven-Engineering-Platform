import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { ProjectRole } from '../../lib/permissions'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { lockArtifact, seedGate } from '../../mocks/handlers/lifecycle'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { renderRoute } from '../../test/render'

/** A project in planning with a pending plan_approval gate, seen by `role` on `section`. */
function openPlanningWithGate(role: ProjectRole, section: string) {
  const members: { userId: string; role: ProjectRole }[] = []
  if (role !== 'owner') {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    members.push({ userId: owner.id, role: 'owner' })
  }
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  members.push({ userId: me.id, role })
  const project: StoredProject = seedProject({ name: 'Inventory tracker', currentPhase: 'planning', members })
  lockArtifact(project, 'project_request')
  lockArtifact(project, 'technical_specification')
  seedGate(project, { type: 'plan_approval', artifactType: 'implementation_plan', artifactVersion: 1 })
  return { project, ...renderRoute(`/projects/${project.id}/${section}`) }
}

const stepper = () => screen.findByRole('navigation', { name: 'Lifecycle stages' })

describe('FR-54: project header (stage stepper, next action)', () => {
  it('FR54_header: planning + pending plan_approval → stepper ✓ ✓ current(1), and the Owner gets "Approve plan"', async () => {
    const { project, router } = openPlanningWithGate('owner', 'artifacts')

    const nav = await stepper()
    await waitFor(() => {
      expect(nav.querySelector('[data-stage-state="current"]')).toHaveTextContent('Planning')
    })
    const links = within(nav).getAllByRole('link')
    expect(links.map((l) => l.closest('li')?.getAttribute('data-stage-state'))).toEqual([
      'done',
      'done',
      'current',
      'upcoming',
      'upcoming',
      'upcoming',
    ])
    // The current stage's state and its "1" (the pending plan_approval gate) are in the link for assistive tech.
    const planning = links[2]
    if (!planning) {
      throw new Error('No Planning stage')
    }
    expect(planning).toHaveAccessibleName(/Planning\s*\(current stage, 1 awaiting action\)/)
    expect(within(planning).getByTestId('stepper-needs-action')).toHaveTextContent('1')
    // Later stages stay navigable.
    expect(within(nav).getByRole('link', { name: /^Release/ })).toHaveAttribute('href', `/projects/${project.id}/release`)

    const action = await screen.findByRole('link', { name: 'Approve plan' })
    // FR-55: the plan gate is decided in place on the Planning stage page.
    expect(action).toHaveAttribute('href', `/projects/${project.id}/planning#stage-gates`)
    await userEvent.setup().click(action)
    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/projects/${project.id}/planning`)
    })
    expect(router.state.location.hash).toBe('stage-gates')
  })

  it('FR54_header: a Contributor sees the step disabled with the reason; a Viewer sees no action', async () => {
    openPlanningWithGate('contributor', 'artifacts')
    const disabled = await screen.findByRole('button', { name: 'Approve plan' })
    expect(disabled).toBeDisabled()
    expect(disabled).toHaveAccessibleDescription('Waiting for a project Owner to approve the plan.')
  })

  it('FR54_header: a Viewer gets no next-action button', async () => {
    openPlanningWithGate('viewer', 'artifacts')
    await stepper()
    await screen.findByRole('heading', { level: 1, name: 'Inventory tracker' })
    expect(screen.queryByTestId('next-action')).not.toBeInTheDocument()
  })

  it('FR54_header: Previous / Next stage follow the stage on screen; the first stage has no previous', async () => {
    const { project } = openPlanningWithGate('owner', 'refinement')
    await stepper()
    expect(await screen.findByRole('button', { name: 'Previous stage' })).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Next stage: Specification' })).toHaveAttribute(
      'href',
      `/projects/${project.id}/specification`,
    )
  })

  it('FR53_live: the shell header shows the live indicator on project pages only', async () => {
    openPlanningWithGate('owner', 'artifacts')
    const live = await screen.findByTestId('live-indicator')
    // In the sticky shell header (SPEC 7.1), not in the page.
    expect(live.closest('header')).toHaveClass('sticky')
    expect(live.closest('main')).toBeNull()
  })
})
