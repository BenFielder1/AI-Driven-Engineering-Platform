import { useQueries } from '@tanstack/react-query'
import { useState } from 'react'

import { panelClass } from '../../components/shared/panel-class'
import { Button } from '../../components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '../../components/ui/sheet'
import { ErrorState, LoadingState } from '../../components/ui/states'
import { cn } from '../../lib/utils'
import { TagList } from '../presets/preset-chip'
import { PresetDetails } from '../presets/preset-details'
import { deployTargetLabel, presetQueryOptions, usePresets, type PresetSummary } from '../presets/presets-api'

/** The agent count of each preset, from its detail (the summary doesn't carry the catalogue). */
function useAgentCounts(presets: readonly PresetSummary[]): Record<string, number | undefined> {
  const details = useQueries({ queries: presets.map((p) => presetQueryOptions(p.id)) })
  return Object.fromEntries(presets.map((p, i) => [p.id, details[i]?.data?.agents.length]))
}

/**
 * Admin → Integrations → Presets (FR-74, SPEC §7.2): every shipped preset with its version, stack, specialist agent
 * count and deployment targets; "View details" opens a sheet with its agents, spec sections, review checklist and
 * protected paths. Read-only: presets are data in the repo (D-V2-12).
 */
export function AdminPresetsPanel() {
  const presets = usePresets()
  const counts = useAgentCounts(presets.data ?? [])
  const [open, setOpen] = useState<PresetSummary | null>(null)

  let body
  if (presets.isPending) {
    body = <LoadingState label="Loading presets…" />
  } else if (presets.isError) {
    body = <ErrorState error={presets.error} title="The presets couldn't load" onRetry={() => void presets.refetch()} />
  } else if (presets.data.length === 0) {
    body = <p className="text-sm text-muted-foreground">No presets are installed. Projects use no preset (general).</p>
  } else {
    body = (
      <ul aria-label="Presets" className="flex flex-col gap-3">
        {presets.data.map((p) => {
          const agents = counts[p.id]
          return (
            <li key={p.id} className="flex flex-col gap-2 rounded-lg border border-border/60 bg-background/40 p-4">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <h4 className="font-semibold">
                    {p.name} <span className="font-mono text-xs text-muted-foreground tabular-nums">{p.version}</span>
                  </h4>
                  <code className="text-xs text-muted-foreground">{p.id}</code>
                </div>
                <Button
                  variant="secondary"
                  aria-label={`View details of ${p.name}`}
                  onClick={() => {
                    setOpen(p)
                  }}
                >
                  View details
                </Button>
              </div>
              <p className="text-sm text-muted-foreground">{p.description}</p>
              <TagList label={`${p.name} stack`} items={p.stack} />
              <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
                <div className="flex gap-1">
                  <dt className="text-muted-foreground">Specialist agents:</dt>
                  <dd className="font-medium tabular-nums">{agents ?? '…'}</dd>
                </div>
                <div className="flex gap-1">
                  <dt className="text-muted-foreground">Deploys to:</dt>
                  <dd className="font-medium">
                    {p.deployTargets.length === 0 ? 'None' : p.deployTargets.map(deployTargetLabel).join(', ')}
                  </dd>
                </div>
              </dl>
            </li>
          )
        })}
      </ul>
    )
  }

  return (
    <section aria-labelledby="admin-presets-title" className={cn(panelClass, 'flex flex-col gap-3 p-5')}>
      <h3 id="admin-presets-title" className="text-lg">
        Presets
      </h3>
      <p className="text-sm text-muted-foreground">
        Tech stack presets ship with AIDE. Owners pick one per version in the new project wizard or when starting a new
        version.
      </p>
      {body}
      <Sheet
        open={open !== null}
        onOpenChange={(next) => {
          if (!next) {
            setOpen(null)
          }
        }}
      >
        <SheetContent className="w-full overflow-y-auto sm:max-w-lg">
          {open && (
            <>
              <SheetHeader>
                <SheetTitle>
                  {open.name} {open.version}
                </SheetTitle>
                <SheetDescription>{open.description}</SheetDescription>
              </SheetHeader>
              <div className="px-4 pb-4">
                <p className="mb-3 text-xs break-all text-muted-foreground">
                  Content hash <code>{open.contentHash}</code>
                </p>
                <PresetDetails presetId={open.id} full />
              </div>
            </>
          )}
        </SheetContent>
      </Sheet>
    </section>
  )
}
