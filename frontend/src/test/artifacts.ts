import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect } from 'vitest'

import type { Schemas } from '../api/client'
import type { ProjectRole } from '../lib/permissions'
import { seedUser, signInAs } from '../mocks/handlers/auth'
import { seedProject, type StoredProject } from '../mocks/handlers/projects'
import { renderRoute } from './render'

/** Spec v1 → v3 for the FR-21 example: one line changed, one added. */
export const specV1 = ['# Inventory tracker spec', '', '## Scope', 'Track stock levels.', '', '## API', '- GET /items'].join('\n')
export const specV2 = ['# Inventory tracker spec', '', '## Scope', 'Track stock levels.', '', '## API', '- GET /items', '- GET /items/{id}'].join('\n')
export const specV3 = [
  '# Inventory tracker spec',
  '',
  '## Scope',
  'Track stock levels across warehouses.',
  '',
  '## API',
  '- GET /items',
  '- POST /items',
].join('\n')

/** Signs in a non-admin user holding `role` in a fresh project (another user owns it when `role` isn't owner). */
export function seedProjectAs(role: ProjectRole, setup?: (p: StoredProject) => void): StoredProject {
  const members: { userId: string; role: ProjectRole }[] = []
  if (role !== 'owner') {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    members.push({ userId: owner.id, role: 'owner' })
  }
  const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
  members.push({ userId: me.id, role })
  const project = seedProject({ currentPhase: 'specification', members })
  setup?.(project)
  return project
}

/** /projects/:id/artifacts, waiting for the type cards. */
export async function openArtifacts(project: StoredProject) {
  const utils = await renderRoute(`/projects/${project.id}/artifacts`)
  await screen.findByRole('list', { name: 'Artifact types' })
  return { ...utils, user: userEvent.setup(), project }
}

/**
 * /projects/:id/artifacts/:type (optionally at `version`), waiting for the page heading and then for the history and
 * the shown version to finish loading (no "Loading the …" status left), so tests start from the settled page.
 */
export async function openArtifact(project: StoredProject, type: Schemas['ArtifactType'], version?: number) {
  const utils = await renderRoute(`/projects/${project.id}/artifacts/${type}${version ? `?version=${String(version)}` : ''}`)
  await screen.findByRole('heading', { level: 2 })
  await waitFor(() => {
    expect(screen.queryByText(/^Loading the /)).not.toBeInTheDocument()
  })
  return { ...utils, user: userEvent.setup(), project }
}
