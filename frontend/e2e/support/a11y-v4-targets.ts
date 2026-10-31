import { expect, type Page } from '@playwright/test'

import type { A11yFixture } from './a11y-fixture.ts'
import { stepHeading, wizardTo, type A11yTarget } from './a11y-v2-targets.ts'

/** Picks an option of the Stack step's current layer and goes on to the next layer. */
async function pickLayer(page: Page, option: string, nextLayer: string) {
  await page.getByRole('radio', { name: option, exact: true }).check()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('heading', { level: 3, name: nextLayer, exact: true })).toBeVisible()
}

async function composing(page: Page) {
  await wizardTo(page, 5)
  await page.getByRole('radio', { name: /^Choose the stack/ }).check()
  await expect(page.getByRole('heading', { level: 3, name: 'Frontend', exact: true })).toBeVisible()
}

/**
 * SPEC §8 v4: the axe checks of the Stack step (composing, with the forbidden options and their reasons open, and the
 * Deployment layer with the summary), the AI model section with its Advanced overrides open, and Settings → Stack and
 * Settings → Deployment of a project with hosts (target cards, database cards and the environment inventory). Connections
 * is the v2 target (its fixture user now has Vercel, Netlify and Render connections).
 */
export function v4Targets(fixture: () => A11yFixture): A11yTarget[] {
  const stackProject = (path: string) => () => `/projects/${fixture().stackProjectId}${path}`
  return [
    {
      name: 'wizard Stack step (Frontend layer)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: composing,
    },
    {
      name: 'wizard Stack step (Data layer with the unavailable options and their reasons)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: async (p) => {
        await composing(p)
        await pickLayer(p, 'React + Vite', 'Backend')
        await pickLayer(p, 'ASP.NET Core', 'Data')
        const data = p.getByRole('group', { name: 'Data', exact: true })
        await data.getByText(/^Not available with your choices \(\d+\)$/).click()
        await expect(data.getByRole('radio', { name: 'Prisma', exact: true })).toBeVisible()
      },
    },
    {
      name: 'wizard Stack step (Deployment layer and summary)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: async (p) => {
        await composing(p)
        await pickLayer(p, 'React + Vite', 'Backend')
        await pickLayer(p, 'ASP.NET Core', 'Data')
        await pickLayer(p, 'EF Core + PostgreSQL', 'Version control')
        await pickLayer(p, 'GitHub', 'Deployment')
        await p.getByRole('group', { name: 'Frontend host' }).getByRole('radio', { name: 'Netlify', exact: true }).check()
        await expect(p.getByRole('region', { name: 'Stack summary' })).toContainText('Backend engineer')
      },
    },
    {
      name: 'wizard AI model section (Advanced open)',
      path: () => '/projects/new',
      signedIn: true,
      ready: (p) => stepHeading(p, 1),
      prepare: async (p) => {
        await wizardTo(p, 6)
        const section = p.getByRole('region', { name: 'AI model' })
        await expect(section.getByRole('radio').first()).toBeVisible()
        await section.getByText('Advanced: model per agent (optional)').click()
        await expect(section.getByLabel('Code generator', { exact: true })).toBeVisible()
      },
    },
    {
      name: 'settings (stack)',
      path: stackProject('/settings/stack'),
      signedIn: true,
      ready: (p) => p.getByRole('region', { name: 'Current stack' }),
    },
    {
      name: 'settings (deployment with two targets and two databases)',
      path: stackProject('/settings/deployment'),
      signedIn: true,
      ready: (p) => p.getByTestId('target-backend'),
      prepare: async (p) => {
        await expect(p.getByTestId('target-frontend')).toBeVisible()
        await expect(p.getByTestId('database-production')).toBeVisible()
        await expect(p.getByTestId('database-preview')).toBeVisible()
      },
    },
  ]
}
