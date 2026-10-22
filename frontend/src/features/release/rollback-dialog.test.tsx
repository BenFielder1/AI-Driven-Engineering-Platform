import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { makeReleaseVersion, seedProject, type StoredProject } from '../../mocks/handlers/projects'
import { ApiError, errorMessage, rollbackBlockedMessages } from '../../lib/errors'
import { openOwnerSettings, openSettings, requestsTo } from '../../test/project-settings'

/**
 * 1.2.0 (released 1.1.0) at checkpoint 6 in review; checkpoint 4 was a release-phase checkpoint (blocked), 6 is
 * current (blocked).
 */
const history = (): Partial<StoredProject> => ({
  currentPhase: 'review',
  status: 'reviewing',
  checkpoint: 6,
  phaseAtVersion: { 1: 'refinement', 2: 'specification', 3: 'planning', 4: 'release', 5: 'execution', 6: 'review' },
  releaseVersions: [
    makeReleaseVersion({ version: '1.1.0', status: 'released' }),
    makeReleaseVersion({ version: '1.2.0', status: 'in_development' }),
  ],
})

async function openRollbackDialog(overrides: Partial<StoredProject> = {}) {
  const utils = await openOwnerSettings('danger', { ...history(), ...overrides })
  await utils.user.click(await screen.findByRole('button', { name: 'Roll back…' }))
  const dialog = await screen.findByRole('alertdialog', { name: 'Roll back the project' })
  const picker = await within(dialog).findByLabelText('Checkpoint to restore')
  return { ...utils, dialog, picker }
}

async function fillAndContinue(utils: Awaited<ReturnType<typeof openRollbackDialog>>, version = '3', reason = 'The plan was wrong.') {
  const { user, dialog, picker } = utils
  await user.selectOptions(picker, version)
  await user.type(within(dialog).getByLabelText('Reason'), reason)
  await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
  return within(dialog).findByRole('button', { name: `Roll back to checkpoint ${version}` })
}

const userClick = (el: HTMLElement) => userEvent.setup().click(el)

