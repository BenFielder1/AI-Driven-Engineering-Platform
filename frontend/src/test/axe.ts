/**
 * FR-205: axe-core in Vitest. `axeViolations(container)` runs axe on a rendered component (WCAG 2.x A/AA and 2.2 AA
 * rules) and returns the violations as short strings, so a failing test names the rule and the node.
 *
 * jsdom has no layout or computed colours, so `color-contrast` is off here: contrast is asserted on the tokens
 * (src/lib/ui/tokens.test.ts) and by axe in Playwright (e2e/a11y.spec.ts). `region` is off because a component renders
 * outside the app's landmarks, and so are the page-level heading and main-landmark checks.
 *
 * `THEMES` / `setTheme(theme)` put `.light` or `.dark` on <html> the way the init script does, for "renders in both
 * themes" tests.
 */
import axe from 'axe-core'

export const THEMES = ['light', 'dark'] as const
export type TestTheme = (typeof THEMES)[number]

export function setTheme(theme: TestTheme): void {
  const root = document.documentElement
  root.classList.toggle('dark', theme === 'dark')
  root.classList.toggle('light', theme === 'light')
  root.style.colorScheme = theme
}

export async function axeViolations(container: Element): Promise<string[]> {
  const results = await axe.run(container, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'] },
    rules: {
      'color-contrast': { enabled: false },
      region: { enabled: false },
      'page-has-heading-one': { enabled: false },
      'landmark-one-main': { enabled: false },
    },
  })
  return results.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${n.target.join(' ')} (${v.help})`))
}
