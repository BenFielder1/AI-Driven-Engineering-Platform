import { expect, test } from '@playwright/test'

test('home page loads and shows the API readiness', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { level: 1, name: 'AIDE Platform' })).toBeVisible()
  await expect(page.getByRole('status')).toBeVisible()
})