describe('Settings → Danger zone → Roll back (FR-44, FR-45, FR-63)', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('FR63_rollback_picker_lists_checkpoints_of_the_version_in_development', async () => {
    const { picker } = await openRollbackDialog()

    const option = (name: string) => within(picker).getByRole('option', { name })
    expect(option('1.2.0 · checkpoint 6 · Review (current checkpoint)')).toBeDisabled()
    expect(option('1.2.0 · checkpoint 4 · Release (release checkpoints can’t be restored)')).toBeDisabled()
    expect(option('1.2.0 · checkpoint 3 · Planning')).toBeEnabled()
    expect(option('1.2.0 · checkpoint 1 · Refinement')).toBeEnabled()
    // Only checkpoints of 1.2.0: nothing of the released 1.1.0.
    expect(within(picker).queryByRole('option', { name: /1\.1\.0/ })).not.toBeInTheDocument()
  })

  it('FR63_released_version_is_closed_for_rollback', async () => {
    await openOwnerSettings('danger', {
      ...history(),
      currentPhase: 'release',
      status: 'released',
      releaseVersions: [makeReleaseVersion({ version: '1.1.0', status: 'released' })],
    })
    const button = await screen.findByRole('button', { name: 'Roll back…' })
    expect(button).toHaveAttribute('aria-disabled', 'true')
    expect(button).toHaveAccessibleDescription(
      "Version 1.1.0 is released and closed, so it can't be rolled back. Start a new version to make changes.",
    )
    await userClick(button)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('FR63_rollback_blocked_version_released_has_copy', () => {
    const error = new ApiError({
      status: 409,
      errorCode: 'ROLLBACK_BLOCKED',
      title: 'The version is released.',
      details: ['reason: version_released'],
    })
    expect(errorMessage(error, 'rollback')).toBe(rollbackBlockedMessages.versionReleased)
  })

  it('FR44_rollback_dialog_requires_reason_and_confirmation', async () => {
    const utils = await openRollbackDialog()
    const { user, dialog, project } = utils

    // Nothing chosen: both fields are required, and nothing is sent.
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))
    expect(await within(dialog).findAllByText('Choose the checkpoint to roll back to.')).not.toHaveLength(0)
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
    const heading = await within(dialog).findByRole('heading', {
      name: 'Roll back from 1.2.0 · checkpoint 6 to 1.2.0 · checkpoint 3?',
    })
    expect(heading).toHaveFocus()
    const effects = within(dialog).getByRole('list', { name: 'What the rollback does' })
    expect(effects).toHaveTextContent('Restores the Planning phase recorded at checkpoint 3')
    expect(effects).toHaveTextContent('Artifacts created after checkpoint 3 are invalidated')
    expect(effects).toHaveTextContent('Pending gates and queued jobs are cancelled')
    expect(requestsTo('POST', `/projects/${project.id}/rollback`)).toHaveLength(0)

    // Back keeps the values.
    await user.click(within(dialog).getByRole('button', { name: 'Back' }))
    expect(await within(dialog).findByLabelText('Checkpoint to restore')).toHaveValue('3')
    expect(within(dialog).getByLabelText('Checkpoint to restore')).toHaveFocus()
    expect(within(dialog).getByLabelText('Reason')).toHaveValue('The plan was wrong.')
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }))

    await user.click(await within(dialog).findByRole('button', { name: 'Roll back to checkpoint 3' }))

    const summary = await screen.findByText('Rolled back from 1.2.0 · checkpoint 6 to checkpoint 3')
    expect(summary.closest('[role="status"]')).not.toBeNull()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('The project is back in Planning.')).toBeInTheDocument()
    expect(requestsTo('POST', `/projects/${project.id}/rollback`).map((r) => r.body)).toEqual([
      { targetCheckpoint: 3, reason: 'The plan was wrong.' },
    ])
    // The history lists the recorded rollback.
    const list = await screen.findByRole('list', { name: 'Rollbacks' })
    expect(list).toHaveTextContent('1.2.0 · checkpoint 6 → checkpoint 3, restored Planning')
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
    expect(await screen.findByText('Rolled back from 1.2.0 · checkpoint 6 to checkpoint 3')).toBeInTheDocument()
  })

  it('FR45: ROLLBACK_BLOCKED explains that a step or run is in progress', async () => {
    const utils = await openRollbackDialog({ rollbackBlocked: true })
    await utils.user.click(await fillAndContinue(utils))

    expect(
      await within(utils.dialog).findByText(/isn't possible while an execution step or agent run is in progress/),
    ).toBeInTheDocument()
    expect(screen.queryByText('Rolled back from 1.2.0 · checkpoint 6 to checkpoint 3')).not.toBeInTheDocument()
  })

  it('FR44: INVALID_TARGET_VERSION returns to the picker with the reason', async () => {
    const utils = await openRollbackDialog()
    const confirm = await fillAndContinue(utils)
    // The checkpoint became invalid meanwhile (e.g. someone else rolled back).
    utils.project.checkpoint = 3
    await utils.user.click(confirm)

    const picker = await within(utils.dialog).findByLabelText('Checkpoint to restore')
    expect(within(utils.dialog).getAllByText(/You can't roll back to that checkpoint/)).not.toHaveLength(0)
    await waitFor(() => {
      expect(
        within(picker).getByRole('option', { name: '1.2.0 · checkpoint 3 · Planning (current checkpoint)' }),
      ).toBeDisabled()
    })
  })

  it('NFR-A11Y: the rollback dialog is a labelled modal that traps focus and returns it on Escape', async () => {
    const { user, dialog, picker } = await openRollbackDialog()

    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(dialog).toHaveAccessibleDescription(/Restore an earlier checkpoint of the version in development/)
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
    expect(picker).toHaveAccessibleDescription(/Choose the checkpoint to roll back to/)
    expect(within(dialog).getByRole('link', { name: 'Choose the checkpoint to roll back to.' })).toHaveAttribute(
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
      ...history(),
      members: [
        { userId: owner.id, role: 'owner' },
        { userId: me.id, role: 'contributor' },
      ],
    })
    await openSettings('danger', project)
    expect(screen.queryByRole('button', { name: 'Roll back…' })).not.toBeInTheDocument()
  })
})
