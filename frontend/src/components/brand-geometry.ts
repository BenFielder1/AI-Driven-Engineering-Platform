/**
 * FR-196: the Tevrin mark, wordmark and static brand files, as pure data and string builders. One source for the inline
 * React component (components/brand.tsx), the files in docs/0.6.0/brand and public/ (scripts/brand-assets.ts writes them)
 * and the tests. No imports: scripts/brand-assets.ts runs it with Node's type stripping.
 *
 * The mark is the docs/0.6.0 concept (D-V6-6): six stroked hexagon segments for the six lifecycle phases, the gaps between
 * them for the approval gates, and a `T` monogram. Coordinates are in a 64 × 64 box.
 */

export interface Line {
  x1: number
  y1: number
  x2: number
  y2: number
}

export interface MarkGeometry {
  /** The six phase segments, clockwise from the top vertex. */
  segments: readonly Line[]
  segmentWidth: number
  /** The `T`: the bar, then the stem. */
  monogram: readonly Line[]
  monogramWidth: number
}

const round = (n: number) => Math.round(n * 1000) / 1000

/**
 * A pointy-top hexagon around (32, 32) with circumradius `r`; each edge keeps the part from `inset` to `r - inset`
 * along it, so the gap at each vertex (a gate) is `inset * √3` between segment ends, minus the stroke width.
 */
function hexagonSegments(r: number, inset: number): Line[] {
  const vertex = (k: number) => {
    const a = ((-90 + 60 * k) * Math.PI) / 180
    return { x: 32 + r * Math.cos(a), y: 32 + r * Math.sin(a) }
  }
  return Array.from({ length: 6 }, (_, k) => {
    const from = vertex(k)
    const to = vertex(k + 1)
    const t1 = inset / r
    const t2 = 1 - inset / r
    return {
      x1: round(from.x + (to.x - from.x) * t1),
      y1: round(from.y + (to.y - from.y) * t1),
      x2: round(from.x + (to.x - from.x) * t2),
      y2: round(from.y + (to.y - from.y) * t2),
    }
  })
}

/** The mark for UI sizes (24 px and up): the concept's geometry with slightly wider gates. */
export const MARK: MarkGeometry = {
  segments: hexagonSegments(27, 5),
  segmentWidth: 5,
  monogram: [
    { x1: 21, y1: 24, x2: 43, y2: 24 },
    { x1: 32, y1: 24, x2: 32, y2: 42 },
  ],
  monogramWidth: 6.5,
}

/**
 * The favicon mark (16–32 px): heavier strokes, and every gap (between segments, and between the `T` and the ring) is
 * at least 8 units, i.e. 2 px at 16 × 16 (asserted in brand.test.tsx).
 */
export const FAVICON_MARK: MarkGeometry = {
  segments: hexagonSegments(28.5, 9.5),
  segmentWidth: 7,
  monogram: [
    { x1: 24, y1: 26.5, x2: 40, y2: 26.5 },
    { x1: 32, y1: 26.5, x2: 32, y2: 41.5 },
  ],
  monogramWidth: 8,
}

/**
 * The wordmark "tevrin" as stroked letterforms (no font dependency), from the docs/0.6.0 concept. Coordinates are in the
 * lockup's 64-high box: the wordmark starts at x ≈ 78 and the letters sit on the mark's vertical centre.
 */
export const WORDMARK = {
  paths: [
    // t
    'M88 10 V42 Q88 52 98 52',
    'M81 24 H97',
    // e
    'M108 37 H136 A14 14 0 1 0 131.9 46.9',
    // v
    'M146 23 L158.5 51 L171 23',
    // r
    'M183 23 V52',
    'M183 35 Q183 23 196 23',
    // i
    'M209 23 V52',
    // n
    'M223 23 V52',
    'M223 36 A12.5 12.5 0 0 1 248 36 V52',
  ],
  /** The dot of the i. */
  dot: { cx: 209, cy: 11, r: 4 },
  strokeWidth: 6.5,
  /** The wordmark's own box, for an inline SVG next to the mark (same height, so the two line up). */
  viewBox: '76 0 176 64',
} as const

/** The lockup (mark + wordmark) box. */
export const LOCKUP_VIEWBOX = '0 0 258 64'

export const linePath = ({ x1, y1, x2, y2 }: Line) => `M${String(x1)} ${String(y1)} L${String(x2)} ${String(y2)}`

