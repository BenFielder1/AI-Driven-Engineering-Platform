import { render, screen } from '@testing-library/react'
import { readFileSync, statSync } from 'node:fs'
import path from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { BRAND_COLORS } from '../lib/ui/tokens'
import { axeViolations, setTheme, THEMES } from '../test/axe'
import { BrandLockup, BrandMark } from './brand'
import { FAVICON_MARK, FAVICON_TILE, faviconSvg, lockupSvg, MARK, markSvg, narrowestGap } from './brand-geometry'

// Vitest runs from frontend/.
const frontend = process.cwd()
const docsBrand = path.resolve(frontend, '../docs/v6/brand')
const publicDir = path.join(frontend, 'public')

const read = (file: string) => readFileSync(file, 'utf8')

/** Width and height from a PNG's IHDR chunk. */
function pngSize(file: string): { width: number; height: number } {
  const bytes = readFileSync(file)
  expect(bytes.subarray(1, 4).toString('ascii')).toBe('PNG')
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) }
}

afterEach(() => {
  document.documentElement.className = ''
})

describe('FR-196: BrandMark and BrandLockup', () => {
  for (const theme of THEMES) {
    it(`FR196_lockup_${theme}: the lockup reads "Tevrin" (no subtitle) and axe finds no violations`, async () => {
      setTheme(theme)
      const { container } = render(
        <a href="#brand">
          <BrandLockup />
        </a>,
      )
      const link = screen.getByRole('link')
      expect(link).toHaveAccessibleName('Tevrin')
      expect(link).toHaveTextContent(/^Tevrin$/)
      expect(container.querySelectorAll('svg[aria-hidden="true"]')).toHaveLength(2)
      expect(await axeViolations(container)).toEqual([])
    })

    it(`FR196_mark_${theme}: the mark is decorative by default, an image with a name when titled, and passes axe`, async () => {
      setTheme(theme)
      const { container } = render(
        <div>
          <BrandMark />
          <BrandMark title="Tevrin" />
        </div>,
      )
      expect(screen.getAllByRole('img', { name: 'Tevrin' })).toHaveLength(1)
      expect(await axeViolations(container)).toEqual([])
    })
  }

  it('FR196_mark_colours: six gradient segments bound to --brand-from/--brand-to; the T in currentColor', () => {
    const { container } = render(<BrandMark />)
    const segments = container.querySelector('[data-part="segments"]')
    const monogram = container.querySelector('[data-part="monogram"]')
    expect(segments?.querySelectorAll('path')).toHaveLength(6)
    expect(segments?.getAttribute('stroke')).toMatch(/^url\(#tevrin-mark-/)
    expect(monogram?.getAttribute('stroke')).toBe('currentColor')
    const stops = [...container.querySelectorAll('stop')].map((s) => s.getAttribute('stop-color'))
    expect(stops).toEqual(['var(--brand-from)', 'var(--brand-to)'])
  })

  it('FR196_mono: the mono mark is one colour (currentColor), with no gradient', () => {
    const { container } = render(<BrandMark mono />)
    expect(container.querySelector('linearGradient')).toBeNull()
    expect(container.querySelector('[data-part="segments"]')?.getAttribute('stroke')).toBe('currentColor')
  })

  it('FR196_ids: two marks on one page use different gradient ids', () => {
    const { container } = render(
      <>
        <BrandMark />
        <BrandMark />
      </>,
    )
    const ids = [...container.querySelectorAll('linearGradient')].map((g) => g.id)
    expect(new Set(ids).size).toBe(2)
  })

  it('FR196_wordmark_hidden: a visually hidden wordmark (collapsed sidebar) still names the product', () => {
    render(
      <a href="#brand">
        <BrandLockup wordmarkClassName="sr-only" />
      </a>,
    )
    expect(screen.getByRole('link')).toHaveAccessibleName('Tevrin')
  })
})

describe('FR-196: brand geometry', () => {
  it('FR196_gates: six segments, and the gaps (gates) between them are visible', () => {
    expect(MARK.segments).toHaveLength(6)
    expect(narrowestGap(MARK)).toBeGreaterThan(3)
  })

  it('FR196_favicon_gaps: the favicon mark has heavier strokes and no gap narrower than 2 px at 16 x 16', () => {
    expect(FAVICON_MARK.segmentWidth).toBeGreaterThan(MARK.segmentWidth)
    expect(FAVICON_MARK.monogramWidth).toBeGreaterThan(MARK.monogramWidth)
    // 64 units -> 16 px: 4 units per pixel.
    expect(narrowestGap(FAVICON_MARK) / 4).toBeGreaterThanOrEqual(2)
  })
})

describe('FR-196: static brand files (written by `npm run brand:assets`)', () => {
  const { light, dark } = BRAND_COLORS

  it('FR196_files_fresh: docs/v6/brand and public/brand hold exactly what the generator writes', () => {
    const expected: Record<string, string> = {
      'tevrin-mark-light.svg': markSvg(light),
      'tevrin-mark-dark.svg': markSvg(dark),
      'tevrin-mark-mono-light.svg': markSvg(light, { mono: true }),
      'tevrin-mark-mono-dark.svg': markSvg(dark, { mono: true }),
      'tevrin-lockup-light.svg': lockupSvg(light),
      'tevrin-lockup-dark.svg': lockupSvg(dark),
      'tevrin-lockup-mono-light.svg': lockupSvg(light, { mono: true }),
      'tevrin-lockup-mono-dark.svg': lockupSvg(dark, { mono: true }),
    }
    for (const [name, svg] of Object.entries(expected)) {
      expect(read(path.join(docsBrand, name)), `docs/v6/brand/${name}`).toBe(svg)
      expect(read(path.join(publicDir, 'brand', name)), `public/brand/${name}`).toBe(svg)
    }
    expect(read(path.join(docsBrand, 'tevrin-favicon.svg'))).toBe(faviconSvg(dark))
    expect(read(path.join(publicDir, 'favicon.svg'))).toBe(faviconSvg(dark))
  })

  it('FR196_files_ink: light files use #0F172A ink and the light gradient; dark files #F8FAFC and the dark gradient', () => {
    const lockupLight = read(path.join(docsBrand, 'tevrin-lockup-light.svg'))
    const lockupDark = read(path.join(docsBrand, 'tevrin-lockup-dark.svg'))
    expect(lockupLight).toContain('#0F172A')
    expect(lockupLight).toContain('stop-color="#4F46E5"')
    expect(lockupLight).toContain('stop-color="#06B6D4"')
    expect(lockupLight).not.toContain('#F8FAFC')
    expect(lockupDark).toContain('#F8FAFC')
    expect(lockupDark).toContain('stop-color="#818CF8"')
    expect(lockupDark).toContain('stop-color="#22D3EE"')
    expect(lockupDark).not.toContain('#0F172A')
    expect(read(path.join(docsBrand, 'tevrin-mark-mono-light.svg'))).not.toContain('linearGradient')
    expect(read(path.join(docsBrand, 'tevrin-lockup-mono-dark.svg'))).not.toContain('linearGradient')
  })

  it('FR196_favicon_tile: favicon.svg is the dark-theme mark on a black tile, gaps still 2 px at 16 x 16', () => {
    const svg = read(path.join(publicDir, 'favicon.svg'))
    expect(svg).toContain('<rect width="64" height="64" rx="12" fill="#000000"/>')
    expect(svg).toContain('stroke="#F8FAFC"')
    expect(svg).toContain('stop-color="#818CF8"')
    expect(svg).toContain('stop-color="#22D3EE"')
    expect(svg).not.toContain('#0F172A')
    expect(svg).not.toContain('prefers-color-scheme')
    expect((narrowestGap(FAVICON_MARK) * FAVICON_TILE.scale) / 4).toBeGreaterThanOrEqual(2)
  })

  it('FR196_png: icon-32.png is 32 px, apple-touch-icon.png 180 px, og.png 1200 x 630 and under 100 KB', () => {
    expect(pngSize(path.join(publicDir, 'icon-32.png'))).toEqual({ width: 32, height: 32 })
    expect(pngSize(path.join(publicDir, 'apple-touch-icon.png'))).toEqual({ width: 180, height: 180 })
    expect(pngSize(path.join(publicDir, 'og.png'))).toEqual({ width: 1200, height: 630 })
    expect(statSync(path.join(publicDir, 'og.png')).size).toBeLessThan(100 * 1024)
  })
})
