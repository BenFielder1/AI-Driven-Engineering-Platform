import { expect, test } from '@playwright/test'

import { api, registerViaApi } from './support/api.ts'

/**
 * FR-51, FR-3, §7: the admin area is forbidden for non-admins — no Admin link, the forbidden view on every /admin route,
 * and 403 from the admin API. (The k8s database already has an Admin, so a fresh user is a plain User.)
 */
test('FR-51: a non-admin sees no Admin link, gets the forbidden page and 403 from the admin API', async ({ page, context }) => {
  await registerViaApi(context, 'non-admin')
  const me = (await (await api(context, 'GET', '/api/auth/me')).json()) as { systemRole: string }
  expect(me.systemRole).toBe('user')

  await page.goto('/projects')
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Admin', exact: true })).toHaveCount(0)

  for (const path of ['/admin', '/admin/events', '/admin/runs', '/admin/dead-letters', '/admin/users']) {
    await page.goto(path)
    await expect(page.getByRole('heading', { level: 1, name: "You don't have access to this page" })).toBeVisible()
    await expect(page.getByText('The admin area is only available to administrators.')).toBeVisible()
  }

  for (const path of ['/api/admin/events', '/api/admin/runs', '/api/admin/dead-letters', '/api/admin/users']) {
    expect((await api(context, 'GET', path)).status(), path).toBe(403)
  }
  const selfPromote = await api(context, 'PATCH', `/api/admin/users/${(me as unknown as { id: string }).id}/role`, {
    systemRole: 'admin',
  })
  expect(selfPromote.status()).toBe(403)
})
