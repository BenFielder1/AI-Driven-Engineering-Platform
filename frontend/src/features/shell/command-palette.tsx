import { ExternalLink } from 'lucide-react'

import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '../../components/ui/command'
import { useAppRouter } from '../../lib/navigation'
import { setThemePreference } from '../../lib/theme'
import { paletteActions, paletteEntries, type PaletteAction } from './nav-config'
import type { ShellNav } from './use-shell-nav'

/**
 * FR-53: the ⌘K / Ctrl+K command palette over every page in nav-config (the current project's pages first), then the
 * FR-209 actions: Go to Home, New project, Import repository, Switch theme (Light/Dark/System) and Open docs (new tab).
 * The same shortcut opens and closes it; the dialog returns focus to whatever opened it.
 */
export function CommandPalette({ open, onOpenChange, nav }: { open: boolean; onOpenChange: (open: boolean) => void; nav: ShellNav }) {
  const router = useAppRouter()
  const entries = paletteEntries(
    nav.viewer,
    nav.projectId ? {
          id: nav.projectId,
          name: nav.projectName ?? 'Project',
          deploymentsEnabled: nav.deploymentsEnabled,
          migrationsEnabled: nav.migrationsEnabled,
          analysisEnabled: nav.analysisEnabled,
        } : undefined,
  )
  const groups = [...new Set(entries.map((e) => e.group))]

  function go(href: string) {
    onOpenChange(false)
    void router.navigate({ href })
  }

  function run(action: PaletteAction) {
    switch (action.run.type) {
      case 'navigate':
        go(action.run.href)
        return
      case 'theme':
        setThemePreference(action.run.theme)
        onOpenChange(false)
        return
      case 'external':
        window.open(action.run.href, '_blank', 'noopener,noreferrer')
        onOpenChange(false)
        return
    }
  }

  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title="Command palette" description="Search for a page or an action" label="Search pages">
      <CommandInput placeholder="Search pages and actions…" />
      <CommandList>
        <CommandEmpty>No pages or actions match.</CommandEmpty>
        {groups.map((group) => (
          <CommandGroup key={group} heading={group}>
            {entries
              .filter((e) => e.group === group)
              .map((entry) => (
                <CommandItem
                  key={entry.key}
                  value={`${entry.group} ${entry.label}`}
                  keywords={[...entry.keywords]}
                  onSelect={() => {
                    go(entry.href)
                  }}
                >
                  <entry.icon aria-hidden />
                  {entry.label}
                </CommandItem>
              ))}
          </CommandGroup>
        ))}
        <CommandGroup heading="Actions">
          {paletteActions().map((action) => (
            <CommandItem
              key={action.key}
              value={`Actions ${action.label}`}
              keywords={[...action.keywords]}
              onSelect={() => {
                run(action)
              }}
            >
              <action.icon aria-hidden />
              {action.label}
              {action.run.type === 'external' && (
                <>
                  <ExternalLink aria-hidden className="ml-auto" />
                  <span className="sr-only"> (opens in a new tab)</span>
                </>
              )}
            </CommandItem>
          ))}
        </CommandGroup>
      </CommandList>
    </CommandDialog>
  )
}
