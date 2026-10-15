import type { ReactNode } from 'react'

import { panelClass } from '../../components/shared/panel-class'
import { cn } from '../../lib/utils'
import { formatDateTime, releaseVersionStatusLabels } from '../projects/labels'
import type { ReleaseVersion } from './versions-api'

function Row({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap justify-between gap-x-3 py-1 text-sm">
      <dt className="text-muted-foreground">{term}</dt>
      <dd className="break-all text-right font-medium">{children}</dd>
    </div>
  )
}

/** FR-62: what a release version is (shown for a past version on the overview and the Release page). */
export function VersionSummary({ version, className }: { version: ReleaseVersion; className?: string }) {
  return (
    <section aria-labelledby="version-summary-title" className={cn(panelClass, 'p-4', className)}>
      <h2 id="version-summary-title" className="mb-2 text-base">
        Version <span className="font-mono">{version.version}</span>
      </h2>
      <dl>
        <Row term="Status">{releaseVersionStatusLabels[version.status]}</Row>
        <Row term="Tag">{version.tag ? <code className="font-mono">{version.tag}</code> : '—'}</Row>
        {version.baseTag && (
          <Row term="Based on">
            <code className="font-mono">{version.baseTag}</code>
          </Row>
        )}
        <Row term="Checkpoints">{version.checkpoints.max}</Row>
        {version.branchName && (
          <Row term="Branch">
            <code className="font-mono">{version.branchName}</code>
          </Row>
        )}
        {version.pullRequestUrl && (
          <Row term="Pull request">
            <a href={version.pullRequestUrl} target="_blank" rel="noopener noreferrer" className="text-primary underline">
              View on GitHub
            </a>
          </Row>
        )}
        {version.startedBy && <Row term="Started by">{version.startedBy.name}</Row>}
        <Row term="Started">
          <time dateTime={version.createdAt}>{formatDateTime(version.createdAt)}</time>
        </Row>
        {version.releasedAt && (
          <Row term="Released">
            <time dateTime={version.releasedAt}>{formatDateTime(version.releasedAt)}</time>
          </Row>
        )}
      </dl>
      {version.request && (
        <div className="mt-3 text-sm">
          <p className="font-medium">Request</p>
          <p className="whitespace-pre-wrap break-words text-muted-foreground">{version.request}</p>
        </div>
      )}
    </section>
  )
}
