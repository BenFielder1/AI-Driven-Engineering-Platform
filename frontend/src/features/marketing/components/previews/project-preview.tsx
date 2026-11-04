import { PhaseBadge } from '../../../../components/shared/phase-badge'
import { ProjectVersionBadges } from '../../../../components/shared/version-badge'
import { previewCaptions, previewProject } from '../../content/previews'
import { PreviewFrame } from './preview-frame'
import { DiffPreview, GatePreview, RailPreview } from './preview-parts'

/**
 * FR-212, FR-220: the hero preview: a project page with the lifecycle rail, a spec approval gate awaiting a decision,
 * and the spec's diff between versions. Static fixture data, theme-aware through the tokens.
 */
export function ProjectPreview({ className }: { className?: string }) {
  return (
    <PreviewFrame
      chrome
      className={className}
      caption={previewCaptions.project}
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-base font-semibold">{previewProject.name}</p>
          <div className="flex flex-wrap items-center gap-1">
            <PhaseBadge phase="specification" />
            <ProjectVersionBadges released={previewProject.releasedVersion} development={previewProject.developmentVersion} />
          </div>
        </div>
        <RailPreview />
        <GatePreview />
        <DiffPreview />
      </div>
    </PreviewFrame>
  )
}
