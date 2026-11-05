import { describe, expect, it } from 'vitest'

import css from '../../index.css?raw'
import { compositeOver, contrastOnSrgb, contrastRatio, oklchToSrgb, parseOklch, relativeLuminance, WCAG_AA_NON_TEXT, WCAG_AA_TEXT } from './contrast'
import { BRAND_COLORS, BRAND_INK_PAIRS, MOTION, NON_TEXT_PAIRS, RADIUS, TEXT_PAIRS, TINTED_TEXT_PAIRS, TOKEN_NAMES, tokens, TYPE_SCALE, type ThemeName } from './tokens'

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

  it('composites a tint over a surface in sRGB (the v2 axe run: #007f84 at 15% over the light sidebar)', () => {
    const surface = compositeOver('oklch(0.50 0.19 195)', 0.15, 'oklch(0.98 0.003 260)')
    // axe measured 3.74:1 for the old light primary on this surface (#d3e7e9).
    expect(contrastOnSrgb('oklch(0.50 0.19 195)', surface)).toBeCloseTo(3.7, 1)
    expect(contrastOnSrgb('oklch(1 0 0)', compositeOver('oklch(0 0 0)', 0, 'oklch(1 0 0)'))).toBeCloseTo(1, 3)
  })

  it('is symmetric', () => {
    expect(contrastRatio('oklch(0.97 0.005 260)', 'oklch(0.06 0.01 260)')).toBe(
      contrastRatio('oklch(0.06 0.01 260)', 'oklch(0.97 0.005 260)'),
    )
  })
})

describe('FR-202 / NFR-A11Y2: design tokens meet WCAG 2.2 AA in both themes', () => {
  for (const theme of THEMES) {
    for (const [fg, bg] of TEXT_PAIRS) {
      const ratio = contrastRatio(tokens[theme][fg], tokens[theme][bg])
      it(`FR202_${theme}: text ${fg} on ${bg} >= 4.5:1 (${ratio.toFixed(2)}:1)`, () => {
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_TEXT)
      })
    }
    for (const [fg, tint, alpha, surface] of TINTED_TEXT_PAIRS) {
      const t = tokens[theme]
      const ratio = contrastOnSrgb(t[fg], compositeOver(t[tint], alpha, t[surface]))
      it(`FR202_${theme}: text ${fg} on ${tint}/${String(alpha * 100)} over ${surface} >= 4.5:1 (${ratio.toFixed(2)}:1)`, () => {
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_TEXT)
      })
    }
    for (const [fg, bg] of NON_TEXT_PAIRS) {
      const ratio = contrastRatio(tokens[theme][fg], tokens[theme][bg])
      it(`FR202_${theme}: UI ${fg} on ${bg} >= 3:1 (${ratio.toFixed(2)}:1)`, () => {
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_NON_TEXT)
      })
    }
  }
})

describe('FR-196: the mark and wordmark ink is visible where the brand renders', () => {
  for (const theme of THEMES) {
    for (const [ink, surface, where] of BRAND_INK_PAIRS) {
      const ratio = contrastRatio(tokens[theme][ink], tokens[theme][surface])
      it(`FR196_${theme}: ${where} (${ink} on ${surface}) >= 3:1 (${ratio.toFixed(2)}:1)`, () => {
        expect(ratio).toBeGreaterThanOrEqual(WCAG_AA_NON_TEXT)
      })
    }
  }
})

