import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { signInAs } from '../../../mocks/handlers/auth'
import { problem } from '../../../mocks/handlers/problem'
import { projectsDb, seedProject } from '../../../mocks/handlers/projects'
import { server } from '../../../mocks/server'
import { openOwnerSettings, openSettings, requestsTo } from '../../../test/project-settings'

const help = 'Fix attempts per cycle before review. 0 turns the build check off'

describe('Settings → General: build fix attempts (FR-258)', () => {
  it('FR258_owner_sees_the_field_next_to_max_review_cycles_with_its_help_text', async () => {
    await openOwnerSettings('general', { maxBuildFixAttempts: 5 })

    const field = await screen.findByLabelText('Build fix attempts')
    expect(field).toHaveValue(5)
    expect(field).toHaveAttribute('min', '0')
    expect(field).toHaveAttribute('max', '10')
    expect(field).toHaveAccessibleDescription(`0–10. ${help}.`)
    expect(screen.getByLabelText('Max review cycles')).toBeInTheDocument()
  })

  it('FR258_11_is_rejected_on_the_client', async () => {
    const { user, project } = await openOwnerSettings('general', { maxBuildFixAttempts: 5 })

    const field = await screen.findByLabelText('Build fix attempts')
    await user.clear(field)
    await user.type(field, '11')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    const message = 'Enter a whole number from 0 to 10 for build fix attempts.'
    const summary = await screen.findByRole('alert')
    expect(within(summary).getByRole('link', { name: message })).toHaveAttribute('href', '#settings-max-build-fix-attempts')
    expect(field).toHaveAttribute('aria-invalid', 'true')
    expect(requestsTo('PATCH', `/api/projects/${project.id}`)).toHaveLength(0)
  })

  it('FR258_0_turns_the_build_check_off_and_is_sent_as_the_only_change', async () => {
    const { user, project } = await openOwnerSettings('general', { maxBuildFixAttempts: 5 })

    const field = await screen.findByLabelText('Build fix attempts')
    await user.clear(field)
    await user.type(field, '0')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => {
      expect(requestsTo('PATCH', `/api/projects/${project.id}`)).toHaveLength(1)
    })
    expect(requestsTo('PATCH', `/api/projects/${project.id}`)[0]?.body).toEqual({ maxBuildFixAttempts: 0 })
    expect(projectsDb.projects.find((p) => p.id === project.id)?.maxBuildFixAttempts).toBe(0)
    expect(await screen.findByText('Settings saved.')).toBeInTheDocument()
  })

  it('FR258_server_VALIDATION_for_the_field_shows_on_it', async () => {
    server.use(
      http.patch('*/api/projects/:id', () =>
        problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
          errors: { maxBuildFixAttempts: ['Max build fix attempts must be between 0 and 10.'] },
        }),
      ),
    )
    const { user } = await openOwnerSettings('general', { maxBuildFixAttempts: 5 })

    const field = await screen.findByLabelText('Build fix attempts')
    await user.clear(field)
    await user.type(field, '10')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => {
      expect(field).toHaveAttribute('aria-invalid', 'true')
    })
    expect(field).toHaveAccessibleDescription(expect.stringContaining('Max build fix attempts must be between 0 and 10.'))
  })

  it('FR258_contributors_read_it_but_cannot_edit_it', async () => {
    const owner = signInAs({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const me = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      maxBuildFixAttempts: 0,
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: me.id, role: 'contributor' },
      ],
    })
    await openSettings('general', project)

    expect(await screen.findByText('Only project Owners can change these settings.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Build fix attempts')).not.toBeInTheDocument()
    const row = screen.getByText('Build fix attempts').closest('div') as HTMLElement
    expect(within(row).getByText('0 (build check off)')).toBeInTheDocument()
  })
})
