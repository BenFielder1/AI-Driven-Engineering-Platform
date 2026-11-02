import { useEffect, useRef } from 'react'
import { toast } from 'sonner'

import { useStackOptions, type StackCatalog } from '../stack/stack-api'
import { applyOptions } from '../stack/stack-model'
import { optionsRequestOfProfile, selectionOfProfile, withHostsOf, type StackProfileDocument } from './stack-profile-format'

/**
 * FR-184: `POST /api/stack/options` for a working profile, and FR-114's reaction to it: a host the server now reports as
 * invalidated (a layer or host change ruled it out) is cleared, with the toast naming it and why. Layers are never
 * cleared: an unavailable layer choice stays, with the server's reason shown by its picker (FR-183, e.g. R3).
 */
export function useProfileOptions(
  catalog: StackCatalog | undefined,
  doc: StackProfileDocument | null,
  onHostsCleared: ((next: StackProfileDocument) => void) | null,
) {
  const options = useStackOptions(doc ? optionsRequestOfProfile(doc) : {}, doc !== null)
  const processed = useRef<string | null>(null)

  useEffect(() => {
    if (!doc || !onHostsCleared || !options.data || options.isPlaceholderData) {
      return
    }
    const marker = `${String(options.dataUpdatedAt)}:${JSON.stringify(doc.hosts)}`
    if (processed.current === marker) {
      return
    }
    processed.current = marker
    const current = selectionOfProfile(doc)
    const { selection, cleared } = applyOptions(current, options.data, catalog)
    const hostCleared = cleared.filter((c) => c.choice === 'frontendHost' || c.choice === 'backendHost' || c.choice === 'databaseHost')
    const next = {
      ...current,
      frontendHost: selection.frontendHost,
      backendHost: selection.backendHost,
      databaseHost: selection.databaseHost,
      local: selection.local,
      originModel: selection.originModel,
    }
    if (JSON.stringify(next) === JSON.stringify(current)) {
      return
    }
    // The toast region is a polite live region, so screen readers hear what was cleared too.
    for (const c of hostCleared) {
      toast.warning(c.message)
    }
    onHostsCleared(withHostsOf(doc, next))
  }, [catalog, doc, onHostsCleared, options.data, options.dataUpdatedAt, options.isPlaceholderData])

  return options
}
