import type { ReactNode } from 'react'

import { cn } from '../../lib/utils'
import type { PlatformProvider } from './platform-connections-api'

/**
 * Simple, original marks for the deployment platforms and databases (FR-142, SPEC §7: inline SVG, no external images;
 * the CSP stays `img-src 'self' data:`). Decorative: the provider's name is always written next to the mark. Dark marks
 * use `currentColor`, so they keep their contrast in both themes.
 */
const marks: Readonly<Record<PlatformProvider, ReactNode>> = {
  vercel: <path fill="currentColor" d="M12 3 22 20H2z" />,
  netlify: (
    <>
      <path fill="#05BDBA" d="M12 1.5 22.5 12 12 22.5 1.5 12z" />
      <path fill="#fff" d="M8 11h8v2H8z" />
    </>
  ),
  cloudflare: (
    <path
      fill="#F38020"
      d="M18.5 18H6a4 4 0 0 1-.6-7.96A5.5 5.5 0 0 1 16 8.6 4.75 4.75 0 0 1 18.5 18z"
    />
  ),
  render: (
    <>
      <rect width="20" height="20" x="2" y="2" rx="5" fill="currentColor" />
      <path fill="none" stroke="var(--color-background, #fff)" strokeWidth="2.25" d="M9 17V11a3 3 0 0 1 3-3h3" />
    </>
  ),
  railway: (
    <>
      <circle cx="12" cy="12" r="10" fill="currentColor" />
      <path fill="none" stroke="var(--color-background, #fff)" strokeWidth="1.75" d="M5 9.5h14M4 12h16M5 14.5h14" />
    </>
  ),
  supabase: <path fill="#3ECF8E" d="M13.5 2 4 14h7.5L10.5 22 20 10h-7.5z" />,
}

export function PlatformLogo({ provider, className }: { provider: PlatformProvider; className?: string }) {
  return (
    <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" className={cn('size-4 shrink-0', className)}>
      {marks[provider]}
    </svg>
  )
}
