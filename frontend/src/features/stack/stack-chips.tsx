import { cn } from '../../lib/utils'
import { useStackCatalog, type StackCatalog } from './stack-api'

/** A pack id's display name ("be-aspnet" → "ASP.NET Core"), from any catalog option that selects it. */
export function packName(catalog: StackCatalog | undefined, pack: string): string {
  for (const choice of catalog?.choices ?? []) {
    const option = choice.options.find((o) => o.pack === pack || o.id === pack)
    if (option) {
      return option.name
    }
  }
  return pack
}

/** Every pack of the catalog once, for the projects list's `pack` filter: "Backend: ASP.NET Core". */
export function catalogPacks(catalog: StackCatalog | undefined): { pack: string; label: string }[] {
  const seen = new Map<string, string>()
  const layerName = (id: string) => catalog?.layers.find((l) => l.id === id)?.name ?? id
  for (const choice of catalog?.choices ?? []) {
    for (const option of choice.options) {
      if (option.pack && !seen.has(option.pack)) {
        seen.set(option.pack, `${layerName(choice.layer)}: ${option.name}`)
      }
    }
  }
  return [...seen].map(([pack, label]) => ({ pack, label }))
}

/**
 * The stack chips of a project (FR-117, §7.2): one small chip per pack of the current version's composition, or
 * nothing when it has no stack. `stackPacks` are pack ids; names come from the catalog when it has loaded.
 */
export function StackChips({ packs, label, className }: { packs: readonly string[]; label: string; className?: string }) {
  const catalog = useStackCatalog(packs.length > 0)
  if (packs.length === 0) {
    return <span className="text-xs text-muted-foreground">No stack</span>
  }
  return (
    <ul aria-label={label} data-testid="stack-chips" className={cn('flex flex-wrap gap-1', className)}>
      {packs.map((pack) => (
        <li key={pack} className="rounded border border-border/60 bg-background/60 px-1.5 py-0.5 text-xs">
          {packName(catalog.data, pack)}
        </li>
      ))}
    </ul>
  )
}
