import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../../mocks/handlers/auth'
import { projectsDb, seedProject } from '../../../mocks/handlers/projects'
import { openOwnerSettings, openSettings, requestsTo, settingsSectionLinks } from '../../../test/project-settings'
import { currentHref } from '../../../test/render'

describe('Settings → Danger zone (FR-7)', () => {
  it('FR-7: deleting needs the project name typed, then deletes and navigates to /projects', async () => {
    const { user, router, project } = await openOwnerSettings('danger', { name: 'Inventory tracker' })

    await user.click(await screen.findByRole('button', { name: 'Delete project' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Delete Inventory tracker?' })
    const confirm = within(dialog).getByRole('button', { name: 'Delete project permanently' })
    const input = within(dialog).getByLabelText('Type the project name (Inventory tracker) to confirm')
    expect(input).toHaveFocus()
    expect(confirm).toBeDisabled()

    await user.type(input, 'Inventory')
    expect(confirm).toBeDisabled()
    await user.keyboard('{Enter}')
    expect(requestsTo('DELETE', `/projects/${project.id}`)).toHaveLength(0)

    await user.type(input, ' tracker')
    expect(confirm).toBeEnabled()
    await user.click(confirm)

    await waitFor(() => {
      expect(currentHref(router)).toBe('/projects')
    })
    expect(requestsTo('DELETE', `/projects/${project.id}`)).toHaveLength(1)
    expect(projectsDb.projects).toHaveLength(0)
  })

  it('FR-7: cancelling the dialog deletes nothing', async () => {
    const { user, project } = await openOwnerSettings('danger')

    await user.click(await screen.findByRole('button', { name: 'Delete project' }))
    const dialog = await screen.findByRole('alertdialog')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete project' })).toHaveFocus()
    expect(projectsDb.projects).toEqual([project])
  })

  it('FR-7, FR53_settings_nav: the danger zone is Owner-only (no section link, and /settings/danger falls back to General)', async () => {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const me = signInAs({ email: 'ada@example.com', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: me.id, role: 'contributor' },
      ],
    })
    const { router } = await openSettings('danger', project)

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/projects/${project.id}/settings/general`)
    })
    expect(settingsSectionLinks().map((l) => l.textContent)).toEqual(['General', 'Members', 'Stack', 'LLM', 'Deployment'])
    expect(screen.getByRole('link', { name: 'General', current: 'page' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete project' })).not.toBeInTheDocument()
    expect(await screen.findByText('Only project Owners can change these settings.')).toBeInTheDocument()
  })
})
