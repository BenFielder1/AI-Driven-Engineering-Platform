import { LifecycleStatusBadge } from '../../../../components/shared/status-badge'
import { previewCaptions, previewDeployments, previewLabels, previewRuns } from '../../content/previews'
import { PreviewFrame } from './preview-frame'
import { DiffPreview, GatePreview, RailPreview } from './preview-parts'

/** FR-213, FR-220: the small previews beside Product sections. Fixture data, real badges. */
export function GatesPreview() {
  return (
    <PreviewFrame caption={previewCaptions.gates}>
      <div className="flex flex-col gap-3">
        <RailPreview />
        <GatePreview />
      </div>
    </PreviewFrame>
  )
}

export function ArtifactsPreview() {
  return (
    <PreviewFrame caption={previewCaptions.artifacts}>
      <DiffPreview />
    </PreviewFrame>
  )
}

export function RunsPreview() {
  return (
    <PreviewFrame caption={previewCaptions.runs}>
      <ul className="divide-y divide-border text-sm">
        {previewRuns.map((run) => (
          <li key={run.agent} className="flex items-center justify-between gap-3 py-2 first:pt-0 last:pb-0">
            <span>
              <span className="block font-medium">{run.agent}</span>
              <span className="font-mono text-xs text-muted-foreground">{run.model}</span>
            </span>
            <LifecycleStatusBadge status={run.status}>{run.label}</LifecycleStatusBadge>
          </li>
        ))}
      </ul>
    </PreviewFrame>
  )
}

export function DeploymentsPreview() {
  return (
    <PreviewFrame caption={previewCaptions.deployments}>
      <ul className="divide-y divide-border text-sm">
        {previewDeployments.map((d) => (
          <li key={d.part} className="flex items-center justify-between gap-3 py-2 first:pt-0 last:pb-0">
            <span>
              <span className="block font-medium">{d.part}</span>
              <span className="text-xs text-muted-foreground">
                {d.platform} · {d.env}
              </span>
            </span>
            <LifecycleStatusBadge status={d.status}>{d.status === 'running' ? previewLabels.deploying : previewLabels.ready}</LifecycleStatusBadge>
          </li>
        ))}
      </ul>
    </PreviewFrame>
  )
}
