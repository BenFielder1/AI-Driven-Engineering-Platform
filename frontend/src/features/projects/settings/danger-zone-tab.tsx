import { useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useRef, useState } from 'react'

import { Alert } from '../../../components/ui/alert'
import { Button } from '../../../components/ui/button'
import { ConfirmDialog } from '../../../components/ui/confirm-dialog'
import { TextField } from '../../../components/ui/text-field'
import { errorMessage } from '../../../lib/errors'
import { RollbackSection } from '../../release/rollback-section'
import { projectKeys, useDeleteProject, type Project } from '../projects-api'

/** Settings → Danger zone: roll back (FR-44, FR-45) and delete with typed-name confirmation (FR-7, SPEC §7). */
export function DangerZoneTab({ project }: { project: Project }) {
  const del = useDeleteProject(project.id)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [typed, setTyped] = useState('')
  const [error, setError] = useState<unknown>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const matches = typed === project.name

  function confirmDelete() {
    if (!matches) {
      return
    }
    setError(null)
    del.mutate(undefined, {
      onSuccess: () => {
        setOpen(false)
        // Leave the page first, so nothing re-requests the deleted project.
        void navigate({ to: '/projects' }).then(() => {
          queryClient.removeQueries({ queryKey: projectKeys.detail(project.id) })
        })
      },
      onError: (e) => {
        setOpen(false)
        setError(e)
      },
    })
  }

  return (
    <div className="flex max-w-2xl flex-col gap-6">
      <h2 className="text-xl">Danger zone</h2>

      <RollbackSection
        projectId={project.id}
        developmentVersion={project.developmentVersion}
        releasedVersion={project.releasedVersion}
        currentCheckpoint={project.checkpoint}
        awaitingMerge={project.status === 'awaiting_merge'}
      />

      <section aria-labelledby="delete-title" className="rounded-lg border border-destructive p-4">
        <h3 id="delete-title" className="font-semibold">
          Delete this project
        </h3>
        <p className="mb-3 mt-1 text-sm text-muted-foreground">
          Deletes the project and everything it owns: artifacts, gates, runs, reviews and settings. This can&apos;t be
          undone.
        </p>
        {error !== null && (
          <Alert tone="error" role="alert" title="We couldn't delete the project" className="mb-3">
            <p>{errorMessage(error)}</p>
          </Alert>
        )}
        <Button
          variant="danger"
          onClick={() => {
            setTyped('')
            setOpen(true)
          }}
        >
          Delete project
        </Button>
      </section>

      <ConfirmDialog
        open={open}
        title={`Delete ${project.name}?`}
        confirmLabel="Delete project permanently"
        pendingLabel="Deleting…"
        pending={del.isPending}
        destructive
        confirmDisabled={!matches}
        initialFocusRef={inputRef}
        onConfirm={confirmDelete}
        onCancel={() => {
          setOpen(false)
        }}
      >
        <p>Everything in this project is deleted permanently.</p>
        <TextField
          ref={inputRef}
          id="delete-confirm-name"
          label={`Type the project name (${project.name}) to confirm`}
          autoComplete="off"
          className="mt-3 text-foreground"
          value={typed}
          onChange={(e) => {
            setTyped(e.target.value)
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              confirmDelete()
            }
          }}
        />
      </ConfirmDialog>
    </div>
  )
}
