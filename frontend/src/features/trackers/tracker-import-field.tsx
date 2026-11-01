import { X } from 'lucide-react'
import { useState } from 'react'

import { Button } from '../../components/ui/button'
import { TrackerBrowseDialog } from './tracker-browse-dialog'
import {
  MAX_IMPORTS_PER_REQUEST,
  MAX_SOURCE_ITEMS,
  pickedItemCount,
  removePickedItem,
  trackerNames,
  type PickedImport,
} from './tracker-picks'

/**
 * FR-171: "Import from tracker" and the picked items as removable chips, on the wizard's Idea step and in Start new
 * version. The items are sent as `sourceImports[]` when the project or version is created.
 */
export function TrackerImportField({
  picks,
  onChange,
  onOpenConnections,
}: {
  picks: readonly PickedImport[]
  onChange: (next: PickedImport[]) => void
  onOpenConnections?: () => void
}) {
  const [browsing, setBrowsing] = useState(false)
  const count = pickedItemCount(picks)
  const capacity = MAX_SOURCE_ITEMS - count
  const atImportLimit = picks.length >= MAX_IMPORTS_PER_REQUEST
  const full = capacity <= 0 || atImportLimit

  return (
    <section aria-label="Tracker items" className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="secondary"
          disabled={full}
          onClick={() => {
            setBrowsing(true)
          }}
        >
          Import from tracker
        </Button>
        <p className="text-sm text-muted-foreground">
          {full
            ? atImportLimit
              ? `That's the most imports for one request (${String(MAX_IMPORTS_PER_REQUEST)}). Remove some items to add others.`
              : `That's the most items (${String(MAX_SOURCE_ITEMS)}). Remove some to add others.`
            : 'Optional. Imported items add to what you write here.'}
        </p>
      </div>
      {count > 0 && (
        <ul aria-label="Items to import" className="flex max-h-48 flex-wrap gap-1 overflow-y-auto">
          {picks.flatMap((pick, pickIndex) =>
            pick.items.map((item) => (
              <li
                key={`${String(pickIndex)}:${item.key}`}
                className="flex max-w-full items-center gap-1 rounded-full border border-border bg-background/40 py-0.5 pr-0.5 pl-2 text-xs"
              >
                <span className="sr-only">{trackerNames[pick.provider]}: </span>
                <span className="font-mono">{item.key}</span>
                <span className="min-w-0 truncate" title={item.title}>
                  {item.title}
                </span>
                <button
                  type="button"
                  aria-label={`Remove ${item.key}`}
                  className="inline-flex size-6 shrink-0 items-center justify-center rounded-full hover:bg-muted"
                  onClick={() => {
                    onChange(removePickedItem(picks, pickIndex, item.key))
                  }}
                >
                  <X aria-hidden className="size-3" />
                </button>
              </li>
            )),
          )}
        </ul>
      )}
      {browsing && (
        <TrackerBrowseDialog
          capacity={capacity}
          onClose={() => {
            setBrowsing(false)
          }}
          onAdd={(pick) => {
            onChange([...picks, pick])
            setBrowsing(false)
          }}
          onOpenConnections={onOpenConnections}
        />
      )}
    </section>
  )
}