/** Shortest distance between two line segments (0 if they cross). */
export function segmentDistance(a: Line, b: Line): number {
  const pointToSegment = (px: number, py: number, s: Line) => {
    const dx = s.x2 - s.x1
    const dy = s.y2 - s.y1
    const len2 = dx * dx + dy * dy
    const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((px - s.x1) * dx + (py - s.y1) * dy) / len2))
    return Math.hypot(px - (s.x1 + t * dx), py - (s.y1 + t * dy))
  }
  const cross = (o: { x: number; y: number }, p: { x: number; y: number }, q: { x: number; y: number }) =>
    (p.x - o.x) * (q.y - o.y) - (p.y - o.y) * (q.x - o.x)
  const a1 = { x: a.x1, y: a.y1 }
  const a2 = { x: a.x2, y: a.y2 }
  const b1 = { x: b.x1, y: b.y1 }
  const b2 = { x: b.x2, y: b.y2 }
  if (cross(a1, a2, b1) * cross(a1, a2, b2) < 0 && cross(b1, b2, a1) * cross(b1, b2, a2) < 0) {
    return 0
  }
  return Math.min(
    pointToSegment(a.x1, a.y1, b),
    pointToSegment(a.x2, a.y2, b),
    pointToSegment(b.x1, b.y1, a),
    pointToSegment(b.x2, b.y2, a),
  )
}

/**
 * The narrowest visible gap in a mark, in its 64-unit box: between any two stroked shapes (round caps), i.e. the
 * centre-line distance minus half of each stroke. The `T`'s bar and stem join, so that pair is skipped.
 */
export function narrowestGap(mark: MarkGeometry): number {
  const shapes = [
    ...mark.segments.map((line) => ({ line, half: mark.segmentWidth / 2, t: false })),
    ...mark.monogram.map((line) => ({ line, half: mark.monogramWidth / 2, t: true })),
  ]
  let min = Number.POSITIVE_INFINITY
  shapes.forEach((a, i) => {
    shapes.slice(i + 1).forEach((b) => {
      if (a.t && b.t) {
        return
      }
      min = Math.min(min, segmentDistance(a.line, b.line) - a.half - b.half)
    })
  })
  return min
}

// ---------------------------------------------------------------------------------------------------------------------
// Static SVG files (FR-196). Hex colours, because files can't read the theme's CSS variables.

export interface BrandPalette {
  from: string
  to: string
  ink: string
}

const svgOpen = (viewBox: string, width: number, height: number, label: string) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}" width="${String(width)}" height="${String(height)}" role="img" aria-label="${label}">`

function gradientDef(id: string, palette: BrandPalette) {
  return (
    `<defs><linearGradient id="${id}" x1="8" y1="6" x2="56" y2="58" gradientUnits="userSpaceOnUse">` +
    `<stop offset="0" stop-color="${palette.from}"/><stop offset="1" stop-color="${palette.to}"/></linearGradient></defs>`
  )
}

/** The mark's shapes. `ring` is the segments' stroke (a gradient url or a colour), `ink` the `T`'s. */
function markShapes(mark: MarkGeometry, ring: string, ink: string) {
  const segments = mark.segments.map((s) => `<path d="${linePath(s)}"/>`).join('')
  const monogram = mark.monogram.map((s) => `<path d="${linePath(s)}"/>`).join('')
  return (
    `<g fill="none" stroke="${ring}" stroke-width="${String(mark.segmentWidth)}" stroke-linecap="round">${segments}</g>` +
    `<g fill="none" stroke="${ink}" stroke-width="${String(mark.monogramWidth)}" stroke-linecap="round">${monogram}</g>`
  )
}

function wordmarkShapes(ink: string) {
  const paths = WORDMARK.paths.map((d) => `<path d="${d}"/>`).join('')
  const { cx, cy, r } = WORDMARK.dot
  return (
    `<g fill="none" stroke="${ink}" stroke-width="${String(WORDMARK.strokeWidth)}" stroke-linecap="round" stroke-linejoin="round">${paths}</g>` +
    `<circle cx="${String(cx)}" cy="${String(cy)}" r="${String(r)}" fill="${ink}"/>`
  )
}

const COMMENT = '<!-- Tevrin. Six segments = the six lifecycle phases; the gaps between them = the approval gates. Generated by frontend/scripts/brand-assets.ts. -->'

/** The mark: gradient segments and an ink `T`, or `mono` (one colour, no gradient). */
export function markSvg(palette: BrandPalette, options: { mono?: boolean; size?: number } = {}) {
  const size = options.size ?? 256
  const ring = options.mono ? palette.ink : 'url(#tevrin-gradient)'
  return (
    `${svgOpen('0 0 64 64', size, size, 'Tevrin')}${COMMENT}` +
    `${options.mono ? '' : gradientDef('tevrin-gradient', palette)}${markShapes(MARK, ring, palette.ink)}</svg>\n`
  )
}

