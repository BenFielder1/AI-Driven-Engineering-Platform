import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { Schemas } from '../../api/client'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedNotification } from '../../mocks/handlers/notifications'
import { renderRoute } from '../../test/render'
import { NOTIFYING_EVENT_TYPES } from '../live/use-project-live'
import { notificationEventLabels, notificationEvents, notificationTypeLabels, notificationTypes, personalNotificationTypes } from '../projects/labels'

const PROJECT = '00000000-0000-7000-8000-0000000000aa'

const cases: { name: string; type: Schemas['NotificationType']; label: string; title: string; link: string }[] = [
  {
    name: 'stack_profile_ready for a draft',
    type: 'stack_profile_ready',
    label: 'Stack profile ready for review',
    title: 'Inventory tracker: stack profile v1 is ready for review',
    link: `/projects/${PROJECT}/analysis`,
  },
  {
    name: 'stack_profile_ready for a failed run',
    type: 'stack_profile_ready',
    label: 'Stack profile ready for review',
    title: 'Inventory tracker: the Stack Analyst run failed',
    link: `/projects/${PROJECT}/analysis`,
  },
  {
    name: 'tracker_import_failed',
    type: 'tracker_import_failed',
    label: 'Tracker import failed',
    title: 'Inventory tracker: import from Jira failed',
    link: `/projects/${PROJECT}/refinement`,
  },
]

describe('FR-193: 0.5.0 notification types in the UI', () => {
  it('FR193_new_types_have_labels_and_are_in_the_preference_and_channel_lists', () => {
    for (const type of ['stack_profile_ready', 'tracker_import_failed'] as const) {
      expect(notificationTypeLabels[type]).toBeTruthy()
      expect(notificationEventLabels[type]).toBeTruthy()
      expect(notificationTypes).toContain(type)
      expect(notificationEvents).toContain(type)
    }
    expect(personalNotificationTypes).toEqual(['connection_invalid'])
  })

  it.each(cases)('FR193_$name renders with its label, icon and server link', async ({ type, label, title, link }) => {
    seedUser({ email: 'root@example.com', systemRole: 'admin' })
    signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    seedNotification({ type, title, link, projectId: PROJECT, projectName: 'Inventory tracker' })
    await renderRoute('/notifications')

    const list = await screen.findByRole('list', { name: 'Notifications' })
    const [item] = within(list).getAllByRole('listitem')
    if (!item) {
      throw new Error('no item')
    }
    expect(within(item).getByTestId(`notification-icon-${type}`)).toBeInTheDocument()
    expect(item).toHaveTextContent(`${label} · Inventory tracker`)
    expect(within(item).getByRole('link', { name: title })).toHaveAttribute('href', link)
  })

  it('FR165_an_expiring_tracker_token_shows_as_a_personal_connection_notification', async () => {
    seedUser({ email: 'root@example.com', systemRole: 'admin' })
    signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
    seedNotification({ type: 'connection_invalid', title: 'Your Jira connection expires on 2026-11-16', link: '/connections', projectId: null, projectName: null })
    await renderRoute('/notifications')

    const list = await screen.findByRole('list', { name: 'Notifications' })
    const [item] = within(list).getAllByRole('listitem')
    if (!item) {
      throw new Error('no item')
    }
    expect(within(item).getByRole('link', { name: 'Your Jira connection expires on 2026-11-16' })).toHaveAttribute('href', '/connections')
  })

  it('FR193_a_failed_import_refreshes_the_bell_live', () => {
    expect(NOTIFYING_EVENT_TYPES.has('TRACKER_IMPORT_FAILED')).toBe(true)
    for (const type of ['TRACKER_IMPORT_COMPLETED', 'SOURCE_ITEMS_REMOVED', 'STACK_PROFILE_APPROVED', 'DEPLOYMENT_GROUP_CONFIGURED', 'COMMIT_CREDIT_CHANGED']) {
      expect(NOTIFYING_EVENT_TYPES.has(type)).toBe(false)
    }
  })
})
