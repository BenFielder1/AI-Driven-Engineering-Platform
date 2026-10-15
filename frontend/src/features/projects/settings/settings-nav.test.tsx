import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedProject } from '../../../mocks/handlers/projects'
import { settingsSectionLinks, signInAsOwner } from '../../../test/project-settings'
import { renderRoute } from '../../../test/render'

describe('Settings secondary nav (SPEC §7.2)', () => {
  it('FR53_settings_nav: /settings redirects to /settings/general and the Owner sees every section', async () => {
    signInAsOwner()
    const project = seedProject()
    const { router } = renderRoute(`/projects/${project.id}/settings`)

    await screen.findByRole('navigation', { name: 'Settings sections' })
    expect(router.state.location.pathname).toBe(`/projects/${project.id}/settings/general`)
    expect(settingsSectionLinks().map((l) => l.textContent)).toEqual([
      'General',
      'Members',
      'Git',
      'LLM',
      'Notifications',
      'Danger zone',
    ])
    expect(screen.getByRole('link', { name: 'General', current: 'page' })).toBeInTheDocument()
  })

  it('FR53_settings_nav: a v1 ?tab= link lands on its section, and the links are sub-routes', async () => {
    signInAsOwner()
    const project = seedProject()
    const { router } = renderRoute(`/projects/${project.id}/settings?tab=git`)

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/projects/${project.id}/settings/git`)
    })
    expect(await screen.findByRole('link', { name: 'Git', current: 'page' })).toBeInTheDocument()

    await userEvent.setup().click(screen.getByRole('link', { name: 'Members' }))
    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/projects/${project.id}/settings/members`)
    })
    expect(screen.getByRole('link', { name: 'Members', current: 'page' })).toBeInTheDocument()
  })

  it('FR53_settings_nav: an unknown section falls back to General', async () => {
    signInAsOwner()
    const project = seedProject()
    const { router } = renderRoute(`/projects/${project.id}/settings/nope`)

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/projects/${project.id}/settings/general`)
    })
  })
})
