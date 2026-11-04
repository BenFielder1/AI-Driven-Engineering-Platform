import { expect, test, type Page } from '@playwright/test'

import { password, registerViaApi, registerViaUi } from './support/api.ts'

/**
 * v6 M40: Home (`/home`) is the signed-in landing page (FR-207, §4.32 routing table), and the shell gained a Home item,
 * the theme and Docs in the user menu (FR-206, FR-204) and palette actions (FR-209). Register is rate limited: every
 * registration goes through support/api.ts's register lock.
 */

test('FR207_home: a newly registered user lands on /home and sees the Get set up checklist', async ({ page }) => {
  await registerViaUi(page, 'home', 'Hana Home')

  await expect(page).toHaveURL(/\/home$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Hana', exact: true })).toBeVisible()

  // §4.30: a fresh user has done none of the four items.
  const checklist = page.getByRole('region', { name: 'Get set up' })
  await expect(checklist).toBeVisible()
  await expect(checklist.getByTestId('checklist-progress')).toHaveText('0/4 done')

  // Recent projects is empty for a new user, with the create and import actions.
  const recent = page.getByRole('region', { name: 'Recent projects' })
  await expect(recent.getByRole('heading', { name: 'No projects yet' })).toBeVisible()
  await expect(recent.getByRole('link', { name: 'Create project' })).toBeVisible()
})

test('FR207_home: /home signed out redirects to /login?returnUrl=%2Fhome, and signing in returns there', async ({ page, browser }) => {
  const setup = await browser.newContext()
  const user = await registerViaApi(setup, 'home-out', 'Otto Home')
  await setup.close()

  await page.goto('/home')
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fhome$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Tevrin', exact: true })).toBeVisible()

  await page.getByLabel('Email').fill(user.email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/home$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Otto', exact: true })).toBeVisible()
})

async function openUserMenu(page: Page) {
  await expect(page.getByRole('menu')).toHaveCount(0)
  await page.getByRole('complementary', { name: 'Sidebar' }).getByRole('button', { name: /Account menu for/ }).click()
  await expect(page.getByRole('menu')).toBeVisible()
}

test('FR206_shell, FR209_palette: Home in the sidebar, theme and Docs in the user menu, and the palette actions', async ({
  page,
  context,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await registerViaApi(context, 'shell')
  await page.goto('/projects')
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()

  // FR-206: the Home item goes to /home and becomes the current page.
  const sidebar = page.getByRole('complementary', { name: 'Sidebar' })
  await sidebar.getByRole('link', { name: 'Home', exact: true }).click()
  await expect(page).toHaveURL(/\/home$/)
  await expect(sidebar.getByRole('link', { name: 'Home', exact: true })).toHaveAttribute('aria-current', 'page')

  // FR-204, FR-206: the theme is a radio group in the user menu.
  await openUserMenu(page)
  const menu = page.getByRole('menu')
  await expect(menu.getByRole('menuitemradio', { name: 'System' })).toBeVisible()
  await menu.getByRole('menuitemradio', { name: 'Dark' }).click()
  await expect(page.locator('html')).toHaveClass(/(^|\s)dark(\s|$)/)
  await openUserMenu(page)
  await expect(page.getByRole('menu').getByRole('menuitemradio', { name: 'Dark' })).toHaveAttribute('aria-checked', 'true')
  await page.getByRole('menu').getByRole('menuitemradio', { name: 'Light' }).click()
  await expect(page.locator('html')).toHaveClass(/(^|\s)light(\s|$)/)

  // FR-206: Docs opens in a new tab.
  await openUserMenu(page)
  const docs = page.getByRole('menu').getByRole('menuitem', { name: /^Docs/ })
  await expect(docs).toHaveAttribute('target', '_blank')
  await expect(docs).toHaveAttribute('rel', /noopener/)
  await page.keyboard.press('Escape')
  await expect(page.getByRole('menu')).toHaveCount(0)

  // FR-209: the palette's actions. "New project" navigates; Switch theme applies at once.
  await page.goto('/projects')
  await expect(page.getByRole('heading', { level: 1, name: 'Projects', exact: true })).toBeVisible()
  await sidebar.getByRole('button', { name: 'Search pages' }).click()
  const palette = page.getByRole('dialog', { name: 'Command palette' })
  await expect(palette).toBeVisible()
  for (const action of ['Go to Home', 'New project', 'Import repository', 'Switch theme: Light', 'Switch theme: Dark', 'Switch theme: System']) {
    await expect(palette.getByRole('option', { name: action, exact: true })).toBeVisible()
  }
  await expect(palette.getByRole('option', { name: /^Open docs/ })).toBeVisible()
  await palette.getByRole('option', { name: 'Switch theme: Dark', exact: true }).click()
  await expect(palette).toHaveCount(0)
  await expect(page.locator('html')).toHaveClass(/(^|\s)dark(\s|$)/)

  await sidebar.getByRole('button', { name: 'Search pages' }).click()
  await expect(palette).toBeVisible()
  await palette.getByRole('option', { name: 'Go to Home', exact: true }).click()
  await expect(page).toHaveURL(/\/home$/)
  await expect(page.getByRole('heading', { level: 1, name: /^Welcome, / })).toBeVisible()
})
