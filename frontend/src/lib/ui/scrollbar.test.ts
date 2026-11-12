import { describe, expect, it } from 'vitest'

import css from '../../index.css?raw'

/** FR-235 (SPEC §4.39): the themed, CSS-only scrollbar. Reads index.css the same way tokens.test.ts does. */

function block(selector: RegExp): string {
  const match = selector.exec(css)
  if (!match) {
    throw new Error(`Block ${String(selector)} not found in index.css`)
  }
  const start = match.index + match[0].length
  return css.slice(start, css.indexOf('}', start))
}

const themes = {
  light: block(/:root,\s*\.light\s*\{/),
  dark: block(/\n\.dark\s*\{/),
} as const

const expected = {
  light: {
    'scrollbar-track': 'var(--background)',
    'scrollbar-thumb': 'oklch(0.869 0.022 252.9)',
    'scrollbar-thumb-hover': 'var(--field-border)',
  },
  dark: {
    'scrollbar-track': 'var(--background)',
    'scrollbar-thumb': 'oklch(0.446 0.043 257.3)',
    'scrollbar-thumb-hover': 'var(--muted-foreground)',
  },
} as const

/** The declarations of the first rule whose selector is exactly `selector`. */
function rule(selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  return block(new RegExp(`(?:^|[\\s,}])${escaped}\\s*\\{`))
}

describe('FR-235: themed scrollbar', () => {
  for (const theme of ['light', 'dark'] as const) {
    it(`FR235_tokens_${theme}: the ${theme} block defines the scrollbar track, thumb and hover tokens`, () => {
      for (const [name, value] of Object.entries(expected[theme])) {
        expect(themes[theme], `--${name} (${theme})`).toContain(`--${name}: ${value};`)
      }
    })
  }

  it('FR235_page: the scrollbar is 10px, the thumb a 999px pill, and the page track and thumb gap use the track colour', () => {
    expect(rule('::-webkit-scrollbar')).toMatch(/width: 10px;[\s\S]*height: 10px;/)
    const thumb = rule('::-webkit-scrollbar-thumb')
    expect(thumb).toContain('background-color: var(--scrollbar-thumb);')
    expect(thumb).toContain('border-radius: 999px;')
    expect(rule('::-webkit-scrollbar-thumb:hover')).toContain('background-color: var(--scrollbar-thumb-hover);')
    expect(rule('html::-webkit-scrollbar-corner')).toContain('background: var(--scrollbar-track);')
    expect(rule('html::-webkit-scrollbar-thumb')).toContain('border-color: var(--scrollbar-track);')
  })

  it('FR235_inner: inner scroll areas get a transparent track and an inset, padding-box thumb', () => {
    expect(rule('::-webkit-scrollbar-corner')).toContain('background: transparent;')
    const thumb = rule('::-webkit-scrollbar-thumb')
    expect(thumb).toContain('border: 2px solid transparent;')
    expect(thumb).toContain('background-clip: padding-box;')
  })

  it('FR235_firefox: thin scrollbar-color only where ::-webkit-scrollbar is unsupported (Chromium would drop the pill)', () => {
    const start = css.indexOf('@supports not selector(::-webkit-scrollbar)')
    expect(start).toBeGreaterThan(-1)
    const firefox = css.slice(start)
    expect(firefox).toMatch(/\*\s*\{\s*scrollbar-width: thin;\s*scrollbar-color: var\(--scrollbar-thumb\) transparent;/)
    expect(firefox).toMatch(/html\s*\{\s*scrollbar-color: var\(--scrollbar-thumb\) var\(--scrollbar-track\);/)
    expect(css.slice(0, start)).not.toMatch(/scrollbar-(width|color):/)
  })
})
