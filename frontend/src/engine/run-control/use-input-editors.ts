import { useCallback, useRef } from 'react'

import type { InputEditorHandle } from '../schema-form/input-editor'

/** The input editors of a dialog by artifact key: each registers its handle, and submit collects them all. */
export function useInputEditors() {
  const handles = useRef(new Map<string, InputEditorHandle>())

  const refFor = useCallback(
    (key: string) => (handle: InputEditorHandle | null) => {
      if (handle) {
        handles.current.set(key, handle)
      } else {
        handles.current.delete(key)
      }
    },
    [],
  )

  /** Every editor's value by key (optional ones left blank are left out), or null when any shows errors. */
  const collect = useCallback(async (keys: readonly string[]): Promise<Record<string, unknown> | null> => {
    const values: Record<string, unknown> = {}
    let valid = true
    for (const key of keys) {
      const handle = handles.current.get(key)
      if (!handle) {
        continue
      }
      const result = await handle.collect()
      if (result.kind === 'invalid') {
        valid = false
      } else if (result.kind === 'value') {
        values[key] = result.value
      }
    }
    return valid ? values : null
  }, [])

  return { refFor, collect }
}
