/**
 * FR-196: writes the Tevrin brand files from one source (src/components/brand-geometry.ts and the brand colours in
 * src/lib/ui/tokens.ts). Reproducible: `npm run brand:assets`, then commit the output.
 *
 * - docs/v6/brand/  source SVGs: mark and lockup in light (#0F172A ink) and dark (#F8FAFC ink), each also mono, and the
 *                   favicon mark
 * - public/         served copies: favicon.svg, icon-32.png, apple-touch-icon.png (180 px), og.png (1200 × 630), and
 *                   the mark and lockup SVGs under public/brand/
 *
 * PNGs are rendered with resvg; the Open Graph tagline uses Geist (the TTF from the `geist` devDependency, no system
 * fonts), so the output doesn't depend on the machine. Runs with Node's type stripping (Node 24).
 */
import { mkdirSync, statSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { Resvg } from '@resvg/resvg-js'

import { faviconSvg, lockupSvg, markSvg, ogSvg, tileIconSvg } from '../src/components/brand-geometry.ts'
import { BRAND_COLORS } from '../src/lib/ui/tokens.ts'

const frontend = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const docsBrand = path.resolve(frontend, '../docs/v6/brand')
const publicDir = path.join(frontend, 'public')
const publicBrand = path.join(publicDir, 'brand')
const geistMedium = path.join(frontend, 'node_modules/geist/dist/fonts/geist-sans/Geist-Medium.ttf')

const { light, dark } = BRAND_COLORS
/** OG tagline colour: slate-400 on the dark surface (the dark theme's muted-foreground). */
const OG_MUTED = '#94A3B8'
const OG_MAX_BYTES = 100 * 1024

const svgFiles: Record<string, string> = {
  'tevrin-mark-light.svg': markSvg(light),
  'tevrin-mark-dark.svg': markSvg(dark),
  'tevrin-mark-mono-light.svg': markSvg(light, { mono: true }),
  'tevrin-mark-mono-dark.svg': markSvg(dark, { mono: true }),
  'tevrin-lockup-light.svg': lockupSvg(light),
  'tevrin-lockup-dark.svg': lockupSvg(dark),
  'tevrin-lockup-mono-light.svg': lockupSvg(light, { mono: true }),
  'tevrin-lockup-mono-dark.svg': lockupSvg(dark, { mono: true }),
}

function write(file: string, content: string | Buffer) {
  mkdirSync(path.dirname(file), { recursive: true })
  writeFileSync(file, content)
  console.log(`wrote ${path.relative(path.resolve(frontend, '..'), file)}`)
}

function png(svg: string, width: number, fonts = false): Buffer {
  const resvg = new Resvg(svg, {
    fitTo: { mode: 'width', value: width },
    font: fonts ? { fontFiles: [geistMedium], loadSystemFonts: false, defaultFontFamily: 'Geist' } : { loadSystemFonts: false },
  })
  return resvg.render().asPng()
}

for (const [name, svg] of Object.entries(svgFiles)) {
  write(path.join(docsBrand, name), svg)
  write(path.join(publicBrand, name), svg)
}
const favicon = faviconSvg(light, dark)
write(path.join(docsBrand, 'tevrin-favicon.svg'), favicon)
write(path.join(publicDir, 'favicon.svg'), favicon)

write(path.join(publicDir, 'icon-32.png'), png(tileIconSvg(dark, { size: 32, radius: 14, scale: 0.86 }), 32))
write(path.join(publicDir, 'apple-touch-icon.png'), png(tileIconSvg(dark, { size: 180, radius: 0, scale: 0.72 }), 180))

const og = path.join(publicDir, 'og.png')
write(og, png(ogSvg({ ...dark, muted: OG_MUTED }, 'Geist'), 1200, true))
const ogBytes = statSync(og).size
if (ogBytes > OG_MAX_BYTES) {
  throw new Error(`og.png is ${String(ogBytes)} bytes, over the ${String(OG_MAX_BYTES)}-byte budget (SPEC §8)`)
}
