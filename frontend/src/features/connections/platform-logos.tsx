import type { ReactNode } from 'react'

import { cn } from '../../lib/utils'
import type { PlatformProvider } from './platform-connections-api'

/**
 * Marks for the deployment platforms, databases and issue trackers (FR-142, FR-165, SPEC §7: inline SVG, no external images;
 * the CSP stays `img-src 'self' data:`). Decorative: the provider's name is always written next to the mark. Dark marks
 * use `currentColor`, so they keep their contrast in both themes.
 */
const marks: Readonly<Record<PlatformProvider | 'github' | 'gitlab', ReactNode>> = {
  jira: (
    <path
      fill="#2684FF"
      d="M11.571 11.513H0a5.218 5.218 0 0 0 5.232 5.215h2.13v2.057A5.215 5.215 0 0 0 12.575 24V12.518a1.005 1.005 0 0 0-1.005-1.005zm5.723-5.756H5.736a5.215 5.215 0 0 0 5.215 5.214h2.129v2.058a5.218 5.218 0 0 0 5.215 5.214V6.758a1.001 1.001 0 0 0-1.001-1.001zM23.013 0H11.455a5.215 5.215 0 0 0 5.215 5.215h2.129v2.057A5.215 5.215 0 0 0 24 12.483V1.005A1.001 1.001 0 0 0 23.013 0Z"
    />
  ),
  linear: (
    <path
      fill="#5E6AD2"
      d="M2.886 4.18A11.982 11.982 0 0 1 11.99 0C18.624 0 24 5.376 24 12.009c0 3.64-1.62 6.903-4.18 9.105L2.887 4.18ZM1.817 5.626l16.556 16.556c-.524.33-1.075.62-1.65.866L.951 7.277c.247-.575.537-1.126.866-1.65ZM.322 9.163l14.515 14.515c-.71.172-1.443.282-2.195.322L0 11.358a12 12 0 0 1 .322-2.195Zm-.17 4.862 9.823 9.824a12.02 12.02 0 0 1-9.824-9.824Z"
    />
  ),
  github: (
    <path
      fill="currentColor"
      d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"
    />
  ),
  gitlab: (
    <path
      fill="#FC6D26"
      d="m23.6 9.593-.033-.086L20.3.98a.851.851 0 0 0-.336-.405.875.875 0 0 0-1 .054.875.875 0 0 0-.29.44L16.47 7.818H7.537L5.333 1.07a.857.857 0 0 0-.29-.44.875.875 0 0 0-1-.055.859.859 0 0 0-.336.405L.433 9.502l-.032.086a6.066 6.066 0 0 0 2.012 7.01l.01.009.03.021 4.977 3.727 2.462 1.863 1.5 1.132a1.009 1.009 0 0 0 1.22 0l1.499-1.132 2.461-1.863 5.006-3.75.013-.01a6.068 6.068 0 0 0 2.009-7.002Z"
    />
  ),
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

export function PlatformLogo({ provider, className }: { provider: PlatformProvider | 'github' | 'gitlab'; className?: string }) {
  return (
    <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" className={cn('size-4 shrink-0', className)}>
      {marks[provider]}
    </svg>
  )
}
