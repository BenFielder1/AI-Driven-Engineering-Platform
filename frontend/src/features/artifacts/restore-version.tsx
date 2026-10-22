import { useRef, useState, type RefObject } from 'react'
import { toast } from 'sonner'

import { Alert, ErrorSummary } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { ModalDialog } from '../../components/ui/modal-dialog'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { artifactTypeLabels } from '../projects/labels'
import { useProjectAccess } from '../projects/project-context'
import { restoreBlockReason } from '../stages/stage-model'
import { useArtifactDiff, useRestoreArtifact, type Artifact, type ArtifactSummary, type ArtifactType } from './artifacts-api'
import { DiffView } from './diff-view'
import { restoreBlockMessage, restoreErrorMessage } from './restore-copy'

/** FR-58: the diff preview (the chosen version against the latest), then confirm. */
function RestoreDialog({
  type,
  version,
  latest,
  onClose,
  onRestored,
  fallbackFocusRef,
}: {
  type: ArtifactType
  version: number
  latest: number
  onClose: () => void
  onRestored: (created: Artifact) => void
  fallbackFocusRef: RefObject<HTMLElement | null>
}) {
  const { project } = useProjectAccess()
  const diff = useArtifactDiff(project.id, type, { from: latest, to: version })
  const restore = useRestoreArtifact(project.id, type)
  const [error, setError] = useState<string | null>(null)
  const label = artifactTypeLabels[type].toLowerCase()

  return (
    <ModalDialog
      open
      title={`Restore ${label} v${String(version)}?`}
      description={`A new draft (v${String(latest + 1)}) is created with v${String(version)}'s content, based on the latest version v${String(latest)}. It then needs approval like any draft.`}
      busy={restore.isPending}
      fallbackFocusRef={fallbackFocusRef}
      onClose={onClose}
      className="sm:max-w-4xl"
    >
      <div className="flex flex-col gap-4">
        <ErrorSummary title="We couldn't restore this version" errors={error ? [{ message: error }] : []} />
        <section aria-label={`Changes from v${String(latest)} to v${String(version)}`} className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">
            What changes compared with the latest version (v{latest})
          </h3>
          {diff.isPending ? (
            <LoadingState label="Loading the diff…" />
          ) : diff.isError ? (
            <ErrorState error={diff.error} context="artifactDiff" title="The diff couldn't load" onRetry={() => void diff.refetch()} />
          ) : (
            <DiffView diff={diff.data} />
          )}
        </section>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={restore.isPending}>
            Cancel
          </Button>
          <Button
            pending={restore.isPending}
            pendingLabel="Restoring…"
            onClick={() => {
              setError(null)
              restore.mutate(version, {
                onSuccess: onRestored,
                onError: (e) => {
                  setError(restoreErrorMessage(e, type))
                },
              })
            }}
          >
            Restore v{version}
          </Button>
        </div>
      </div>
    </ModalDialog>
  )
}

/**
 * FR-58: "Restore this version" on a version older than the latest. Writers only; when the rules don't allow it
 * (another release version, stage complete, releasing, corrupted), the action is hidden and the reason shown instead.
 */
export function RestoreVersionAction({
  type,
  shown,
  latest,
  onRestored,
}: {
  type: ArtifactType
  shown: Pick<ArtifactSummary, 'version' | 'integrity' | 'releaseVersion'>
  latest: number
  onRestored: (version: number) => void
}) {
  const { project, readOnly, role } = useProjectAccess()
  const [open, setOpen] = useState(false)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const writer = role === 'owner' || role === 'contributor'
  if (!writer || shown.version >= latest) {
    return null
  }
  const blocked = restoreBlockReason({ type, project, readOnly, version: shown })
  return (
    <>
      {/* An open dialog stays up even if a refetch now blocks the restore, so the server's reason stays readable. */}
      {blocked && !open ? (
        <Alert tone="info" title="This version can't be restored">
          <p>{restoreBlockMessage(blocked, type)}</p>
        </Alert>
      ) : (
        <div>
          <Button
            ref={buttonRef}
            variant="secondary"
            onClick={() => {
              setOpen(true)
            }}
          >
            Restore this version
          </Button>
        </div>
      )}
      {open && (
        <RestoreDialog
          type={type}
          version={shown.version}
          latest={latest}
          fallbackFocusRef={buttonRef}
          onClose={() => {
            setOpen(false)
          }}
          onRestored={(created) => {
            setOpen(false)
            toast.success(`Restored v${String(shown.version)} as draft v${String(created.version)}. It now needs approval.`)
            onRestored(created.version)
          }}
        />
      )}
    </>
  )
}
