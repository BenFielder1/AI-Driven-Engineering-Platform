import { cn } from '../../lib/utils'
import { StatusBadge } from './status-badge'

export type VersionKind = 'released' | 'development'

/**
 * A release version (SPEC §7.1, FR-62): "Released 1.1.0", "Not released" (released without a version), or
 * "In development 1.2.0". The text reads as one phrase for screen readers and `toHaveTextContent`.
 */
export function VersionBadge({ version, kind, className }: { version: string | null; kind: VersionKind; className?: string }) {
  if (kind === 'released' && !version) {
    return (
      <StatusBadge tone="neutral" className={className}>
        Not released
      </StatusBadge>
    )
  }
  return (
    <StatusBadge tone={kind === 'released' ? 'success' : 'info'} pulse={kind === 'development'} className={className}>
      {kind === 'released' ? 'Released' : 'In development'} <span className="font-mono tabular-nums">{version}</span>
    </StatusBadge>
  )
}

/**
 * FR-62: the two version badges shown wherever the project status is: "Released X" (or "Not released") and, only when
 * a version is in development, "In development Y".
 */
export function ProjectVersionBadges({
  released,
  development,
  className,
}: {
  released: string | null | undefined
  development: string | null | undefined
  className?: string
}) {
  return (
    <span className={cn('flex flex-wrap gap-1', className)} data-testid="version-badges">
      <VersionBadge version={released ?? null} kind="released" />
      {development && <VersionBadge version={development} kind="development" />}
    </span>
  )
}
