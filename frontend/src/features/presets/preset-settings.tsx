import { useState } from 'react'
import { toast } from 'sonner'

import { Alert } from '../../components/ui/alert'
import { Button } from '../../components/ui/button'
import { SelectField } from '../../components/ui/select-field'
import { errorMessage, isApiError, messageFor } from '../../lib/errors'
import { useProjectState, type Project } from '../projects/projects-api'
import { PresetChip } from './preset-chip'
import { PresetDetails } from './preset-details'
import { usePresets, useUpdateProjectPreset } from './presets-api'

/** The select's value for "No preset (general)". */
const NONE = ''

/**
 * Why the preset can't change now (FR-74): nothing is in development, or the version's implementation plan is locked.
 * The API decides (409 PRESET_LOCKED); this only explains it before the Owner tries.
 */
function useLockReason(project: Project): string | null {
  const state = useProjectState(project.id)
  if (project.developmentVersion === null) {
    return 'No version is in development. Choose a preset when you start the next version.'
  }
  const plan = state.data?.artifacts.find((a) => a.type === 'implementation_plan')
  return typeof plan?.currentLockedVersion === 'number' ? messageFor('PRESET_LOCKED') : null
}

/**
 * Settings → General → Stack (FR-74, SPEC §2): the preset of the version in development. Owners change it until the
 * version's implementation plan is locked; Contributors and Viewers see it read-only.
 */
export function PresetSettingsSection({ project, canChange }: { project: Project; canChange: boolean }) {
  const presets = usePresets()
  const update = useUpdateProjectPreset(project.id)
  const lockReason = useLockReason(project)
  const current = project.presetId ?? NONE
  const [picked, setPicked] = useState(current)
  const [lockedByServer, setLockedByServer] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)

  // The project changed elsewhere (e.g. another Owner): follow it.
  const [lastCurrent, setLastCurrent] = useState(current)
  if (current !== lastCurrent) {
    setLastCurrent(current)
    setPicked(current)
  }

  const locked = lockedByServer || lockReason !== null
  const reason = lockedByServer ? messageFor('PRESET_LOCKED') : lockReason

  const save = () => {
    setFailure(null)
    update.mutate(picked === NONE ? null : picked, {
      onSuccess: (updated) => {
        toast.success(updated.presetName ? `Preset changed to ${updated.presetName}.` : 'Preset removed: no preset (general).')
      },
      onError: (error) => {
        if (isApiError(error, 'PRESET_LOCKED')) {
          setLockedByServer(true)
          setPicked(current)
        } else {
          setFailure(errorMessage(error, 'changePreset'))
        }
      },
    })
  }

  return (
    <section aria-labelledby="preset-settings-title" className="mt-8 flex flex-col gap-3 border-t border-border/60 pt-6">
      <h2 id="preset-settings-title" className="text-xl">
        Stack
      </h2>
      <p className="text-sm">
        <span className="text-muted-foreground">Preset of </span>
        <span className="font-mono">{project.developmentVersion ?? project.releasedVersion ?? 'this version'}</span>
        <span className="text-muted-foreground">: </span>
        {project.presetName ? (
          <PresetChip name={project.presetName} version={project.presetVersion} />
        ) : (
          <span className="font-medium">No preset (general)</span>
        )}
      </p>

      {!canChange && <p className="text-sm text-muted-foreground">Only project Owners change the preset.</p>}

      {canChange && (
        <div className="flex flex-col gap-3">
          <SelectField
            id="settings-preset"
            label="Preset"
            hint={reason ?? 'Applies to the version in development: its specification, plan, specialist agents and review.'}
            value={picked}
            disabled={locked || presets.isPending}
            onChange={(e) => {
              setPicked(e.target.value)
            }}
          >
            <option value={NONE}>No preset (general)</option>
            {presets.data?.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} {p.version}
              </option>
            ))}
            {current !== NONE && !presets.data?.some((p) => p.id === current) && (
              <option value={current}>{project.presetName ?? current}</option>
            )}
          </SelectField>
          {presets.isError && <p className="text-sm text-destructive">{errorMessage(presets.error)}</p>}
          {failure && (
            <Alert tone="error" role="alert" title="We couldn't change the preset">
              <p>{failure}</p>
            </Alert>
          )}
          <div>
            <Button
              variant="secondary"
              disabled={locked || picked === current}
              pending={update.isPending}
              pendingLabel="Changing…"
              onClick={save}
            >
              Change preset
            </Button>
          </div>
          {!locked && picked !== NONE && picked !== current && (
            <div className="rounded-lg border border-border/60 bg-background/40 p-4">
              <PresetDetails presetId={picked} />
            </div>
          )}
        </div>
      )}
    </section>
  )
}
