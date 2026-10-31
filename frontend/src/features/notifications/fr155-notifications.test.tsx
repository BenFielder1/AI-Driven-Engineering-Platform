import { QueryClient } from '@tanstack/react-query'
import { screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import type { Schemas } from '../../api/client'
import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { seedNotification } from '../../mocks/handlers/notifications'
import { renderRoute } from '../../test/render'
import { invalidateForEvent } from '../live/live-invalidation'
import { NOTIFYING_EVENT_TYPES } from '../live/use-project-live'
import { notificationEvents, notificationTypeLabels, notificationTypes } from '../projects/labels'
import { projectKeys } from '../projects/projects-api'

const PROJECT = '00000000-0000-7000-8000-0000000000aa'

const cases: { name: string; type: Schemas['NotificationType']; label: string; title: string; link: string }[] = [
  {
    name: 'git_binding_cleared',
    type: 'git_binding_cleared',
    label: 'Git connection unbound',
    title: 'GitLab connection unbound',
    link: `/projects/${PROJECT}/settings/git`,
  },
  {
    name: 'database_provisioning_failed',
    type: 'database_provisioning_failed',
    label: 'Database setup failed',
    title: 'Production database setup failed',
    link: `/projects/${PROJECT}/settings/deployment`,
  },
  {
    name: 'database_expiring',
    type: 'database_expiring',
    label: 'Database expiring',
    title: 'Preview database expires on 8 Nov 2026',
    link: `/projects/${PROJECT}/settings/deployment`,
  },
  {
    name: 'migration_failed with a run',
    type: 'migration_failed',
    label: 'Migration failed',
    title: 'Production migration run failed',
    link: `/projects/${PROJECT}/migrations`,
  },
  {
    name: 'migration_failed in a preview pre-deploy',
    type: 'migration_failed',
    label: 'Migration failed',
    title: 'Preview migrations failed',
    link: `/projects/${PROJECT}/review`,
  },
  {
    name: 'migration_failed in a production pre-deploy',
    type: 'migration_failed',
    label: 'Migration failed',
    title: 'Production migrations failed',
    link: `/projects/${PROJECT}/release`,
  },
  {
    name: 'deployment_target_attention',
    type: 'deployment_target_attention',
    label: 'Deployment target needs attention',
    title: 'Frontend target on Netlify lost its Git access',
    link: `/projects/${PROJECT}/settings/deployment`,
  },
]

const newTypes = ['database_provisioning_failed', 'database_expiring', 'migration_failed', 'deployment_target_attention'] as const

describe('FR-155: notification types in the UI', () => {
  it('FR155_new_types_have_labels_and_are_in_the_preference_and_channel_lists', () => {
    for (const type of newTypes) {
      expect(notificationTypeLabels[type]).toBeTruthy()
      expect(notificationTypes).toContain(type)
      expect(notificationEvents).toContain(type)
    }
  })

  it.each(cases)('FR155_$name renders with its label, icon and server link', async ({ type, label, title, link }) => {
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

  it('FR155_events_that_notify_refresh_the_bell_live', () => {
    for (const type of ['GIT_BINDING_CLEARED', 'DATABASE_PROVISIONING_FAILED', 'DATABASE_EXPIRING', 'MIGRATION_RUN_FAILED', 'DEPLOYMENT_TARGET_FAILED']) {
      expect(NOTIFYING_EVENT_TYPES.has(type)).toBe(true)
    }
    for (const type of ['STACK_CHANGED', 'DATABASE_PROVISIONED', 'MIGRATION_RUN_COMPLETED', 'PREVIEW_ROLLOUT_COMPLETED']) {
      expect(NOTIFYING_EVENT_TYPES.has(type)).toBe(false)
    }
  })

  it.each(['release', 'project'] as const)('FR155_live_%s_category_events_refresh_the_whole_project', async (category) => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    await invalidateForEvent(client, PROJECT, category, 'DATABASE_EXPIRING')
    expect(invalidate).toHaveBeenCalledWith({ queryKey: projectKeys.detail(PROJECT) }, undefined)
  })
})