/** The lockup: the mark and the wordmark "tevrin". */
export function lockupSvg(palette: BrandPalette, options: { mono?: boolean } = {}) {
  const ring = options.mono ? palette.ink : 'url(#tevrin-gradient)'
  return (
    `${svgOpen(LOCKUP_VIEWBOX, 774, 192, 'Tevrin')}${COMMENT}` +
    `${options.mono ? '' : gradientDef('tevrin-gradient', palette)}${markShapes(MARK, ring, palette.ink)}${wordmarkShapes(palette.ink)}</svg>\n`
  )
}

/**
 * favicon.svg: the heavier favicon mark, light colours by default and dark ones when the browser UI is dark (SVG
 * favicons honour prefers-color-scheme).
 */
export function faviconSvg(light: BrandPalette, dark: BrandPalette) {
  const style =
    `<style>.ink{stroke:${light.ink}}.from{stop-color:${light.from}}.to{stop-color:${light.to}}` +
    `@media (prefers-color-scheme: dark){.ink{stroke:${dark.ink}}.from{stop-color:${dark.from}}.to{stop-color:${dark.to}}}</style>`
  const segments = FAVICON_MARK.segments.map((s) => `<path d="${linePath(s)}"/>`).join('')
  const monogram = FAVICON_MARK.monogram.map((s) => `<path d="${linePath(s)}"/>`).join('')
  return (
    `${svgOpen('0 0 64 64', 32, 32, 'Tevrin')}${COMMENT}${style}` +
    `<defs><linearGradient id="tevrin-gradient" x1="8" y1="6" x2="56" y2="58" gradientUnits="userSpaceOnUse">` +
    `<stop offset="0" class="from" stop-color="${light.from}"/><stop offset="1" class="to" stop-color="${light.to}"/></linearGradient></defs>` +
    `<g fill="none" stroke="url(#tevrin-gradient)" stroke-width="${String(FAVICON_MARK.segmentWidth)}" stroke-linecap="round">${segments}</g>` +
    `<g class="ink" fill="none" stroke="${light.ink}" stroke-width="${String(FAVICON_MARK.monogramWidth)}" stroke-linecap="round">${monogram}</g></svg>\n`
  )
}

/**
 * An app icon on a solid tile (icon-32.png, apple-touch-icon.png): the favicon mark, scaled into the tile. `radius` 0
 * gives a full-bleed square (iOS rounds it itself).
 */
export function tileIconSvg(palette: BrandPalette & { surface: string }, options: { size: number; radius: number; scale: number }) {
  const offset = 32 * (1 - options.scale)
  return (
    svgOpen('0 0 64 64', options.size, options.size, 'Tevrin') +
    `<rect width="64" height="64" rx="${String(options.radius)}" fill="${palette.surface}"/>` +
    gradientDef('tevrin-gradient', palette) +
    `<g transform="translate(${String(offset)} ${String(offset)}) scale(${String(options.scale)})">${markShapes(FAVICON_MARK, 'url(#tevrin-gradient)', palette.ink)}</g></svg>\n`
  )
}

export const TAGLINE = 'Ship software with AI agents, under human control.'

/**
 * og.png (1200 × 630): the dark lockup and the tagline on the dark surface. A thin gradient rule at the top is the only
 * gradient, and no text sits on it. `fontFamily` is the family the renderer has loaded (Geist).
 */
export function ogSvg(palette: BrandPalette & { surface: string; muted: string }, fontFamily: string) {
  const lockupScale = 2.6
  const lockupWidth = 258 * lockupScale
  const x = (1200 - lockupWidth) / 2
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 630" width="1200" height="630">` +
    `<defs><linearGradient id="rule" x1="0" y1="0" x2="1200" y2="0" gradientUnits="userSpaceOnUse">` +
    `<stop offset="0" stop-color="${palette.from}"/><stop offset="1" stop-color="${palette.to}"/></linearGradient>` +
    `<linearGradient id="tevrin-gradient" x1="8" y1="6" x2="56" y2="58" gradientUnits="userSpaceOnUse">` +
    `<stop offset="0" stop-color="${palette.from}"/><stop offset="1" stop-color="${palette.to}"/></linearGradient></defs>` +
    `<rect width="1200" height="630" fill="${palette.surface}"/>` +
    `<rect width="1200" height="6" fill="url(#rule)"/>` +
    `<g transform="translate(${String(x)} 190) scale(${String(lockupScale)})">${markShapes(MARK, 'url(#tevrin-gradient)', palette.ink)}${wordmarkShapes(palette.ink)}</g>` +
    `<text x="600" y="450" text-anchor="middle" font-family="${fontFamily}" font-size="44" font-weight="500" fill="${palette.muted}">${TAGLINE}</text>` +
    `</svg>`
  )
}
