import { screen } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'

import { signInAs } from '../../mocks/handlers/auth'
import { seedProject } from '../../mocks/handlers/projects'
import { seedReleaseVersions } from '../../mocks/handlers/versions'
import { renderRoute } from '../../test/render'
import { installRunControlMocks, memberOf } from '../run-control/pipeline-harness.test-utils'

beforeEach(() => {
  installRunControlMocks()
})

/** The real /projects/:id/pipeline route: the host maps the project access onto the engine's `access`. */
async function openRoute(projectId: string, query = '') {
  await renderRoute(`/projects/${projectId}/pipeline${query}`)
  await screen.findByRole('heading', { level: 2, name: 'Pipeline' })
  // Feedback renders for every role once the registry loads, so the role-gated controls have settled by then.
  expect(await screen.findByRole('region', { name: 'Feedback' })).toBeInTheDocument()
  await screen.findByRole('combobox', { name: 'Agent' })
}

const startRun = () => screen.queryByRole('button', { name: 'Start run' })
const triggers = () => screen.queryByRole('region', { name: 'Triggers' })

describe('FR-362, FR-366: the Pipeline route passes the project role to the engine UI', () => {
  it('FR362_route_viewer_sees_no_start_run_or_triggers', async () => {
    const { project } = memberOf('viewer')
    await openRoute(project.id)
    expect(startRun()).not.toBeInTheDocument()
    expect(triggers()).not.toBeInTheDocument()
  })

  it('FR362_route_contributor_sees_start_run_but_no_triggers', async () => {
    const { project } = memberOf('contributor')
    await openRoute(project.id)
    expect(await screen.findByRole('button', { name: 'Start run' })).toBeInTheDocument()
    expect(triggers()).not.toBeInTheDocument()
  })

  it('FR362_route_read_only_release_view_acts_as_viewer', async () => {
    const { project } = memberOf('owner')
    seedReleaseVersions(project, { released: '1.1.0', development: '1.2.0' })
    await openRoute(project.id, '?release=1.1.0')
    expect(startRun()).not.toBeInTheDocument()
    expect(triggers()).not.toBeInTheDocument()
  })

  it('FR366_route_admin_who_is_not_a_member_acts_as_owner', async () => {
    signInAs({ email: 'root@example.com', name: 'Root', systemRole: 'admin' })
    const project = seedProject({ name: 'P', members: [] })
    await openRoute(project.id)
    expect(await screen.findByRole('button', { name: 'Start run' })).toBeInTheDocument()
    expect(await screen.findByRole('region', { name: 'Triggers' })).toBeInTheDocument()
  })
})
