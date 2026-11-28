import { useState } from 'react'

import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { cn } from '../../lib/utils'
import { useRunArtifacts } from '../api/hooks'
import type { PipelineDefinition, RunArtifact } from '../api/types'
import { definitionLabels, followUpLabel, itemLabel } from '../labels'
import { ArtifactDiff, ArtifactViewer } from './artifact-viewer'

type View = 'content' | 'changes'

/** FR-363: a version's construct label (its variant, follow-up or map item), for the version picker. */
function versionLabel(v: RunArtifact['versions'][number]): string {
  const parts = [v.variant ? followUpLabel(v.variant) : null, v.itemIndex != null ? itemLabel(v.itemIndex) : null].filter(Boolean)
  return parts.length > 0 ? `v${String(v.version)} (${parts.join(', ')})` : `v${String(v.version)}`
}

function ArtifactDetail({ runId, artifact, definition }: { runId: string; artifact: RunArtifact; definition: PipelineDefinition | undefined }) {
  const labels = definitionLabels(definition)
  const title = labels.artifact(artifact.key)
  const versions = artifact.versions.map((v) => v.version)
  const [selected, setSelected] = useState<number | null>(null)
  const [view, setView] = useState<View>('content')
  const version = selected !== null && versions.includes(selected) ? selected : (versions[0] ?? null)
  // Versions are newest first: the previous one is the next lower number in the list.
  const previous = version === null ? undefined : versions.find((v) => v < version)
  if (version === null) {
    return <EmptyState title={`${title} has no versions yet`} />
  }
  const viewButton = (value: View, text: string) => (
    <button
      type="button"
      aria-pressed={view === value}
      onClick={() => {
        setView(value)
      }}
      className={cn(
        'min-h-8 rounded-md border border-border px-3 text-sm',
        view === value ? 'bg-primary text-primary-foreground' : 'bg-card hover:bg-muted',
      )}
    >
      {text}
    </button>
  )
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end gap-3">
        <SelectField
          label={`${title} version`}
          value={String(version)}
          onChange={(e) => {
            setSelected(Number(e.target.value))
          }}
        >
          {artifact.versions.map((v) => (
            <option key={v.version} value={v.version}>
              {versionLabel(v)}
            </option>
          ))}
        </SelectField>
        {previous !== undefined && (
          <div role="group" aria-label="Show" className="flex gap-2">
            {viewButton('content', 'Content')}
            {viewButton('changes', `Changes from v${String(previous)}`)}
          </div>
        )}
      </div>
      {view === 'changes' && previous !== undefined ? (
        <ArtifactDiff runId={runId} artifactKey={artifact.key} from={previous} to={version} title={title} />
      ) : (
        <ArtifactViewer
          runId={runId}
          artifactKey={artifact.key}
          version={version}
          definition={definition?.artifacts.find((a) => a.key === artifact.key)}
          title={title}
        />
      )}
    </div>
  )
}

/**
 * FR-306: the run's artifacts by key (titles from the definition) → the selected version's content, or its changes
 * from the previous version.
 */
export function RunArtifacts({ runId, definition }: { runId: string; definition: PipelineDefinition | undefined }) {
  const artifacts = useRunArtifacts(runId)
  const [openKey, setOpenKey] = useState<string | null>(null)
  const labels = definitionLabels(definition)

  if (artifacts.isPending) {
    return <LoadingState label="Loading artifacts…" />
  }
  if (artifacts.isError) {
    return <ErrorState error={artifacts.error} onRetry={() => void artifacts.refetch()} title="The artifacts couldn't load" />
  }
  if (artifacts.data.length === 0) {
    return <EmptyState title="No artifacts yet">Artifacts appear here when a stage writes one.</EmptyState>
  }
  const open = artifacts.data.find((a) => a.key === openKey) ?? artifacts.data[0]
  return (
    <div className="flex flex-col gap-4 lg:flex-row">
      <ul className="flex shrink-0 flex-col gap-1 lg:w-56" aria-label="Artifacts">
        {artifacts.data.map((artifact) => {
          const latest = artifact.versions[0]
          const current = artifact.key === open?.key
          return (
            <li key={artifact.key}>
              <button
                type="button"
                aria-current={current ? 'true' : undefined}
                onClick={() => {
                  setOpenKey(artifact.key)
                }}
                className={cn(
                  'flex min-h-10 w-full items-center justify-between gap-2 rounded-md px-3 text-left text-sm',
                  current ? 'bg-muted font-medium' : 'hover:bg-muted',
                )}
              >
                <span className="min-w-0 break-words">{labels.artifact(artifact.key)}</span>
                <span className="shrink-0 font-mono text-xs text-muted-foreground">
                  {latest ? `v${String(latest.version)}` : '—'}
                </span>
              </button>
            </li>
          )
        })}
      </ul>
      <div className="min-w-0 flex-1">
        {open && <ArtifactDetail key={open.key} runId={runId} artifact={open} definition={definition} />}
      </div>
    </div>
  )
}