describe('FR-202 (amends FR-53): index.css declares exactly the tokens in tokens.ts', () => {
  const block = (selector: RegExp): string => {
    const match = selector.exec(css)
    if (!match) {
      throw new Error(`Block ${String(selector)} not found in index.css`)
    }
    const start = match.index + match[0].length
    return css.slice(start, css.indexOf('}', start))
  }
  const blocks: Record<ThemeName, string> = {
    light: block(/:root,\s*\.light\s*\{/),
    dark: block(/\n\.dark\s*\{/),
  }

  for (const theme of THEMES) {
    it(`FR202_tokens_${theme}: the ${theme} block declares every token with the exact value`, () => {
      for (const name of TOKEN_NAMES) {
        expect(blocks[theme], `--${name} (${theme})`).toContain(`--${name}: ${tokens[theme][name]};`)
      }
    })
  }

  it('FR202_tokens: light is the pre-script default (:root), the radius is 0.75rem, and every token maps into @theme', () => {
    expect(blocks.light).toContain('color-scheme: light;')
    expect(blocks.dark).toContain('color-scheme: dark;')
    expect(blocks.light).toContain(`--radius: ${RADIUS};`)
    for (const name of TOKEN_NAMES) {
      expect(css).toContain(`--color-${name}: var(--${name});`)
    }
  })

  it('FR202_gradient: the brand gradient stops are the FR-196 colours (light #4F46E5 -> #06B6D4, dark #818CF8 -> #22D3EE)', () => {
    expect(BRAND_COLORS.light).toMatchObject({ from: '#4F46E5', to: '#06B6D4', ink: '#0F172A' })
    expect(BRAND_COLORS.dark).toMatchObject({ from: '#818CF8', to: '#22D3EE', ink: '#F8FAFC' })
    const rgb = (hex: string) => [1, 3, 5].map((i) => Number.parseInt(hex.slice(i, i + 2), 16) / 255)
    for (const theme of THEMES) {
      const pairs: [string, string][] = [
        [tokens[theme]['brand-from'], BRAND_COLORS[theme].from],
        [tokens[theme]['brand-to'], BRAND_COLORS[theme].to],
        [tokens[theme].foreground, BRAND_COLORS[theme].ink],
      ]
      for (const [oklch, hex] of pairs) {
        const actual = oklchToSrgb(parseOklch(oklch))
        rgb(hex).forEach((channel, i) => {
          // Within 3/255 per channel: the oklch values are rounded to three decimals.
          expect(Math.abs((actual[i] ?? 0) - channel), `${theme} ${oklch} vs ${hex}`).toBeLessThanOrEqual(3 / 255)
        })
      }
    }
  })

  it('FR203_type_scale: the type scale is 12/14/16/20/24/30/36/48/60 px, declared once in index.css', () => {
    const px = new Set(Object.values(TYPE_SCALE).map((rem) => Number.parseFloat(rem) * 16))
    expect([...px].sort((a, b) => a - b)).toEqual([12, 14, 16, 20, 24, 30, 36, 48, 60])
    expect(css).toContain('--text-*: initial;')
    for (const [step, size] of Object.entries(TYPE_SCALE)) {
      expect(css).toContain(`--text-${step}: ${size};`)
    }
  })

  it('FR203_fonts: Geist Sans and Geist Mono only; DM Sans and Outfit are gone', () => {
    expect(css).toContain("--font-sans: 'Geist Sans'")
    expect(css).toContain("--font-mono: 'Geist Mono'")
    expect(css).not.toMatch(/DM Sans|Outfit/)
    expect(css).not.toMatch(/@import\s+url\(\s*['"]?https?:/)
  })

  it('FR205_motion: durations are 150-250 ms, and the prefers-reduced-motion kill switch is there', () => {
    for (const [name, value] of Object.entries(MOTION)) {
      const ms = Number.parseInt(value, 10)
      expect(ms).toBeGreaterThanOrEqual(150)
      expect(ms).toBeLessThanOrEqual(250)
      expect(css).toContain(`--duration-${name}: ${value};`)
    }
    expect(css).toMatch(/@media \(prefers-reduced-motion: reduce\)\s*\{[^}]*animation-duration: 0\.01ms !important/)
  })

  it('FR202_no_orbs: the v2/v3 primary glow utility is gone', () => {
    expect(css).not.toContain('.glow-primary')
  })
})
