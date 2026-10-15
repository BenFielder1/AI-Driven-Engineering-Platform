/**
 * Pure colour maths for the design-token contrast check (NFR-A11Y2, SPEC §7.1).
 * OKLCH -> OKLab -> linear sRGB -> gamma sRGB (clamped) -> WCAG 2.x relative luminance.
 */

export interface Oklch {
  l: number
  c: number
  h: number
}

const OKLCH_RE = /^oklch\(\s*([\d.]+)(%?)\s+([\d.]+)\s+([\d.]+)(?:deg)?\s*(?:\/\s*[\d.]+%?\s*)?\)$/

/** Parses `oklch(L C H)` (L as 0..1 or a percentage). Throws on anything else. */
export function parseOklch(value: string): Oklch {
  const m = OKLCH_RE.exec(value.trim())
  if (!m) {
    throw new Error(`Not an oklch() colour: ${value}`)
  }
  const [, l = '0', pct, c = '0', h = '0'] = m
  return { l: Number(l) / (pct ? 100 : 1), c: Number(c), h: Number(h) }
}

/** Converts OKLCH to gamma-encoded sRGB channels, each clamped to [0, 1]. */
export function oklchToSrgb({ l, c, h }: Oklch): [number, number, number] {
  const hr = (h * Math.PI) / 180
  const a = c * Math.cos(hr)
  const b = c * Math.sin(hr)

  const l_ = l + 0.3963377774 * a + 0.2158037573 * b
  const m_ = l - 0.1055613458 * a - 0.0638541728 * b
  const s_ = l - 0.0894841775 * a - 1.291485548 * b

  const L = l_ ** 3
  const M = m_ ** 3
  const S = s_ ** 3

  const linear = [
    4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S,
    -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S,
    -0.0041960863 * L - 0.7034186147 * M + 1.707614701 * S,
  ]
  const gamma = (v: number) => {
    const x = Math.min(1, Math.max(0, v))
    return x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055
  }
  return [gamma(linear[0] ?? 0), gamma(linear[1] ?? 0), gamma(linear[2] ?? 0)]
}

/** WCAG 2.x relative luminance of a gamma-encoded sRGB colour. */
export function relativeLuminance([r, g, b]: [number, number, number]): number {
  const lin = (v: number) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)
}

/** WCAG contrast ratio between two `oklch()` strings (1..21). */
export function contrastRatio(fg: string, bg: string): number {
  const l1 = relativeLuminance(oklchToSrgb(parseOklch(fg)))
  const l2 = relativeLuminance(oklchToSrgb(parseOklch(bg)))
  const [hi, lo] = l1 > l2 ? [l1, l2] : [l2, l1]
  return (hi + 0.05) / (lo + 0.05)
}

/** WCAG 2.2 AA: normal text (1.4.3). */
export const WCAG_AA_TEXT = 4.5
/** WCAG 2.2 AA: user-interface component boundaries (1.4.11). */
export const WCAG_AA_NON_TEXT = 3
