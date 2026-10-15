import { useId } from 'react'

import { cn } from '../lib/utils'

/**
 * The AIDE mark (docs/v2/brand/aide-mark.svg), inline so the gradient follows the theme: stops bound to --chart-1 and
 * --chart-2 (SPEC §7.1). Six hexagon segments = the six lifecycle stages; the gaps are the approval gates.
 * Decorative unless `title` is given.
 */
export function BrandMark({ className, title }: { className?: string; title?: string }) {
  // One gradient id per instance: the mark can appear more than once (sidebar, drawer).
  const gradientId = `aide-mark-${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`
  const stroke = `url(#${gradientId})`
  return (
    <svg
      viewBox="0 0 32 32"
      className={cn('size-8 shrink-0', className)}
      role={title ? 'img' : undefined}
      aria-hidden={title ? undefined : true}
      aria-label={title}
      focusable="false"
    >
      <defs>
        <linearGradient id={gradientId} x1="4" y1="4" x2="28" y2="28" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="var(--chart-1)" />
          <stop offset="1" stopColor="var(--chart-2)" />
        </linearGradient>
      </defs>
      <rect x="0.5" y="0.5" width="31" height="31" rx="9" fill={stroke} fillOpacity="0.16" />
      <g fill="none" stroke={stroke} strokeWidth="2.2" strokeLinecap="round">
        <path d="M17.43 5.83 L24.10 9.68" />
        <path d="M25.53 12.15 L25.53 19.85" />
        <path d="M24.10 22.33 L17.43 26.18" />
        <path d="M14.57 26.18 L7.90 22.33" />
        <path d="M6.47 19.85 L6.47 12.15" />
        <path d="M7.90 9.68 L14.57 5.83" />
      </g>
      <g fill="none" stroke={stroke} strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round">
        <path d="M11.5 21 L16 10.5 L20.5 21" />
        <path d="M13.3 17.3 L18.7 17.3" />
      </g>
      <circle cx="16" cy="5" r="1.9" fill="var(--chart-1)" />
    </svg>
  )
}

/**
 * The lockup (docs/v2/brand/aide-logo.svg): mark plus the two-line wordmark. `wordmarkClassName` can hide the text
 * visually (e.g. `sr-only` when the sidebar is collapsed) while the name is still announced.
 */
export function BrandLockup({ className, wordmarkClassName }: { className?: string; wordmarkClassName?: string }) {
  return (
    <span className={cn('flex items-center gap-2.5', className)}>
      <BrandMark />
      <span className={cn('flex flex-col leading-tight', wordmarkClassName)}>
        <span className="font-heading text-[17px] text-foreground">AIDE</span>
        <span className="text-[10px] font-medium text-muted-foreground">AI-Driven Engineering</span>
      </span>
    </span>
  )
}
