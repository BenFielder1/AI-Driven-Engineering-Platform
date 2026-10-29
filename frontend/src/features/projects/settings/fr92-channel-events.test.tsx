import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { StoredChannel } from '../../../mocks/handlers/projects'
import { openOwnerSettings, requestsTo } from '../../../test/project-settings'

const emailChannel: StoredChannel = {
  id: '00000000-0000-7000-8000-00000000c101',
  type: 'email',
  target: 'team@example.com',
  secret: null,
  events: ['gate_required', 'preview_ready'],
}

describe('Settings → Notifications: FR-92 channel events', () => {
  it('FR92_channel_events_offer_the_four_new_project_events_but_not_connection_invalid', async () => {
    const { user, project } = await openOwnerSettings('notifications', { channels: [{ ...emailChannel }] })
    await screen.findByRole('heading', { name: 'Notifications' })
    const events = screen.getByRole('group', { name: 'Channel 1 events' })

    expect(within(events).getByLabelText('Preview ready')).toBeChecked()
    for (const label of ['Release awaiting a merge on GitHub', 'Deployment failed', 'LLM connection unbound', 'Git connection unbound']) {
      expect(within(events).getByLabelText(label)).not.toBeChecked()
    }
    // Personal: never a project channel event.
    expect(within(events).queryByLabelText('Connection invalid')).not.toBeInTheDocument()
    expect(within(events).getAllByRole('checkbox')).toHaveLength(15)

    await user.click(within(events).getByLabelText('Release awaiting a merge on GitHub'))
    await user.click(within(events).getByLabelText('Deployment failed'))
    await user.click(within(events).getByLabelText('LLM connection unbound'))
    await user.click(screen.getByRole('button', { name: 'Save notifications' }))

    expect(await screen.findByText('Notification settings saved.')).toBeInTheDocument()
    const sent = requestsTo('PATCH', '/settings/notifications').at(-1)?.body as { channels: { events: string[] }[] }
    expect([...(sent.channels[0]?.events ?? [])].sort()).toEqual(
      ['deployment_failed', 'gate_required', 'llm_binding_cleared', 'preview_ready', 'release_awaiting_merge'].sort(),
    )
    expect(project.channels[0]?.events).toContain('llm_binding_cleared')
  })
})
