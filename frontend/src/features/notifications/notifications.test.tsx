import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { seedUser, signInAs } from '../../mocks/handlers/auth'
import { notificationsDb, seedNotification, seedNotifications } from '../../mocks/handlers/notifications'
import { currentHref, renderRoute } from '../../test/render'
import { requestsTo } from '../../test/project-settings'
import { PAGE_SIZE } from './notifications-api'

function nth(items: HTMLElement[], i: number): HTMLElement {
  const item = items[i]
  if (!item) {
    throw new Error(`No item ${String(i)}`)
  }
  return item
}

function signInUser() {
  seedUser({ email: 'root@example.com', systemRole: 'admin' })
  return signInAs({ email: 'ada@example.com', name: 'Ada Lovelace', systemRole: 'user' })
}

describe('Notifications (FR-47)', () => {
  it('FR-47: the header bell shows the unread count and the latest notifications', async () => {
    signInUser()
    seedNotification({ title: 'Old and read', readAt: '2026-09-01T12:30:00Z' })
    seedNotification({ title: 'Release completed', type: 'release_completed', projectName: 'Inventory tracker' })
    seedNotification({ title: 'Plan approval needs a decision' })
    renderRoute('/projects')
    const user = userEvent.setup()

    const bell = await screen.findByRole('button', { name: 'Notifications, 2 unread' })
    expect(bell).toHaveAttribute('aria-expanded', 'false')

    await user.click(bell)
    expect(bell).toHaveAttribute('aria-expanded', 'true')
    const latest = await screen.findByRole('list', { name: 'Latest notifications' })
    const items = within(latest).getAllByRole('listitem')
    expect(items).toHaveLength(3)
    expect(items[0]).toHaveTextContent('Plan approval needs a decision')
    // Unread state is text, not colour alone.
    expect(within(nth(items, 0)).getByText('Unread')).toBeInTheDocument()
    expect(within(nth(items, 2)).queryByText('Unread')).not.toBeInTheDocument()
    expect(within(nth(items, 1)).getByText(/Release completed · Inventory tracker/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'View all notifications' })).toHaveAttribute('href', '/notifications')

    await user.keyboard('{Escape}')
    expect(bell).toHaveAttribute('aria-expanded', 'false')
    expect(bell).toHaveFocus()
  })

  it('FR-47: with nothing unread the bell says 0 unread and the dropdown is empty', async () => {
    signInUser()
    renderRoute('/projects')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Notifications, 0 unread' }))
    expect(await screen.findByText("You're all caught up")).toBeInTheDocument()
  })

  it('FR-47: opening a notification marks it read and goes to its link', async () => {
    signInUser()
    const n = seedNotification({ title: 'Open the project list', link: '/projects' })
    const { router } = renderRoute('/notifications')
    const user = userEvent.setup()

    await user.click(await screen.findByRole('link', { name: 'Open the project list' }))

    await waitFor(() => {
      expect(currentHref(router)).toBe('/projects')
    })
    expect(requestsTo('PATCH', `/api/me/notifications/${n.id}/read`)).toHaveLength(1)
    expect(await screen.findByRole('button', { name: 'Notifications, 0 unread' })).toBeInTheDocument()
  })

  it('FR-47: mark one read, load more, then mark all as read', async () => {
    signInUser()
    seedNotifications(PAGE_SIZE + 3)
    renderRoute('/notifications')
    const user = userEvent.setup()

    const list = await screen.findByRole('list', { name: 'Notifications' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(PAGE_SIZE)
    expect(within(list).getAllByRole('listitem')[0]).toHaveTextContent(`Notification ${String(PAGE_SIZE + 3)}`)
    expect(screen.getByText(`${String(PAGE_SIZE + 3)} unread`)).toBeInTheDocument()

    await user.click(
      within(list).getByRole('button', { name: `Mark "Notification ${String(PAGE_SIZE + 3)}" as read` }),
    )
    expect(await screen.findByText(`${String(PAGE_SIZE + 2)} unread`)).toBeInTheDocument()
    expect(
      await screen.findByRole('button', { name: `Notifications, ${String(PAGE_SIZE + 2)} unread` }),
    ).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Load more' }))
    await waitFor(() => {
      expect(within(list).getAllByRole('listitem')).toHaveLength(PAGE_SIZE + 3)
    })
    expect(within(list).getAllByRole('listitem').at(-1)).toHaveTextContent('Notification 1')
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Mark all as read' }))
    expect(await screen.findByText('All notifications are marked as read.')).toBeInTheDocument()
    expect(await screen.findByText('No unread notifications')).toBeInTheDocument()
    expect(within(list).queryByText('Unread')).not.toBeInTheDocument()
    expect(notificationsDb.items.every((n) => n.readAt !== null)).toBe(true)
  })

  it('FR-47: "Unread only" lists unread notifications only, with an empty state', async () => {
    signInUser()
    seedNotification({ title: 'Already read', readAt: '2026-09-01T12:30:00Z' })
    seedNotification({ title: 'Still unread' })
    const { router } = renderRoute('/notifications')
    const user = userEvent.setup()

    expect(await screen.findByText('Already read')).toBeInTheDocument()
    await user.click(screen.getByLabelText('Unread only'))

    await waitFor(() => {
      expect(screen.queryByText('Already read')).not.toBeInTheDocument()
    })
    expect(screen.getByText('Still unread')).toBeInTheDocument()
    expect(currentHref(router)).toBe('/notifications?unread=true')

    await user.click(screen.getByRole('button', { name: 'Mark "Still unread" as read' }))
    expect(await screen.findByText("You're all caught up.")).toBeInTheDocument()
  })

  it('FR-47: shows an empty state when there are no notifications', async () => {
    signInUser()
    renderRoute('/notifications')

    expect(await screen.findByText('No notifications yet')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Mark all as read' })).toBeDisabled()
  })

  it('FR-47 (§2): preferences save the unchecked types as muted', async () => {
    const me = signInUser()
    notificationsDb.muted.set(me.id, ['run_failed'])
    renderRoute('/account')
    const user = userEvent.setup()

    const section = await screen.findByRole('region', { name: 'Notification preferences' })
    const runFailed = await within(section).findByLabelText('Agent run failed')
    expect(runFailed).not.toBeChecked()
    expect(within(section).getByLabelText('Gate reminder (after 24 hours)')).toBeChecked()

    await user.click(runFailed)
    await user.click(within(section).getByLabelText('Rollback'))
    await user.click(within(section).getByRole('button', { name: 'Save preferences' }))

    // SPEC 7.1: the result is a toast.
    expect(await screen.findByText('Notification preferences saved.')).toBeInTheDocument()
    expect(requestsTo('PUT', '/api/me/notification-preferences')[0]?.body).toEqual({ mutedTypes: ['rollback'] })
    expect(within(section).getByLabelText('Rollback')).not.toBeChecked()
    expect(within(section).getByLabelText('Agent run failed')).toBeChecked()
  })
})
