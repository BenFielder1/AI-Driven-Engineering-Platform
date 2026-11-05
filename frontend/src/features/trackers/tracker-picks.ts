import { z } from 'zod'

import type { Schemas } from '../../api/client'

export type TrackerProvider = Schemas['TrackerProvider']

/** §4.26: items across a version's active imports. */
export const MAX_SOURCE_ITEMS = 200
/** TrackerRules.MaxImportsPerRequest: `sourceImports[]` entries on one create request. */
export const MAX_IMPORTS_PER_REQUEST = 10

/** FR-171: the field error for an empty idea or request with items selected (D-V5-6). */
export const IDEA_REQUIRED_WITH_ITEMS = 'Say what you want to build. Imported items add to this.'

/** The tracker's display name (matches the backend's TrackerRules.DisplayName). */
export const trackerNames: Readonly<Record<TrackerProvider, string>> = {
  jira: 'Jira',
  linear: 'Linear',
  github: 'GitHub Issues',
  gitlab: 'GitLab Issues',
}

const statusCategory = z.enum(['todo', 'in_progress', 'done'])

const pickedImportSchema = z.object({
  provider: z.enum(['jira', 'linear', 'github', 'gitlab']),
  connectionId: z.string().nullable(),
  selection: z.object({
    keys: z.array(z.string()).nullable().optional(),
    query: z.string().nullable().optional(),
    containerId: z.string().nullable().optional(),
    filter: z
      .object({
        text: z.string().nullable().optional(),
        statusCategories: z.array(statusCategory).nullable().optional(),
        labels: z.array(z.string()).nullable().optional(),
        parent: z.string().nullable().optional(),
      })
      .nullable()
      .optional(),
  }),
  options: z.object({
    includeChildren: z.boolean(),
    includeComments: z.boolean(),
    includeAssignees: z.boolean(),
  }),
  items: z.array(z.object({ key: z.string(), title: z.string(), url: z.string() })),
})

/** FR-171: one import chosen in the browse dialog, with what the chips show. Nothing here is a secret. */
export type PickedImport = z.infer<typeof pickedImportSchema>
export type PickedItem = PickedImport['items'][number]

/**
 * The wizard keeps its picks as a JSON string, so its form stays flat and the FR-159 draft carries them to Connections
 * and back. Anything that doesn't parse counts as no picks.
 */
export function parsePicks(json: string): PickedImport[] {
  try {
    const parsed = z.array(pickedImportSchema).safeParse(JSON.parse(json))
    return parsed.success ? parsed.data : []
  } catch {
    return []
  }
}

export const serializePicks = (picks: readonly PickedImport[]): string => JSON.stringify(picks)

export const pickedItemCount = (picks: readonly PickedImport[]): number => picks.reduce((n, p) => n + p.items.length, 0)

export function toSourceImportRequest(pick: PickedImport): Schemas['SourceImportRequest'] {
  return { provider: pick.provider, connectionId: pick.connectionId, selection: pick.selection, options: pick.options }
}

/**
 * Removes one chip. A "select all matching" pick becomes the ticked keys that are left, because its query would bring
 * the removed item back. Removing the last item removes the pick.
 */
export function removePickedItem(picks: readonly PickedImport[], pickIndex: number, key: string): PickedImport[] {
  return picks.flatMap((pick, i) => {
    if (i !== pickIndex) {
      return [pick]
    }
    const items = pick.items.filter((item) => item.key !== key)
    if (items.length === 0) {
      return []
    }
    return [
      {
        ...pick,
        items,
        selection: { keys: items.map((item) => item.key), containerId: pick.selection.containerId ?? null },
      },
    ]
  })
}

/** Only http(s) links reach an href: tracker URLs and the session-storage draft are outside Tevrin's control. */
export function safeExternalUrl(url: string): string | null {
  try {
    const { protocol } = new URL(url)
    return protocol === 'https:' || protocol === 'http:' ? url : null
  } catch {
    return null
  }
}
