import { Link } from '@tanstack/react-router'
import type { ReactNode } from 'react'

import { formatDateTime } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { checkpointLabel } from '../versions/version-label'
import { authorName } from './artifact-author'
import { ArtifactStatusBadge, IntegrityBadge } from './artifact-badges'
import type { Artifact } from './artifacts-api'

function Meta({ term, children }: { term: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs text-muted-foreground">{term}</dt>
      <dd className="text-sm font-medium break-words">{children}</dd>
    </div>
  )
}

/**
 * FR-16, FR-55, FR-58, FR-59: one version's facts: status, integrity, author (and the agent run that produced it),
 * release version and checkpoint, parent, the version a restore copied, approver and the content hash.
 */
export function ArtifactVersionDetails({ projectId, artifact }: { projectId: string; artifact: Artifact }) {
  const runId = artifact.author.agentRunId
  return (
    <dl className="grid grid-cols-2 gap-3 rounded-lg border border-border/60 bg-background/40 p-3 sm:grid-cols-4">
      <Meta term="Status">
        <ArtifactStatusBadge status={artifact.status} />
      </Meta>
      <Meta term="Integrity">
        <IntegrityBadge integrity={artifact.integrity} />
      </Meta>
      <Meta term="Author">{authorName(artifact.author)}</Meta>
      <Meta term="Created">
        <RelativeTime iso={artifact.createdAt} />
      </Meta>
      <Meta term="Release version">{artifact.releaseVersion || 'Unknown'}</Meta>
      <Meta term="Created at">{checkpointLabel(artifact.releaseVersion || undefined, artifact.checkpointAtCreation)}</Meta>
      <Meta term="Parent version">{artifact.parentVersion === null ? 'None' : `v${String(artifact.parentVersion)}`}</Meta>
      {artifact.restoredFromVersion !== null && (
        <Meta term="Restored from">{`v${String(artifact.restoredFromVersion)}`}</Meta>
      )}
      {runId && (
        <Meta term="Produced by">
          <Link to="/projects/$projectId/runs" params={{ projectId }} search={{ run: runId }} className="text-primary underline">
            Agent run
          </Link>
        </Meta>
      )}
      {artifact.approvedAt && (
        <Meta term="Approved">
          {artifact.approvedBy ? `${artifact.approvedBy.name}, ` : ''}
          <time dateTime={artifact.approvedAt}>{formatDateTime(artifact.approvedAt)}</time>
        </Meta>
      )}
      <Meta term="Content hash (SHA-256)">
        <code className="text-xs" title={artifact.contentHash}>
          {artifact.contentHash.slice(0, 12)}…
        </code>
      </Meta>
      {artifact.rejectionComment && (
        <div className="col-span-full flex flex-col gap-0.5">
          <dt className="text-xs text-muted-foreground">Rejection comment</dt>
          <dd className="text-sm whitespace-pre-wrap">{artifact.rejectionComment}</dd>
        </div>
      )}
    </dl>
  )
}
