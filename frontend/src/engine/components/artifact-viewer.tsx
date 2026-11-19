import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useRunArtifactDiff, useRunArtifactVersion } from '../api/hooks'
import type { ArtifactDefinition } from '../api/types'
import { DiffView } from '../diff/diff-view'
import { formatDateTime } from '../format'
import { artifactStatusLabels } from '../labels'
import { MarkdownView } from '../markdown/markdown-view'

/** JSON content, pretty-printed when it parses (shown as stored otherwise). */
function prettyJson(content: string): string {
  try {
    return JSON.stringify(JSON.parse(content), null, 2)
  } catch {
    return content
  }
}

/**
 * FR-301, FR-306: one version of a run's artifact, rendered in its definition format (Markdown, or JSON as a
 * scrolling monospace block). A version whose content no longer matches its hash says so (FR-20).
 */
export function ArtifactViewer({
  runId,
  artifactKey,
  version,
  definition,
  title,
}: {
  runId: string
  artifactKey: string
  version: number
  /** The artifact's definition entry (title and format); absent when the definition doesn't list the key. */
  definition: ArtifactDefinition | undefined
  /** The label to use (the definition title, or the API's title). */
  title: string
}) {
  const shown = useRunArtifactVersion(runId, artifactKey, version)
  if (shown.isPending) {
    return <LoadingState label={`Loading ${title} v${String(version)}…`} />
  }
  if (shown.isError) {
    return <ErrorState error={shown.error} onRetry={() => void shown.refetch()} title={`${title} v${String(version)} couldn't load`} />
  }
  const v = shown.data
  const label = `${title} v${String(v.version)}`
  const format = definition?.format ?? 'markdown'
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
        {v.status && <Badge>{artifactStatusLabels[v.status]}</Badge>}
        <span>
          Created <time dateTime={v.createdAt}>{formatDateTime(v.createdAt)}</time>
        </span>
        <code className="font-mono text-xs" title={v.hash}>
          {v.hash.slice(0, 12)}
        </code>
      </div>
      {!v.intact && (
        <Alert tone="error" title="This version's content doesn't match its hash">
          <p>It may have been changed outside Tevrin. Treat it with care.</p>
        </Alert>
      )}
      {format === 'json' ? (
        <pre
          role="group"
          tabIndex={0}
          aria-label={`${label} (JSON)`}
          className="max-h-[32rem] overflow-auto rounded-lg border border-border/60 bg-muted p-3 font-mono text-xs"
        >
          {prettyJson(v.content)}
        </pre>
      ) : (
        <MarkdownView content={v.content} label={label} />
      )}
    </div>
  )
}

/** FR-301, FR-306: the line diff between two versions of a run's artifact, side by side. */
export function ArtifactDiff({
  runId,
  artifactKey,
  from,
  to,
  title,
}: {
  runId: string
  artifactKey: string
  from: number
  to: number
  title: string
}) {
  const diff = useRunArtifactDiff(runId, artifactKey, { from, to })
  if (diff.isPending) {
    return <LoadingState label={`Loading the changes to ${title}…`} />
  }
  if (diff.isError) {
    return <ErrorState error={diff.error} onRetry={() => void diff.refetch()} title={`The changes to ${title} couldn't load`} />
  }
  return <DiffView diff={diff.data} />
}
