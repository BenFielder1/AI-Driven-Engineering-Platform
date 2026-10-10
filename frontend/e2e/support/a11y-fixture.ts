import type { BrowserContextOptions } from '@playwright/test'

/** Where a11y.setup.ts leaves the signed-in state and project ids for a11y.spec.ts (inside the git-ignored output dir). */
export const A11Y_FIXTURE = 'test-results/a11y-fixture.json'

export interface A11yFixture {
  storageState: Exclude<BrowserContextOptions['storageState'], string | undefined>
  projectId: string
  diffProjectId: string
}
