import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { notificationsDb, seedNotification } from '../../mocks/handlers/notifications'
import { currentHref, renderRoute } from '../../test/render'
import { requestsTo } from '../../test/project-settings'
import { NOTIFYING_EVENT_TYPES } from '../live/use-project-live'
import { notificationEvents, notificationTypeLabels, notificationTypes } from '../projects/labels'

const PROJECT = '00000000-0000-7000-8000-0000000000aa'

function signInUser() {
  seedUser({ email: 'root@example.com', systemRole: 'admin' })
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

/** The five FR-92 types with the links the backend's `NotificationRules.LinkPath` gives them. */
const fr92Cases: { type: Schemas['NotificationType']; label: string; title: string; link: string }[] = [
  {
    type: 'release_awaiting_merge',
    label: 'Release awaiting a merge on GitHub',
    title: 'Release v1.2.0 is waiting for a merge',
    link: `/projects/${PROJECT}/release`,
  },
  {
    type: 'deployment_failed',
    label: 'Deployment failed',
    title: 'Production deployment of 1.2.0 failed',
    link: `/projects/${PROJECT}/release`,
  },
  { type: 'preview_ready', label: 'Preview ready', title: 'Preview of 1.2.0 is ready', link: `/projects/${PROJECT}/review` },
  {
    type: 'llm_binding_cleared',
    label: 'LLM connection unbound',
    title: 'The LLM connection was unbound',
    link: `/projects/${PROJECT}/settings/llm`,
  },
  { type: 'connection_invalid', label: 'Connection invalid', title: 'Your GitHub connection is invalid', link: '/connections' },
]

const allTypes: Schemas['NotificationType'][] = [
  'gate_required',
  'gate_overdue',
  'gate_reminder',
  'artifact_ready',
  'step_blocked',
  'run_failed',
  'budget_exceeded',
  'review_completed',
  'release_completed',
  'release_failed',
  'rollback',
  'release_awaiting_merge',
  'deployment_failed',
  'preview_ready',
  'connection_invalid',
  'llm_binding_cleared',
]

describe('FR-92: v2 notification types in the UI', () => {
  it('FR92_every_type_has_a_label_and_the_preference_and_channel_lists_cover_them', () => {
    expect([...notificationTypes].sort()).toEqual([...allTypes].sort())
    for (const t of allTypes) {
      expect(notificationTypeLabels[t]).toBeTruthy()
    }
    // Channels: every type but the reminder (it follows gate_required) and the personal connection_invalid.
    expect([...notificationEvents].sort()).toEqual(
      allTypes.filter((t) => t !== 'gate_reminder' && t !== 'connection_invalid').sort(),
    )
  })

  it.each(fr92Cases)('FR92_$type renders with its label, icon and link', async ({ type, label, title, link }) => {
    signInUser()
    const personal = type === 'connection_invalid'
    seedNotification({
      type,
      title,
      link,
      projectId: personal ? null : PROJECT,
      projectName: personal ? null : 'Inventory tracker',
    })
    const { router } = await renderRoute('/notifications')
    const user = userEvent.setup()

    const list = await screen.findByRole('list', { name: 'Notifications' })
    const [item] = within(list).getAllByRole('listitem')
    if (!item) {
      throw new Error('no item')
    }
    expect(within(item).getByTestId(`notification-icon-${type}`)).toBeInTheDocument()
    expect(item).toHaveTextContent(personal ? `${label} · Personal` : `${label} · Inventory tracker`)
    const anchor = within(item).getByRole('link', { name: title })
    expect(anchor).toHaveAttribute('href', link)

    await user.click(anchor)
    await waitFor(() => {
      expect(currentHref(router)).toBe(link)
    })
  })

  it('FR92_project_less_connection_invalid_in_the_bell_has_no_project_and_opens_connections', async () => {
    signInUser()
    seedNotification({
      type: 'connection_invalid',
      title: 'Your GitHub connection is invalid',
      body: 'GitHub rejected the token of @ada. Reconnect it under Connections.',
      link: '/connections',
      projectId: null,
      projectName: null,
    })
    const { router } = await renderRoute('/projects')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Notifications, 1 unread' }))
    const latest = await screen.findByRole('list', { name: 'Latest notifications' })
    const [item] = within(latest).getAllByRole('listitem')
    if (!item) {
      throw new Error('no item')
    }
    expect(item).toHaveTextContent('Connection invalid · Personal')
    expect(item).not.toHaveTextContent('Inventory tracker')
    await user.click(within(item).getByRole('link', { name: 'Your GitHub connection is invalid' }))
    await waitFor(() => {
      expect(currentHref(router)).toBe('/connections')
    })
  })

  it('FR92_preferences_list_the_new_types_on_by_default_and_muting_connection_invalid_notes_the_email', async () => {
    const me = signInUser()
    await renderRoute('/account')
    const user = userEvent.setup()

    const section = await screen.findByRole('region', { name: 'Notification preferences' })
    for (const { label } of fr92Cases) {
      expect(await within(section).findByLabelText(label)).toBeChecked()
    }
    const connection = within(section).getByLabelText('Connection invalid')
    expect(connection).toHaveAccessibleDescription(/also stops its email/)

    await user.click(connection)
    await user.click(within(section).getByLabelText('Preview ready'))
    await user.click(within(section).getByRole('button', { name: 'Save preferences' }))

    expect(await screen.findByText('Notification preferences saved.')).toBeInTheDocument()
    expect(requestsTo('PUT', '/api/me/notification-preferences')[0]?.body).toEqual({
      mutedTypes: ['preview_ready', 'connection_invalid'],
    })
    expect(notificationsDb.muted.get(me.id)).toEqual(['preview_ready', 'connection_invalid'])
    expect(within(section).getByLabelText('Connection invalid')).not.toBeChecked()
  })

  it('FR92_live_bell_refresh_mirrors_NotificationRules_ForEvent', () => {
    expect([...NOTIFYING_EVENT_TYPES].sort()).toEqual(
      [
        'GATE_CREATED',
        'GATE_OVERDUE',
        'ARTIFACT_CREATED',
        'ARTIFACT_RESTORED',
        'STEP_BLOCKED',
        'RUN_FAILED',
        'BUDGET_EXCEEDED',
        'REVIEW_COMPLETED',
        'RELEASE_COMPLETED',
        'RELEASE_FAILED',
        'RELEASE_AWAITING_MERGE',
        'ROLLBACK_COMPLETED',
        'DEPLOYMENT_FAILED',
        'DEPLOYMENT_PREVIEW_READY',
        'CONNECTION_INVALID',
        'LLM_BINDING_CLEARED',
      ].sort(),
    )
    // Not notifying: deployment bookkeeping, merges seen on GitHub, versions and presets.
    for (const t of ['DEPLOYMENT_CREATED', 'DEPLOYMENT_UPDATED', 'RELEASE_MERGED_EXTERNALLY', 'VERSION_STARTED', 'PRESET_CHANGED']) {
      expect(NOTIFYING_EVENT_TYPES.has(t)).toBe(false)
    }
  })
})
