// FR-197: copy that names the product says Tevrin (it said AIDE before v6); the assertions below use the new copy.
import { expect, test } from '@playwright/test'

test('the API is ready behind the same-origin /api proxy', async ({ request }) => {
  const response = await request.get('/api/health/ready')
  expect(response.status()).toBe(200)
  expect(await response.json()).toMatchObject({ status: 'Healthy' })
})

test('FR-211: a signed-out visitor to / gets the marketing Home, with Sign in and Get started', async ({ page }) => {
  await page.goto('/')

  // v6: `/` is the marketing Home for visitors (it redirected to /login before v6).
  await expect(page).toHaveURL(/\/$/)
  await expect(
    page.getByRole('heading', { level: 1, name: 'Ship software with AI agents, under human control.', exact: true }),
  ).toBeVisible()
  const header = page.getByTestId('marketing-header')
  await expect(header.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible()
  await expect(header.getByRole('link', { name: 'Get started', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Skip to content' })).toBeAttached()
})
