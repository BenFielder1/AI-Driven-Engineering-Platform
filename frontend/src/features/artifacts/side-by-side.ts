import type { DiffLine } from './artifacts-api'

export interface DiffCell {
  line: number
  text: string
}

/**
 * One row of a side-by-side diff. `kind` is `context` (same line on both sides), `changed` (a removed line next to
 * the added line that replaced it), `removed` (only the left side) or `added` (only the right side).
 */
export interface LineRow {
  kind: 'context' | 'changed' | 'removed' | 'added'
  left: DiffCell | null
  right: DiffCell | null
}

/** A run of `count` unchanged lines the API left out, starting at `oldLine`/`newLine` on each side. */
export interface SkippedRow {
  kind: 'skipped'
  count: number
  oldLine: number
  newLine: number
}

export type SideBySideRow = LineRow | SkippedRow

/**
 * FR-21: pairs the API's ordered `lines` into side-by-side rows. Context lines sit on both sides; within each run of
 * changed lines (between two context lines) the removed and added lines are paired in order, row by row, and the
 * remainder of the longer side stands alone. `skipped` entries (unchanged lines far from any change) become one
 * `skipped` row.
 */
export function toSideBySide(lines: readonly DiffLine[]): SideBySideRow[] {
  const rows: SideBySideRow[] = []
  let removed: DiffCell[] = []
  let added: DiffCell[] = []

  const flush = () => {
    const count = Math.max(removed.length, added.length)
    for (let i = 0; i < count; i += 1) {
      const left = removed[i] ?? null
      const right = added[i] ?? null
      rows.push({ kind: left && right ? 'changed' : left ? 'removed' : 'added', left, right })
    }
    removed = []
    added = []
  }

  for (const line of lines) {
    if (line.kind === 'skipped') {
      flush()
      rows.push({ kind: 'skipped', count: line.skipped ?? 0, oldLine: line.oldLine ?? 0, newLine: line.newLine ?? 0 })
    } else if (line.kind === 'removed') {
      removed.push({ line: line.oldLine ?? 0, text: line.text })
    } else if (line.kind === 'added') {
      added.push({ line: line.newLine ?? 0, text: line.text })
    } else {
      flush()
      rows.push({
        kind: 'context',
        left: { line: line.oldLine ?? 0, text: line.text },
        right: { line: line.newLine ?? 0, text: line.text },
      })
    }
  }
  flush()
  return rows
}

/** Counts for the diff caption ("2 lines added, 1 removed"). */
export function diffStats(lines: readonly DiffLine[]): { added: number; removed: number } {
  let added = 0
  let removed = 0
  for (const line of lines) {
    if (line.kind === 'added') {
      added += 1
    } else if (line.kind === 'removed') {
      removed += 1
    }
  }
  return { added, removed }
}
