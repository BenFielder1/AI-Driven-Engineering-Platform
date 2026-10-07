import { expect, test, type Page } from '@playwright/test'

// FR-1 end to end against a real API: register → me → sign out → sign in (and a wrong password).
// One registration per run (register is rate limited per IP).

const password = 'e2e-password1'

async function openUserMenu(page: Page) {
  await page.getByRole('button', { name: /Account menu for/ }).click()
}

test('FR-1: register, see yourself, sign out, sign back in', async ({ page }) => {
  const email = `e2e-${String(Date.now())}-${String(Math.floor(Math.random() * 1e6))}@example.com`
  const name = 'E2E User'

  // Register → signed in, on /projects.
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Name', { exact: true }).fill(name)
  await page.getByLabel(/^Password/).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()
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

test('FR-4: create a token (shown once) and revoke it', async ({ page }) => {
  const email = `e2e-tok-${String(Date.now())}-${String(Math.floor(Math.random() * 1e6))}@example.com`
  await page.goto('/register?returnUrl=%2Faccount')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Name', { exact: true }).fill('Token User')
  await page.getByLabel(/^Password/).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
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
