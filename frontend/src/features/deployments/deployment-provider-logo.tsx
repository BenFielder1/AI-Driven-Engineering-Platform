import { cn } from '../../lib/utils'
import { PlatformLogo } from '../connections/platform-logos'
import { isPlatformProvider } from '../connections/platform-connections-api'
import type { Schemas } from '../../api/client'

/**
 * A deployment provider's mark (SPEC §7: inline SVG, no external images): the platform marks from Connections, plus
 * GitHub Pages and the Fake provider. Decorative: the provider's name is always written next to it.
 */
export function DeploymentProviderLogo({ provider, className }: { provider: Schemas['DeploymentProvider']; className?: string }) {
  if (isPlatformProvider(provider)) {
    return <PlatformLogo provider={provider} className={className} />
  }
  return (
    <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" className={cn('size-4 shrink-0', className)}>
      {provider === 'github_pages' ? (
        <>
          <rect width="20" height="16" x="2" y="4" rx="2.5" fill="currentColor" />
          <path fill="none" stroke="var(--color-background, #fff)" strokeWidth="1.75" d="M9 9.5 6.5 12 9 14.5M15 9.5l2.5 2.5-2.5 2.5" />
        </>
      ) : (
        <path fill="none" stroke="currentColor" strokeWidth="2" strokeDasharray="3 2" d="M4 4h16v16H4z" />
      )}
    </svg>
  )
}
