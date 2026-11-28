import { screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { runControlDb, seedPipelineSchedule } from '../../mocks/handlers/engine-run-control'
import { installRunControlMocks, memberOf, renderPipeline } from '../run-control/pipeline-harness.test-utils'

beforeEach(() => {
  installRunControlMocks()
})

const triggers = async () => within(await screen.findByRole('region', { name: 'Triggers' }))

describe('FR-366: triggers', () => {
  it('FR366_owner_sees_a_new_webhook_secret_once_and_a_reload_shows_it_without', async () => {
    const { project, access } = memberOf('owner')
    const first = await renderPipeline(project.id, access)
    // After userEvent.setup(), which installs its own clipboard.
    const writeText = vi.fn(() => Promise.resolve())
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    const section = await triggers()
    const webhooks = within(section.getByRole('region', { name: 'Webhooks' }))
    expect(await webhooks.findByText('No webhooks')).toBeInTheDocument()
    // Only pipelines declaring a webhook trigger are offered (the coach pipeline here).
    expect(webhooks.getAllByRole('option').map((o) => o.textContent)).toEqual(['Impulse coaching'])

    await first.user.click(webhooks.getByRole('button', { name: 'Create webhook' }))
    const secretField = await webhooks.findByRole('textbox', { name: 'Signing secret' })
    const stored = runControlDb.webhooks[0]
    expect(stored).toBeDefined()
    const secret = stored?.secret ?? ''
    expect(secretField).toHaveValue(secret)
    expect(webhooks.getByText('Copy the secret now. It isn’t shown again.')).toBeInTheDocument()
    const example = webhooks.getByRole('region', { name: 'Signing a call' })
    expect(example).toHaveTextContent('X-Tevrin-Signature: t=$T,v1=$SIG')
    expect(example).toHaveTextContent(`${window.location.origin}/api/hooks/pipelines/${stored?.id ?? ''}`)
    expect(example).toHaveTextContent('the session log as JSON')
    expect(example).not.toHaveTextContent(secret)

    await first.user.click(webhooks.getByRole('button', { name: 'Copy secret' }))
    expect(writeText).toHaveBeenCalledWith(secret)
    expect(await webhooks.findByText('Copied to the clipboard.')).toBeInTheDocument()
    // The list refetched without secrets, and no query holds the secret.
    expect(await webhooks.findByRole('list', { name: 'Webhooks' })).toBeInTheDocument()
    const cached = JSON.stringify(first.queryClient.getQueryCache().getAll().map((q) => q.state.data))
    expect(cached).not.toContain(secret)
    first.unmount()

    // A reload: a fresh cache. The webhook is listed, its secret is gone.
    await renderPipeline(project.id, access)
    const reloaded = within((await triggers()).getByRole('region', { name: 'Webhooks' }))
    const list = await reloaded.findByRole('list', { name: 'Webhooks' })
    expect(within(list).getByText(`${window.location.origin}/api/hooks/pipelines/${stored?.id ?? ''}`)).toBeInTheDocument()
    expect(reloaded.queryByRole('textbox', { name: 'Signing secret' })).not.toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(secret)
  })

  it('FR366_rotating_a_secret_shows_the_new_one_once', async () => {
    const { project, access } = memberOf('owner')
    const { user } = await renderPipeline(project.id, access)
    const webhooks = within((await triggers()).getByRole('region', { name: 'Webhooks' }))
    await user.click(await webhooks.findByRole('button', { name: 'Create webhook' }))
    await webhooks.findByRole('textbox', { name: 'Signing secret' })
    await user.click(webhooks.getByRole('button', { name: 'Done' }))
    expect(webhooks.queryByRole('textbox', { name: 'Signing secret' })).not.toBeInTheDocument()

    await user.click(await webhooks.findByRole('button', { name: 'Rotate the secret of the Impulse coaching webhook' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Rotate this webhook’s secret?' })
    await user.click(within(confirm).getByRole('button', { name: 'Rotate secret' }))
    const field = await webhooks.findByRole('textbox', { name: 'Signing secret' })
    expect(field).toHaveValue(runControlDb.webhooks[0]?.secret)
    expect(webhooks.getByText('The previous secret keeps working for 24 hours.')).toBeInTheDocument()
    expect(await webhooks.findByText(/The previous secret works until/)).toBeInTheDocument()
  })

  it('FR366_a_new_schedule_previews_its_next_3_fire_times_in_its_timezone', async () => {
    const { project, access } = memberOf('owner')
    const { user } = await renderPipeline(project.id, access)
    const schedules = within((await triggers()).getByRole('region', { name: 'Schedules' }))
    expect(await schedules.findByText('No schedules')).toBeInTheDocument()

    await user.type(schedules.getByRole('textbox', { name: 'Cron expression' }), '0 7 * * *')
    const tz = schedules.getByRole('textbox', { name: 'Timezone' })
    await user.clear(tz)
    await user.type(tz, 'America/New_York')
    // The API's UTC times (12:00Z) show at 07:00 New York time.
    const preview = await schedules.findByRole('list', { name: 'Next fire times' })
    const times = within(preview).getAllByRole('listitem')
    expect(times).toHaveLength(3)
    for (const t of times) {
      expect(t).toHaveTextContent(/07:00/)
      expect(t).toHaveTextContent(/EST|GMT-5/)
    }
    expect(within(preview).getAllByRole('listitem')[0]?.querySelector('time')).toHaveAttribute('dateTime', '2026-12-01T12:00:00Z')

    await user.click(schedules.getByRole('button', { name: 'Add schedule' }))
    const list = await schedules.findByRole('list', { name: 'Schedules' })
    expect(within(list).getByText('0 7 * * *')).toBeInTheDocument()
    expect(within(list).getByRole('list', { name: 'Next fire times of Impulse coaching (0 7 * * *)' })).toHaveTextContent(/07:00/)
    expect(runControlDb.schedules[0]).toMatchObject({ pipelineId: 'impulse-coach', cron: '0 7 * * *', timezone: 'America/New_York' })
  })

  it('FR366_an_invalid_cron_or_timezone_says_so', async () => {
    const { project, access } = memberOf('owner')
    const { user } = await renderPipeline(project.id, access)
    const schedules = within((await triggers()).getByRole('region', { name: 'Schedules' }))
    await user.type(await schedules.findByRole('textbox', { name: 'Cron expression' }), '0 7 * *')
    await user.click(schedules.getByRole('button', { name: 'Add schedule' }))
    expect(await schedules.findAllByText('Enter five fields: minute hour day-of-month month day-of-week.')).not.toHaveLength(0)

    await user.type(schedules.getByRole('textbox', { name: 'Cron expression' }), ' 1')
    const tz = schedules.getByRole('textbox', { name: 'Timezone' })
    await user.clear(tz)
    await user.type(tz, 'Mars/Olympus')
    expect(await schedules.findByText("That schedule isn't valid. Check the cron expression and timezone.")).toBeInTheDocument()
  })

  it('FR366_owner_pauses_resumes_and_deletes_a_schedule', async () => {
    const { project, access } = memberOf('owner')
    seedPipelineSchedule(project.id, { pipelineId: 'impulse-coach', cron: '30 6 * * 1', timezone: 'Europe/London' })
    const { user } = await renderPipeline(project.id, access)
    const schedules = within((await triggers()).getByRole('region', { name: 'Schedules' }))
    const list = await schedules.findByRole('list', { name: 'Schedules' })
    expect(within(list).getByText('Active')).toBeInTheDocument()
    expect(within(list).getByRole('list', { name: /Next fire times of Impulse coaching/ })).toHaveTextContent(/12:00 (GMT|UTC)/)

    await user.click(within(list).getByRole('button', { name: 'Pause' }))
    expect(await within(list).findByText('Paused')).toBeInTheDocument()
    await user.click(within(list).getByRole('button', { name: 'Resume' }))
    expect(await within(list).findByText('Active')).toBeInTheDocument()

    await user.click(within(list).getByRole('button', { name: 'Delete' }))
    await user.click(within(await screen.findByRole('alertdialog', { name: 'Delete this schedule?' })).getByRole('button', { name: 'Delete' }))
    expect(await schedules.findByText('No schedules')).toBeInTheDocument()
  })

  it('FR366_triggers_are_for_owners_only', async () => {
    const { project, access } = memberOf('contributor')
    await renderPipeline(project.id, access)
    expect(await screen.findByRole('button', { name: 'Start run' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Triggers' })).not.toBeInTheDocument()
  })
})
