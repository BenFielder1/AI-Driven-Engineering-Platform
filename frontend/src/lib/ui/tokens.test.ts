import { describe, expect, it } from 'vitest'

import css from '../../index.css?raw'
import { contrastRatio, oklchToSrgb, parseOklch, relativeLuminance, WCAG_AA_NON_TEXT, WCAG_AA_TEXT } from './contrast'
import { NON_TEXT_PAIRS, RADIUS, TEXT_PAIRS, TOKEN_NAMES, tokens, type ThemeName } from './tokens'

const THEMES: ThemeName[] = ['dark', 'light']

describe('oklch colour maths', () => {
  it('parses oklch() strings and rejects anything else', () => {
    expect(parseOklch('oklch(0.78 0.18 195)')).toEqual({ l: 0.78, c: 0.18, h: 195 })
    expect(parseOklch('oklch(50% 0 0)')).toEqual({ l: 0.5, c: 0, h: 0 })
    expect(() => parseOklch('#fff')).toThrow()
  })

  it('maps white and black to the luminance extremes (21:1)', () => {
    expect(relativeLuminance(oklchToSrgb(parseOklch('oklch(1 0 0)')))).toBeCloseTo(1, 3)
    expect(relativeLuminance(oklchToSrgb(parseOklch('oklch(0 0 0)')))).toBeCloseTo(0, 6)
    expect(contrastRatio('oklch(1 0 0)', 'oklch(0 0 0)')).toBeCloseTo(21, 1)
  })

  it('is symmetric', () => {
    expect(contrastRatio('oklch(0.97 0.005 260)', 'oklch(0.06 0.01 260)')).toBe(
      contrastRatio('oklch(0.06 0.01 260)', 'oklch(0.97 0.005 260)'),
    )
  })
})

describe('NFR-A11Y2: design tokens meet WCAG 2.2 AA in both themes', () => {
  for (const theme of THEMES) {
    for (const [fg, bg] of TEXT_PAIRS) {
      const ratio = contrastRatio(tokens[theme][fg], tokens[theme][bg])
      it(`NFR_A11Y2_${theme}: text ${fg} on ${bg} >= 4.5:1 (${ratio.toFixed(2)}:1)`, () => {
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_TEXT)
      })
    }
    for (const [fg, bg] of NON_TEXT_PAIRS) {
      const ratio = contrastRatio(tokens[theme][fg], tokens[theme][bg])
      it(`NFR_A11Y2_${theme}: UI ${fg} on ${bg} >= 3:1 (${ratio.toFixed(2)}:1)`, () => {
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_NON_TEXT)
      })
    }
  }
})

describe('FR-53: index.css declares exactly the tokens in tokens.ts', () => {
  const block = (selector: RegExp): string => {
    const match = selector.exec(css)
    if (!match) {
      throw new Error(`Block ${String(selector)} not found in index.css`)
    }
    const start = match.index + match[0].length
    return css.slice(start, css.indexOf('}', start))
  }
  const blocks: Record<ThemeName, string> = {
    dark: block(/:root,\s*\.dark\s*\{/),
    light: block(/\n\.light\s*\{/),
  }

  for (const theme of THEMES) {
    it(`FR53_tokens_${theme}: the ${theme} block declares every token with the exact value`, () => {
      for (const name of TOKEN_NAMES) {
        expect(blocks[theme], `--${name} (${theme})`).toContain(`--${name}: ${tokens[theme][name]};`)
      }
    })
  }

  it('FR53_tokens: dark is the default (:root), the radius is 0.75rem, and every token maps into @theme', () => {
    expect(blocks.dark).toContain('color-scheme: dark;')
    expect(blocks.dark).toContain(`--radius: ${RADIUS};`)
    for (const name of TOKEN_NAMES) {
      expect(css).toContain(`--color-${name}: var(--${name});`)
    }
  })

  it('FR53_motion: has the prefers-reduced-motion kill switch', () => {
    expect(css).toMatch(/@media \(prefers-reduced-motion: reduce\)\s*\{[^}]*animation-duration: 0\.01ms !important/)
  })
})
