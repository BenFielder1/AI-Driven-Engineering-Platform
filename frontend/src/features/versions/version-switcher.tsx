import { useQuery } from '@tanstack/react-query'
import { Check, ChevronDown, GitBranch } from 'lucide-react'

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '../../components/ui/dropdown-menu'
import { releaseVersionStatusLabels } from '../projects/labels'
import { projectQueryOptions } from '../projects/projects-api'
import { useSelectedReleaseVersion, useShowReleaseVersion } from './use-selected-release-version'

/**
 * FR-62 version switcher in the shell header on project pages: lists every release version (in development, awaiting
 * merge, released, imported baseline). Picking a past one sets `?release=<semver>`, which every project page reads
 * through `useSelectedReleaseVersion` and renders read-only; picking the current one clears it.
 */
export function VersionSwitcher({ projectId }: { projectId: string }) {
  const project = useQuery(projectQueryOptions(projectId))
  const currentId = project.data?.releaseVersionId ?? null
  const view = useSelectedReleaseVersion(projectId, currentId)
  const select = useShowReleaseVersion()

  if (!project.data || view.versions.length === 0 || !view.version) {
    return null
  }
  const shown = view.version

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={`Release version: ${shown.version} (${releaseVersionStatusLabels[shown.status].toLowerCase()}). Switch version`}
        data-testid="version-switcher"
        className="flex min-h-11 items-center gap-1.5 rounded-lg border border-border/60 bg-background/40 px-2.5 text-sm hover:bg-accent"
      >
        <GitBranch aria-hidden className="size-4 text-muted-foreground" />
        <span className="font-mono tabular-nums">{shown.version}</span>
        {view.readOnly && <span className="text-xs text-muted-foreground max-sm:hidden">read-only</span>}
        <ChevronDown aria-hidden className="size-4 text-muted-foreground" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="text-xs text-muted-foreground">Release versions</DropdownMenuLabel>
        {view.versions.map((v) => {
          const isCurrent = v.id === currentId
          const selected = v.id === shown.id
          return (
            <DropdownMenuItem
              key={v.id}
              onSelect={() => {
                select(isCurrent ? undefined : v.version)
              }}
            >
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="font-mono tabular-nums">{v.version}</span>
                <span className="text-xs text-muted-foreground">
                  {releaseVersionStatusLabels[v.status]}
                  {isCurrent ? ' · current' : ''}
                </span>
              </span>
              {selected ? <Check className="size-4" aria-label="Shown" /> : <span className="size-4" aria-hidden />}
            </DropdownMenuItem>
          )
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
