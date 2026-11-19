import { Alert } from '../../components/ui/alert'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { useRunArtifacts } from '../api/hooks'
import type { PipelineDefinition, TimelineItem } from '../api/types'
import type { DefinitionLabels } from '../labels'
import { ArtifactDiff, ArtifactViewer } from './artifact-viewer'
import { TimelineEntry } from './run-timeline'

function TextBlock({ label, text, truncated, missing }: { label: string; text: string | null | undefined; truncated: boolean; missing: string }) {
  return (
    <div className="flex min-w-0 flex-col gap-2">
      <h3 className="text-sm font-semibold">{label}</h3>
      {truncated && (
        <Alert tone="warning">
          <p>This {label.toLowerCase()} is cut short: the stored text was capped, or it is too long to show here in full.</p>
        </Alert>
      )}
      {text ? (
        <pre
          role="group"
          tabIndex={0}
          aria-label={label}
          className="max-h-[28rem] overflow-auto rounded-lg border border-border/60 bg-muted p-3 font-mono text-xs whitespace-pre-wrap break-words"
        >
          {text}
        </pre>
      ) : (
        <p className="rounded-lg border border-dashed border-border p-3 text-sm text-muted-foreground">{missing}</p>
      )}
    </div>
  )
}

/** An agent run: its input (the rendered prompt, redacted) and output side by side, with truncation notices. */
function AgentRunBody({ item }: { item: TimelineItem }) {
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <TextBlock
        label="Input"
        text={item.input}
        truncated={item.inputTruncated === true}
        missing="No input was recorded for this run. Runs from before Tevrin 0.7.0 don't store it."
      />
      <TextBlock
        label="Output"
        text={item.output}
        truncated={item.outputTruncated === true}
        missing="This run has no output."
      />
    </div>
  )
}

/** An artifact version: its diff to the previous version of the same artifact, or its content when it is the first. */
function ArtifactVersionBody({ runId, item, definition, labels }: { runId: string; item: TimelineItem; definition: PipelineDefinition | undefined; labels: DefinitionLabels }) {
  const artifacts = useRunArtifacts(runId)
  const key = item.artifactKey
  const version = item.artifactVersion
  if (!key || version == null) {
    return null
  }
  const title = labels.artifact(key)
  const versions = artifacts.data?.find((a) => a.key === key)?.versions.map((v) => v.version) ?? []
  const previous = versions.filter((v) => v < version).reduce<number | null>((best, v) => (best === null || v > best ? v : best), null)
  if (artifacts.isPending) {
    return <LoadingState label={`Loading the versions of ${title}…`} />
  }
  if (artifacts.isError) {
    // Without the version list the previous version is unknown: never guess "first version".
    return <ErrorState error={artifacts.error} onRetry={() => void artifacts.refetch()} title={`The versions of ${title} couldn't load`} />
  }
  if (previous === null) {
    return (
      <div className="flex flex-col gap-2">
        <p className="text-sm text-muted-foreground">The first version of {title}.</p>
        <ArtifactViewer runId={runId} artifactKey={key} version={version} definition={definition?.artifacts.find((a) => a.key === key)} title={title} />
      </div>
    )
  }
  return <ArtifactDiff runId={runId} artifactKey={key} from={previous} to={version} title={title} />
}

/** FR-306: one replay step: the entry in words, then its agent input and output or its artifact diff. */
export function ReplayEntry({ runId, item, definition, labels }: { runId: string; item: TimelineItem; definition: PipelineDefinition | undefined; labels: DefinitionLabels }) {
  return (
    <article aria-label="Timeline entry" className="flex flex-col gap-4">
      <TimelineEntry item={item} labels={labels} />
      {item.kind === 'agent_run' && <AgentRunBody item={item} />}
      {item.kind === 'artifact_version' && <ArtifactVersionBody runId={runId} item={item} definition={definition} labels={labels} />}
    </article>
  )
}
