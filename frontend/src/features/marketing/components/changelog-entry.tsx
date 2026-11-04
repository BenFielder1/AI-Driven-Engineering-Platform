import { panelClass } from '../../../components/shared/panel-class'
import { StatusBadge } from '../../../components/shared/status-badge'
import { cn } from '../../../lib/utils'
import { formatChangelogDate, type ChangelogEntry } from '../content/changelog'
import { SiteLink } from './site-link'

/**
 * One version (FR-219): version, date (or "In preview"), title with "(as AIDE)" for v1–v5, highlights and a link to the
 * version's page on the docs site. `compact` (Home's Latest changes) shows the first three highlights.
 */
export function ChangelogEntryCard({
  entry,
  headingLevel = 'h2',
  compact = false,
}: {
  entry: ChangelogEntry
  headingLevel?: 'h2' | 'h3'
  compact?: boolean
}) {
  const Heading = headingLevel
  const highlights = compact ? entry.highlights.slice(0, 3) : entry.highlights
  return (
    <article className={cn(panelClass, 'p-6')} data-testid="changelog-entry" aria-labelledby={`changelog-${entry.version}`}>
      <div className="flex flex-wrap items-center gap-3">
        <span className="font-mono text-sm font-semibold text-primary">{entry.version}</span>
        {entry.date === null ? (
          <StatusBadge tone="info">{formatChangelogDate(null)}</StatusBadge>
        ) : (
          <time dateTime={entry.date} className="text-sm text-muted-foreground">
            {formatChangelogDate(entry.date)}
          </time>
        )}
      </div>
      <Heading id={`changelog-${entry.version}`} className="mt-2 text-xl font-semibold tracking-tight">
        {entry.title}
        {entry.asAide && <span className="font-normal text-muted-foreground"> (as AIDE)</span>}
      </Heading>
      <ul className="mt-3 list-disc space-y-1.5 pl-5 text-sm text-muted-foreground marker:text-border">
        {highlights.map((h) => (
          <li key={h}>{h}</li>
        ))}
      </ul>
      <p className="mt-4 text-sm">
        <SiteLink
          link={{ label: `${entry.version} spec and notes`, href: entry.specHref, external: true }}
          className="inline-flex min-h-6 items-center font-medium text-primary underline-offset-4 hover:underline"
        />
      </p>
    </article>
  )
}
