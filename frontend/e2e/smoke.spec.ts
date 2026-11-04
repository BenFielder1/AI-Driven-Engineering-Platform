// FR-197: copy that names the product says Tevrin (it said AIDE before v6); the assertions below use the new copy.
import { expect, test } from '@playwright/test'

test('the API is ready behind the same-origin /api proxy', async ({ request }) => {
  const response = await request.get('/api/health/ready')
  expect(response.status()).toBe(200)
  expect(await response.json()).toMatchObject({ status: 'Healthy' })
})

test('a signed-out visitor to / lands on the sign-in page', async ({ page }) => {
  await page.goto('/')

  // FR-207: `/` sends people to Home, so a signed-out visitor returns there after sign-in.
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fhome$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Tevrin', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Skip to content' })).toBeAttached()
})
