import { Eye } from 'lucide-react'

import { Button } from '../../components/ui/button'
import { releaseVersionStatusLabels } from '../projects/labels'
import { useShowReleaseVersion } from './use-selected-release-version'
import type { ReleaseVersion } from './versions-api'

/** SPEC §7.2 UI rule: "Viewing 1.1.0 (released). Read-only", with a way back to the current version. */
export function ReadOnlyBanner({ version }: { version: ReleaseVersion }) {
  const show = useShowReleaseVersion()
  return (
    <div
      role="status"
      data-testid="read-only-banner"
      className="mb-5 flex flex-wrap items-center justify-between gap-2 rounded-xl border border-warning/40 bg-warning/10 px-4 py-3 text-sm"
    >
      <p className="flex items-center gap-2 font-medium">
        <Eye aria-hidden className="size-4 shrink-0" />
        {`Viewing ${version.version} (${releaseVersionStatusLabels[version.status].toLowerCase()}). Read-only`}
      </p>
      <Button
        variant="secondary"
        onClick={() => {
          show(undefined)
        }}
      >
        Back to the current version
      </Button>
    </div>
  )
}
