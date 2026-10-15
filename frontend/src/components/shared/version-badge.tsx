import { StatusBadge } from './status-badge'

export type VersionKind = 'released' | 'development'

/**
 * A release version (SPEC §7.1; FR-54 project switcher): "Released 1.1.0" or "In dev 1.2.0". Props only for now; the
 * release-version data arrives with the release versions work (FR-60 onward).
 */
export function VersionBadge({ version, kind, className }: { version: string; kind: VersionKind; className?: string }) {
  return (
    <StatusBadge tone={kind === 'released' ? 'success' : 'info'} pulse={kind === 'development'} className={className}>
      <span className="sr-only">{kind === 'released' ? 'Released version' : 'Version in development'} </span>
      <span aria-hidden>{kind === 'released' ? 'Released' : 'In dev'}</span>
      <span className="font-mono tabular-nums">{version}</span>
    </StatusBadge>
  )
}
