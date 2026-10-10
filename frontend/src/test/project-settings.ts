import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import type { SettingsTab } from '../features/projects/settings/settings-tabs'
import { signInAs } from '../mocks/handlers/auth'
import { projectsDb, seedProject, type StoredProject } from '../mocks/handlers/projects'
import { renderRoute } from './render'

type SeedOverrides = Parameters<typeof seedProject>[0]

/** Signs in a non-admin user (Owner of the seeded project unless `members` says otherwise). */
export function signInAsOwner() {
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

/** Renders /projects/:id/settings at `tab` and waits for the tab list. */
export async function openSettings(tab: SettingsTab, project: StoredProject) {
  const utils = renderRoute(`/projects/${project.id}/settings${tab === 'general' ? '' : `?tab=${tab}`}`)
  await screen.findByRole('tablist', { name: 'Settings sections' })
  return { ...utils, user: userEvent.setup(), project }
}

/** Seeds a project owned by a freshly signed-in user and opens its settings at `tab`. */
export async function openOwnerSettings(tab: SettingsTab, overrides: SeedOverrides = {}) {
  signInAsOwner()
  return openSettings(tab, seedProject(overrides))
}

/** Requests the mock API received for `method` on paths ending with `suffix`. */
export function requestsTo(method: string, suffix: string) {
  return projectsDb.requests.filter((r) => r.method === method && r.path.endsWith(suffix))
}
