import { useId } from 'react'

import { cn } from '../lib/utils'
import { linePath, MARK, WORDMARK } from './brand-geometry'

/**
 * The Tevrin mark (FR-196, D-V6-6), inline so it follows the theme: six hexagon segments for the six lifecycle phases,
 * the gaps between them for the approval gates, and a `T` monogram. The segments take the brand gradient
 * (`--brand-from` → `--brand-to`, light #4F46E5 → #06B6D4, dark #818CF8 → #22D3EE); the `T` is `currentColor`, so it
 * takes the text colour of where it sits (≥ 3:1 there, asserted in tokens.test.ts). `mono` draws everything in
 * `currentColor` (no gradient). Decorative unless `title` is given.
 */
export function BrandMark({ className, title, mono = false }: { className?: string; title?: string; mono?: boolean }) {
  // One gradient id per instance: the mark can appear more than once (sidebar, drawer, header).
  const gradientId = `tevrin-mark-${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`
  const ring = mono ? 'currentColor' : `url(#${gradientId})`
  return (
    <svg
      viewBox="0 0 64 64"
      data-slot="brand-mark"
      className={cn('size-8 shrink-0', className)}
      role={title ? 'img' : undefined}
      aria-hidden={title ? undefined : true}
      aria-label={title}
      focusable="false"
    >
      {!mono && (
        <defs>
          <linearGradient id={gradientId} x1="8" y1="6" x2="56" y2="58" gradientUnits="userSpaceOnUse">
            <stop offset="0" stopColor="var(--brand-from)" />
            <stop offset="1" stopColor="var(--brand-to)" />
          </linearGradient>
        </defs>
      )}
      <g fill="none" stroke={ring} strokeWidth={MARK.segmentWidth} strokeLinecap="round" data-part="segments">
        {MARK.segments.map((s) => (
          <path key={linePath(s)} d={linePath(s)} />
        ))}
      </g>
      <g fill="none" stroke="currentColor" strokeWidth={MARK.monogramWidth} strokeLinecap="round" data-part="monogram">
        {MARK.monogram.map((s) => (
          <path key={linePath(s)} d={linePath(s)} />
        ))}
      </g>
    </svg>
  )
}

/** The wordmark "tevrin" (stroked letterforms in `currentColor`). Decorative: BrandLockup names the product in text. */
function Wordmark({ className }: { className?: string }) {
  const { cx, cy, r } = WORDMARK.dot
  return (
    <svg viewBox={WORDMARK.viewBox} className={cn('h-8 w-auto shrink-0', className)} aria-hidden focusable="false" data-slot="brand-wordmark">
      <g fill="none" stroke="currentColor" strokeWidth={WORDMARK.strokeWidth} strokeLinecap="round" strokeLinejoin="round">
        {WORDMARK.paths.map((d) => (
          <path key={d} d={d} />
        ))}
      </g>
      <circle cx={cx} cy={cy} r={r} fill="currentColor" />
    </svg>
  )
}

/**
 * The lockup (FR-196): the mark plus the wordmark, no subtitle. It reads as "Tevrin" to assistive tech.
 * `wordmarkClassName` can hide the wordmark visually (e.g. `sr-only` when the sidebar is collapsed) while the name is
 * still announced. The ink is `text-foreground` unless `className` sets another text colour.
 */
export function BrandLockup({
  className,
  wordmarkClassName,
  mono = false,
}: {
  className?: string
  wordmarkClassName?: string
  mono?: boolean
}) {
  return (
    <span data-slot="brand-lockup" className={cn('flex items-center gap-2 text-foreground', className)}>
      <BrandMark mono={mono} />
      <span className={cn('flex items-center', wordmarkClassName)}>
        <Wordmark className="h-8" />
        <span className="sr-only">Tevrin</span>
      </span>
    </span>
  )
}
