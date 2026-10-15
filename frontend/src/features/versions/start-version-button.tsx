import { useId, useState } from 'react'
import { toast } from 'sonner'

import { Button } from '../../components/ui/button'
import type { ProjectState } from '../projects/projects-api'
import { startVersionBlock } from './start-version-rules'
import { StartVersionDialog } from './start-version-dialog'
import { useReleaseVersions } from './versions-api'

/**
 * FR-60 "Start new version" (Owners; on the Release page and the overview). Disabled with the reason as tooltip and
 * description when the project isn't released, its release awaits merge, or a version is already in development.
 */
export function StartVersionButton({ projectId, state }: { projectId: string; state: ProjectState }) {
  const versions = useReleaseVersions(projectId)
  const [open, setOpen] = useState(false)
  const reasonId = useId()
  if (!versions.isSuccess) {
    return null
  }
  const block = startVersionBlock(state.status, versions.data)

  return (
    <div className="flex flex-col gap-1">
      <div>
        <Button
          variant="primary"
          aria-disabled={block ? true : undefined}
          aria-describedby={block ? reasonId : undefined}
          title={block?.reason}
          className={block ? 'cursor-not-allowed opacity-60' : undefined}
          onClick={() => {
            if (!block) {
              setOpen(true)
            }
          }}
        >
          Start new version
        </Button>
      </div>
      {block && (
        <p id={reasonId} className="text-sm text-muted-foreground">
          {block.reason}
        </p>
      )}
      {open && (
        <StartVersionDialog
          projectId={projectId}
          released={state.releasedVersion}
          suggested={state.suggestedNextVersion}
          versions={versions.data}
          onClose={() => {
            setOpen(false)
          }}
          onStarted={(v) => {
            setOpen(false)
            toast.success(`Version ${v.version} is in development. The project is back in Refinement.`)
          }}
        />
      )}
    </div>
  )
}
