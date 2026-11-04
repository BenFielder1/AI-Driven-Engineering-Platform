import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { appPaths } from '../lib/app-paths'
import { signInAs } from '../mocks/handlers/auth'
import { seedProject } from '../mocks/handlers/projects'
import { renderRoute } from './render'

/**
 * FR-208: every in-app route renders exactly one h1 (the PageHeader title, or the forbidden / error state's). Public
 * routes (`/`, `/login`, `/register`) are M41's.
 */
const appRoutes = appPaths.filter((p) => !['/', '/login', '/register'].includes(p))

function concrete(path: string, projectId: string): string {
  return path
    .replace('$projectId', projectId)
    .replace('$type', 'technical_specification')
    .replace('$section', 'general')
    .replace('$token', 'unknown-token')
}

describe('FR-208: one h1 per app route', () => {
  it.each(appRoutes.map((p) => [p]))('FR208_single_h1: %s renders a single h1', async (path) => {
    signInAs({ systemRole: 'admin', name: 'Ada' })
    const project = seedProject({ name: 'Inventory tracker' })
    await renderRoute(concrete(path, project.id))

    await screen.findAllByRole('heading', { level: 1 })
    await waitFor(() => {
      expect(screen.queryAllByText(/^Loading\b/)).toHaveLength(0)
    })
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })
})
