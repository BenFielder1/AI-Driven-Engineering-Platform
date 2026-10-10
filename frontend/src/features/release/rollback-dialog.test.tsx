import { screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { openOwnerSettings, openSettings, requestsTo } from '../../test/project-settings'

/** Version 6 in review; version 4 was a release-phase version (blocked), 6 is current (blocked). */
const history: Partial<StoredProject> = {
  currentPhase: 'review',
  status: 'reviewing',
  projectVersion: 6,
  phaseAtVersion: { 1: 'refinement', 2: 'specification', 3: 'planning', 4: 'release', 5: 'execution', 6: 'review' },
}

async function openRollbackDialog(overrides: Partial<StoredProject> = {}) {
  const utils = await openOwnerSettings('danger', { ...history, ...overrides })
  await utils.user.click(await screen.findByRole('button', { name: 'Roll back…' }))
  const dialog = await screen.findByRole('alertdialog', { name: 'Roll back the project' })
  const picker = await within(dialog).findByLabelText('Version to restore')
  return { ...utils, dialog, picker }
}

async function fillAndContinue(utils: Awaited<ReturnType<typeof openRollbackDialog>>, version = '3', reason = 'The plan was wrong.') {
  const { user, dialog, picker } = utils
  await user.selectOptions(picker, version)
  await user.type(within(dialog).getByLabelText('Reason'), reason)
  await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
  return within(dialog).findByRole('button', { name: `Roll back to version ${version}` })
}

describe('Settings → Danger zone → Roll back (FR-44, FR-45)', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('FR44_disabled_versions_show_reason', async () => {
    const { picker } = await openRollbackDialog()

    const option = (name: string) => within(picker).getByRole('option', { name })
    expect(option('Version 6 · Review (current version)')).toBeDisabled()
    expect(option('Version 4 · Release (release versions can’t be restored)')).toBeDisabled()
    expect(option('Version 3 · Planning')).toBeEnabled()
    expect(option('Version 1 · Refinement')).toBeEnabled()
  })

  it('FR44_rollback_dialog_requires_reason_and_confirmation', async () => {
    const utils = await openRollbackDialog()
    const { user, dialog, project } = utils

    // Nothing chosen: both fields are required, and nothing is sent.
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
    expect(await within(dialog).findAllByText('Choose the version to roll back to.')).not.toHaveLength(0)
    expect(within(dialog).getAllByText('Enter the reason for the rollback.')).not.toHaveLength(0)

    // A whitespace-only reason is still empty.
    await user.selectOptions(utils.picker, '3')
    await user.type(within(dialog).getByLabelText('Reason'), '   ')
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
    expect(within(dialog).getAllByText('Enter the reason for the rollback.')).not.toHaveLength(0)
    await user.clear(within(dialog).getByLabelText('Reason'))

    await user.type(within(dialog).getByLabelText('Reason'), 'The plan was wrong.')
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))

    // Confirmation step: a summary of the effects, and still nothing sent.
    const heading = await within(dialog).findByRole('heading', { name: 'Roll back from version 6 to version 3?' })
    expect(heading).toHaveFocus()
    const effects = within(dialog).getByRole('list', { name: 'What the rollback does' })
    expect(effects).toHaveTextContent('Restores the Planning phase recorded at version 3')
    expect(effects).toHaveTextContent('Artifacts created after version 3 are invalidated')
    expect(effects).toHaveTextContent('Pending gates and queued jobs are cancelled')
    expect(requestsTo('POST', `/projects/${project.id}/rollback`)).toHaveLength(0)

    // Back keeps the values.
    await user.click(within(dialog).getByRole('button', { name: 'Back' }))
    expect(await within(dialog).findByLabelText('Version to restore')).toHaveValue('3')
    expect(within(dialog).getByLabelText('Version to restore')).toHaveFocus()
    expect(within(dialog).getByLabelText('Reason')).toHaveValue('The plan was wrong.')
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))

    await user.click(await within(dialog).findByRole('button', { name: 'Roll back to version 3' }))

    const summary = await screen.findByText('Rolled back from version 6 to version 3')
    expect(summary.closest('[role="status"]')).not.toBeNull()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('The project is back in Planning.')).toBeInTheDocument()
    expect(requestsTo('POST', `/projects/${project.id}/rollback`).map((r) => r.body)).toEqual([
      { targetVersion: 3, reason: 'The plan was wrong.' },
    ])
    // The history lists the recorded rollback.
    const list = await screen.findByRole('list', { name: 'Rollbacks' })
    expect(list).toHaveTextContent('Version 6 → version 3, restored Planning')
    expect(list).toHaveTextContent('The plan was wrong.')
  })

  it('FR44: the reason is at most 500 characters', async () => {
    const utils = await openRollbackDialog()
    const { user, dialog } = utils
    await user.selectOptions(utils.picker, '3')
    await user.click(within(dialog).getByLabelText('Reason'))
    await user.paste('x'.repeat(501))
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
    expect(await within(dialog).findAllByText('The reason is at most 500 characters.')).not.toHaveLength(0)
  })

  it('FR45_rate_limited_shows_countdown', async () => {
    // Pin the clock (only Date; timers stay real) so the mock's one-minute window and the dialog's countdown don't
    // depend on how long opening the dialog takes under load.
    const now = Date.now()
    vi.useFakeTimers({ toFake: ['Date'], now })
    const utils = await openRollbackDialog({ lastRollbackAt: now - 58_000 })
    const confirm = await fillAndContinue(utils)

    await utils.user.click(confirm)

    expect(
      await within(utils.dialog).findByText('A rollback ran less than a minute ago. Only one rollback per minute is allowed.'),
    ).toBeInTheDocument()
    expect(within(utils.dialog).getByText('Try again in 2s.')).toBeInTheDocument()
    expect(confirm).toBeDisabled()

    // The countdown ends and the rollback can be confirmed again.
    vi.setSystemTime(now + 2_000)
    await waitFor(() => {
      expect(confirm).toBeEnabled()
    })
    expect(within(utils.dialog).getByText('You can try again now.')).toBeInTheDocument()
    await utils.user.click(confirm)
    expect(await screen.findByText('Rolled back from version 6 to version 3')).toBeInTheDocument()
  })

  it('FR45: ROLLBACK_BLOCKED explains that a step or run is in progress', async () => {
    const utils = await openRollbackDialog({ rollbackBlocked: true })
    await utils.user.click(await fillAndContinue(utils))

    expect(
      await within(utils.dialog).findByText(/isn't possible while an execution step or agent run is in progress/),
    ).toBeInTheDocument()
    expect(screen.queryByText('Rolled back from version 6 to version 3')).not.toBeInTheDocument()
  })

  it('FR44: INVALID_TARGET_VERSION returns to the picker with the reason', async () => {
    const utils = await openRollbackDialog()
    const confirm = await fillAndContinue(utils)
    // The version became invalid meanwhile (e.g. someone else rolled back).
    utils.project.projectVersion = 3
    await utils.user.click(confirm)

    const picker = await within(utils.dialog).findByLabelText('Version to restore')
    expect(within(utils.dialog).getAllByText(/You can't roll back to that version/)).not.toHaveLength(0)
    await waitFor(() => {
      expect(within(picker).getByRole('option', { name: 'Version 3 · Planning (current version)' })).toBeDisabled()
    })
  })

  it('NFR-A11Y: the rollback dialog is a labelled modal that traps focus and returns it on Escape', async () => {
    const { user, dialog, picker } = await openRollbackDialog()

    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(dialog).toHaveAccessibleDescription(/Restore an earlier project version/)
    await waitFor(() => {
      expect(picker).toHaveFocus()
    })
    expect(within(dialog).getByLabelText('Reason')).toHaveAccessibleDescription(/Up to 500 characters/)

    // Tab cycles inside the dialog.
    for (let i = 0; i < 6; i += 1) {
      await user.tab()
      expect(dialog).toContainElement(document.activeElement as HTMLElement)
    }

    // Invalid fields are announced and described.
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
    await waitFor(() => {
      expect(picker).toHaveAttribute('aria-invalid', 'true')
    })
    expect(picker).toHaveAccessibleDescription(/Choose the version to roll back to/)
    expect(within(dialog).getByRole('link', { name: 'Choose the version to roll back to.' })).toHaveAttribute(
      'href',
      '#rollback-target',
    )

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Roll back…' })).toHaveFocus()
  })

  it('FR44: rollback is Owner-only (the danger zone is hidden from Contributors)', async () => {
    const owner = seedUser({ email: 'grace@example.com', name: 'Grace Hopper', systemRole: 'user' })
    const me = signInAs({ email: 'ada@example.com', systemRole: 'user' })
    const project = seedProject({
      ...history,
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: me.id, role: 'contributor' },
      ],
    })
    await openSettings('danger', project)
    expect(screen.queryByRole('button', { name: 'Roll back…' })).not.toBeInTheDocument()
  })
})
