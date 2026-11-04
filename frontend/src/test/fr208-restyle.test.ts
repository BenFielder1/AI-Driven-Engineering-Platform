import { describe, expect, it } from 'vitest'

/**
 * FR-206, FR-208 (M38 review follow-ups): app surfaces are opaque (no frosted translucent panels), text uses the type
 * scale (nothing under 12 px), and pulsing dots stop under prefers-reduced-motion (`motion-safe:animate-pulse`).
 */
const sources = import.meta.glob<string>(['../features/**/*.tsx', '../components/**/*.tsx', '!../**/*.test.tsx'], {
  query: '?raw',
  import: 'default',
  eager: true,
})

/** FR-210: the marketing header is sticky and translucent by spec; the app's surfaces stay opaque. */
const TRANSLUCENT_BY_SPEC = new Set(['../features/marketing/components/marketing-header.tsx'])

function offenders(pattern: RegExp, allowed: ReadonlySet<string> = new Set()): string[] {
  return Object.entries(sources)
    .filter(([file, text]) => !allowed.has(file) && pattern.test(text))
    .map(([file]) => file)
}

describe('FR-208: restyled surfaces', () => {
  it('FR208_opaque: no backdrop blur or translucent card, sidebar or popover surfaces', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(100)
    for (const file of TRANSLUCENT_BY_SPEC) {
      expect(sources, file).toHaveProperty([file])
    }
    expect(offenders(/backdrop-blur|\bbg-(card|sidebar|popover)\/\d+|\bbg-background\/\d+/, TRANSLUCENT_BY_SPEC)).toEqual([])
  })

  it('FR208_type_scale: no text sizes under 12 px', () => {
    expect(offenders(/text-\[(\d|1[01])px\]/)).toEqual([])
  })

  it('FR208_motion: pulses only through motion-safe', () => {
    expect(offenders(/(?<!motion-safe:)\banimate-pulse\b/)).toEqual([])
  })
})
