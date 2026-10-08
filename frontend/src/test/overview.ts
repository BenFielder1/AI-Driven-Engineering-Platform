import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import { seedUser, signInAs } from '../mocks/handlers/auth'
import { seedProject, type StoredProject } from '../mocks/handlers/projects'
import type { ProjectRole } from '../lib/permissions'
import { renderRoute } from './render'

type SeedOverrides = Omit<NonNullable<Parameters<typeof seedProject>[0]>, 'members'>

/** Renders /projects/:id (Overview) and waits for the lifecycle card and the pending gates. */
export async function openOverview(project: StoredProject) {
  const utils = renderRoute(`/projects/${project.id}`)
  // The first render loads the lazy route chunks, which can be slow when the whole suite runs in parallel; findBy and
  // waitFor use the suite-wide asyncUtilTimeout (src/test/setup.ts).
  await screen.findByRole('heading', { name: 'Lifecycle' })
  await waitFor(() => {
    if (screen.queryByText('Loading gates…')) {
      throw new Error('Gates still loading')
    }
  })
  return { ...utils, user: userEvent.setup(), project }
}

/**
 * Signs in a non-admin user holding `role` in a freshly seeded project (another user is the Owner when `role`
 * isn't owner) and opens its overview.
 */
export async function openOverviewAs(role: ProjectRole, overrides: SeedOverrides = {}, setup?: (p: StoredProject) => void) {
  const members: { userId: string; role: ProjectRole }[] = []
  if (role !== 'owner') {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    members.push({ userId: owner.id, role: 'owner' })
  }
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  members.push({ userId: me.id, role })
  const project = seedProject({ ...overrides, members })
  setup?.(project)
  return openOverview(project)
}

/** The `aria-current="step"` item of the overview's phase timeline. */
export function currentTimelineStep() {
  const timeline = screen.getByRole('list', { name: 'Phase timeline' })
  return timeline.querySelector('[aria-current="step"]')
}
