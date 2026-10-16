import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { contextualErrorMessages, errorMessages } from '../../../lib/errors'
import { seedUser, signInAs } from '../../../mocks/handlers/auth'
import { projectsDb, seedProject } from '../../../mocks/handlers/projects'
import { openOwnerSettings, openSettings, requestsTo, settingsSectionLinks } from '../../../test/project-settings'

function seedGrace() {
  return seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
}

async function membersList() {
  return screen.findByRole('list', { name: 'Project members' })
}

describe('Settings → Members (FR-8)', () => {
  it('FR-8: an Owner adds an existing user by email with a role', async () => {
    seedGrace()
    const { user, project } = await openOwnerSettings('members')
    await membersList()

    await user.type(screen.getByLabelText('Email'), 'grace@example.com')
    await user.selectOptions(screen.getByLabelText('Role'), 'contributor')
    await user.click(screen.getByRole('button', { name: 'Add member' }))

    expect(await screen.findByText('Grace Hopper was added as Contributor.')).toBeInTheDocument()
    const list = await membersList()
    await waitFor(() => {
      expect(within(list).getAllByRole('listitem')).toHaveLength(2)
    })
    expect(within(list).getByLabelText('Role for Grace Hopper')).toHaveValue('contributor')
    expect(projectsDb.projects.find((p) => p.id === project.id)?.members).toHaveLength(2)
  })

  it('FR-8: adding an existing member shows the contextual CONFLICT copy on the email field, linked from the summary', async () => {
    const grace = seedGrace()
    const owner = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: grace.id, role: 'viewer' },
      ],
    })
    const { user } = await openSettings('members', project)
    await membersList()

    await user.type(screen.getByLabelText('Email'), 'grace@example.com')
    await user.click(screen.getByRole('button', { name: 'Add member' }))

    const message = contextualErrorMessages.addMember.CONFLICT
    const email = screen.getByLabelText('Email')
    await waitFor(() => {
      expect(email).toHaveAttribute('aria-invalid', 'true')
    })
    expect(email).toHaveAccessibleDescription(expect.stringContaining(message) as string)
    expect(screen.getByRole('link', { name: message })).toHaveAttribute('href', '#member-email')
    expect(screen.getByText("We couldn't add the member")).toBeInTheDocument()
  })

  it('FR-8: an unknown email shows USER_NOT_FOUND on the email field', async () => {
    const { user } = await openOwnerSettings('members')
    await membersList()

    await user.type(screen.getByLabelText('Email'), 'nobody@example.com')
    await user.click(screen.getByRole('button', { name: 'Add member' }))

    expect(await screen.findByRole('link', { name: errorMessages.USER_NOT_FOUND })).toHaveAttribute('href', '#member-email')
  })

  it("FR-8: an Owner changes a member's role", async () => {
    const grace = seedGrace()
    const owner = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: grace.id, role: 'viewer' },
      ],
    })
    const { user } = await openSettings('members', project)
    const list = await membersList()

    await user.selectOptions(within(list).getByLabelText('Role for Grace Hopper'), 'owner')

    await waitFor(() => {
      expect(requestsTo('PATCH', `/members/${grace.id}`)).toEqual([expect.objectContaining({ body: { role: 'owner' } })])
    })
    await waitFor(() => {
      expect(within(list).getByLabelText('Role for Grace Hopper')).toHaveValue('owner')
    })
    expect(project.members.find((m) => m.userId === grace.id)?.role).toBe('owner')
  })

  it('FR-8: an Owner removes a member after confirming', async () => {
    const grace = seedGrace()
    const owner = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: grace.id, role: 'contributor' },
      ],
    })
    const { user } = await openSettings('members', project)
    const list = await membersList()

    await user.click(within(list).getByRole('button', { name: 'Remove Grace Hopper' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove Grace Hopper?' })
    await user.click(within(dialog).getByRole('button', { name: 'Remove member' }))

    await waitFor(() => {
      expect(screen.queryByText('Grace Hopper')).not.toBeInTheDocument()
    })
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(project.members.map((m) => m.userId)).toEqual([owner.id])
  })

  it('FR-8: demoting the last Owner shows the LAST_OWNER message and keeps the role', async () => {
    const { user, project } = await openOwnerSettings('members')
    const list = await membersList()

    await user.selectOptions(within(list).getByLabelText('Role for Ada Lovelace'), 'viewer')

    expect(await screen.findByText(errorMessages.LAST_OWNER)).toBeInTheDocument()
    expect(screen.getByText("That didn't work").closest('[role="alert"]')).toHaveTextContent(errorMessages.LAST_OWNER)
    expect(project.members[0]?.role).toBe('owner')
    await waitFor(() => {
      expect(within(list).getByLabelText('Role for Ada Lovelace')).toHaveValue('owner')
    })
  })

  it('FR-8: Contributors and Viewers see the member list but no add, role or remove controls', async () => {
    const grace = seedGrace()
    const ada = signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    const project = seedProject({
      members: [
        { userId: grace.id, role: 'owner' },
        { userId: ada.id, role: 'contributor' },
      ],
    })
    await openSettings('members', project)
    const list = await membersList()

    expect(within(list).getAllByRole('listitem')).toHaveLength(2)
    expect(within(list).getByText('Owner')).toBeInTheDocument()
    expect(within(list).getByText('Contributor')).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'Add a member' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument()
    expect(within(list).queryByRole('combobox')).not.toBeInTheDocument()
    expect(within(list).queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Leave project' })).toBeInTheDocument()
    // Only General, Members and LLM (read-only, FR-79) are visible to non-owners.
    expect(settingsSectionLinks().map((t) => t.textContent)).toEqual(['General', 'Members', 'LLM'])
  })
})
