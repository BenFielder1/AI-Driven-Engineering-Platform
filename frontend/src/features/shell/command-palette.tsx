
import { CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '../../components/ui/command'
import { useAppRouter } from '../../lib/navigation'
import { paletteEntries } from './nav-config'
import type { ShellNav } from './use-shell-nav'

/** FR-53: the ⌘K / Ctrl+K command palette over every page in nav-config (the current project's pages first). */
export function CommandPalette({ open, onOpenChange, nav }: { open: boolean; onOpenChange: (open: boolean) => void; nav: ShellNav }) {
  const router = useAppRouter()
  const entries = paletteEntries(
    nav.viewer,
    nav.projectId ? {
          id: nav.projectId,
          name: nav.projectName ?? 'Project',
          deploymentsEnabled: nav.deploymentsEnabled,
          migrationsEnabled: nav.migrationsEnabled,
        } : undefined,
  )
  const groups = [...new Set(entries.map((e) => e.group))]

  function go(href: string) {
    onOpenChange(false)
    void router.navigate({ href })
  }

  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title="Command palette" description="Search for a page and go to it" label="Search pages">
      <CommandInput placeholder="Search pages…" />
      <CommandList>
        <CommandEmpty>No pages match.</CommandEmpty>
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
      </CommandList>
    </CommandDialog>
  )
}
