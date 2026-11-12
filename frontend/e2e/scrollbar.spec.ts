import { expect, test } from '@playwright/test'

/**
 * 0.6.1 M47: the themed, CSS-only scrollbar (FR-235, SPEC §4.39). Chromium only (the default project runs every spec).
 *
 * Headless Chromium launched by Playwright hides scrollbars (`--hide-scrollbars`), so the rendered gutter can't be
 * measured reliably. The checks read the shipped stylesheet through the CSSOM instead: Chromium parses the
 * `::-webkit-scrollbar` rule, and its width is 10px. Where a gutter is rendered (headed runs), it must be 10px too.
 */

interface ScrollbarRule {
  selector: string
  width: string
  height: string
}

test('FR235_webkit_width: the page scrollbar is 10px wide on html, and the colours follow the theme class', async ({ page }) => {
  await page.goto('/faq')
  await expect(page.getByRole('heading', { level: 1, name: 'Frequently asked questions' })).toBeVisible()

  const rules = await page.evaluate(() => {
    const found: ScrollbarRule[] = []
    const visit = (list: CSSRuleList) => {
      for (const r of Array.from(list)) {
        if (r instanceof CSSStyleRule && r.selectorText.split(',').some((s) => s.trim() === '::-webkit-scrollbar')) {
          found.push({ selector: r.selectorText, width: r.style.width, height: r.style.height })
        }
        if ('cssRules' in r && r.cssRules instanceof CSSRuleList) {
          visit(r.cssRules)
        }
      }
    }
    for (const sheet of Array.from(document.styleSheets)) {
      visit(sheet.cssRules)
    }
    return found
  })
  expect(rules.length, 'a ::-webkit-scrollbar rule ships in the stylesheet').toBeGreaterThan(0)
  for (const r of rules) {
    expect(r).toMatchObject({ width: '10px', height: '10px' })
  }

  // Where the browser renders a classic scrollbar gutter, it is the 10px from the rule.
  const gutter = await page.evaluate(() => window.innerWidth - document.documentElement.clientWidth)
  if (gutter > 0) {
    expect(gutter).toBe(10)
  }

  // The colours are custom properties on the theme class, so they switch with no reload.
  const tokens = () =>
    page.evaluate(() => {
      const style = getComputedStyle(document.documentElement)
      return {
        track: style.getPropertyValue('--scrollbar-track').trim(),
        thumb: style.getPropertyValue('--scrollbar-thumb').trim(),
        hover: style.getPropertyValue('--scrollbar-thumb-hover').trim(),
        background: style.getPropertyValue('--background').trim(),
      }
    })
  const setTheme = (theme: 'light' | 'dark') =>
    page.evaluate((t) => {
      document.documentElement.classList.remove('light', 'dark')
      document.documentElement.classList.add(t)
    }, theme)

  await setTheme('light')
  const light = await tokens()
  await setTheme('dark')
  const dark = await tokens()
  for (const values of [light, dark]) {
    expect(values.thumb).not.toBe('')
    expect(values.hover).not.toBe('')
    expect(values.track).toBe(values.background)
  }
  expect(dark.thumb).not.toBe(light.thumb)
  expect(dark.track).not.toBe(light.track)
})
