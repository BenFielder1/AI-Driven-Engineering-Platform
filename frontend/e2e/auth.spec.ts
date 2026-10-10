import { expect, test, type Page } from '@playwright/test'

import { password, registerViaApi, registerViaUi } from './support/api.ts'

// FR-1 end to end against a real API: register → me → sign out → sign in (and a wrong password).
// Register is rate limited per IP (5/min) and other specs register users too: every registration is serialised
// through support/api.ts's register lock and waits out a 429 (Retry-After). Only FR-1 registers through the form.

async function openUserMenu(page: Page) {
  await page.getByRole('button', { name: /Account menu for/ }).click()
}

test('FR-1: register, see yourself, sign out, sign back in', async ({ page }) => {
  const name = 'E2E User'

  // Register → signed in, on /projects.
  const { email } = await registerViaUi(page, 'auth', name)
  await expect(page).toHaveURL(/\/projects$/)

  // GET /api/auth/me returns the new user (cookie session).
  const me = await page.request.get('/api/auth/me')
  expect(me.status()).toBe(200)
  expect(await me.json()).toMatchObject({ email, name })

  // Account page shows the profile.
  await openUserMenu(page)
  await page.getByRole('link', { name: 'Account' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Account', exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Profile' }).getByText(email)).toBeVisible()

  // Sign out → /login; the session is gone.
  await openUserMenu(page)
  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to AIDE', exact: true })).toBeVisible()
  expect((await page.request.get('/api/auth/me')).status()).toBe(401)

  // Guarded page → /login?returnUrl=…
  await page.goto('/account')
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Faccount$/)

  // Wrong password → INVALID_CREDENTIALS copy.
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('not-the-password1')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByText('The email or password is incorrect.')).toBeVisible()

  // Correct password → back to the returnUrl.
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Account', exact: true })).toBeVisible()
  await expect(page).toHaveURL(/\/account$/)
})

test('FR-4: create a token (shown once) and revoke it', async ({ page, context }) => {
  await registerViaApi(context, 'tok', 'Token User')
  await page.goto('/account')
  await expect(page.getByRole('heading', { level: 1, name: 'Account', exact: true })).toBeVisible()

  await page.getByLabel('Name', { exact: true }).fill('E2E CLI')
  await page.getByRole('button', { name: 'Create token' }).click()
  await expect(page.getByLabel('Your new token')).toHaveValue(/^aide_pat_/)
  await page.getByRole('button', { name: 'Done' }).click()
  await expect(page.getByLabel('Your new token')).toHaveCount(0)

  await page.getByRole('button', { name: 'Revoke E2E CLI' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Revoke token' }).click()
  await expect(page.getByRole('list', { name: 'API tokens' }).getByText('Revoked')).toBeVisible()
})
