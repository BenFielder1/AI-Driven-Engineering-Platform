import type { ArtifactDiff } from './artifacts-api'
import { diffStats, toSideBySide, type DiffCell, type LineRow, type SkippedRow } from './side-by-side'

const plural = (n: number, word: string) => `${String(n)} ${word}${n === 1 ? '' : 's'}`

type Side = 'left' | 'right'

/** What a cell means, as text: added/removed is never conveyed by colour alone (NFR-A11Y, WCAG 1.4.1). */
function cellChange(row: LineRow, side: Side): 'removed' | 'added' | null {
  if (row.kind === 'context') {
    return null
  }
  if (side === 'left') {
    return row.left ? 'removed' : null
  }
  return row.right ? 'added' : null
}

const changeStyles = {
  removed: { cell: 'bg-danger-bg', marker: '−', label: 'Removed:' },
  added: { cell: 'bg-success-bg', marker: '+', label: 'Added:' },
} as const

function Cells({ cell, change, side }: { cell: DiffCell | null; change: 'removed' | 'added' | null; side: Side }) {
  const style = change ? changeStyles[change] : null
  const edge = side === 'right' ? 'border-l ' : ''
  if (!cell) {
    return (
      <>
        <td className={`w-10 ${edge}border-r border-border bg-surface px-2`} />
        <td className="bg-surface px-2">
          <span className="sr-only">No line</span>
        </td>
      </>
    )
  }
  return (
    <>
      <td className={`w-10 select-none ${edge}border-r border-border px-2 text-right align-top text-muted ${style?.cell ?? ''}`}>
        {cell.line}
      </td>
      <td className={`whitespace-pre-wrap break-words px-2 align-top ${style?.cell ?? ''}`}>
        {style && (
          <>
            <span aria-hidden="true" className="mr-1 font-bold">
              {style.marker}
            </span>
            <span className="sr-only">{style.label}</span>{' '}
          </>
        )}
        {cell.text || ' '}
      </td>
    </>
  )
}

/** "4" or "4–9": the line numbers a skipped run covers on one side. */
function lineRange(start: number, count: number): string {
  return count === 1 ? String(start) : `${String(start)}–${String(start + count - 1)}`
}

/**
 * A run of unchanged lines the API left out (far from any change): one full-width row. The `⋯` is decorative; screen
 * readers hear the count and the line numbers on each side.
 */
function SkippedLines({ row, from, to }: { row: SkippedRow; from: string; to: string }) {
  const lines = row.count === 1 ? 'line' : 'lines'
  return (
    <tr data-change="skipped" className="border-b border-border bg-surface last:border-b-0">
      <td colSpan={4} className="px-2 py-1 text-center font-sans text-xs text-muted">
        <span aria-hidden="true" className="mr-1">
          ⋯
        </span>
        {plural(row.count, 'unchanged line')}{' '}
        <span className="sr-only">
          hidden ({from} {lines} {lineRange(row.oldLine, row.count)}, {to} {lines} {lineRange(row.newLine, row.count)})
        </span>
      </td>
    </tr>
  )
}

/**
 * FR-21: side-by-side diff between two versions, built from the API's `lines`. Each changed line carries a visible
 * `+`/`−` marker and a screen-reader label ("Added:", "Removed:"), so colour is only reinforcement. Unchanged lines
 * far from any change arrive as `skipped` runs ("⋯ N unchanged lines"); identical versions (a single skipped run)
 * say "No differences".
 */
export function DiffView({ diff }: { diff: ArtifactDiff }) {
  const rows = toSideBySide(diff.lines)
  const { added, removed } = diffStats(diff.lines)
  const from = `v${String(diff.from)}`
  const to = `v${String(diff.to)}`

  if (added === 0 && removed === 0) {
    return <p className="rounded-md border border-border p-3 text-sm">No differences between {from} and {to}.</p>
  }

  return (
    <div className="overflow-x-auto rounded-md border border-border">
      <table className="w-full min-w-[36rem] table-fixed border-collapse font-mono text-sm">
        <caption className="border-b border-border px-3 py-2 text-left font-sans text-sm">
          Changes from {from} to {to}: {plural(added, 'line')} added, {plural(removed, 'line')} removed
        </caption>
        <colgroup>
          <col className="w-10" />
          <col />
          <col className="w-10" />
          <col />
        </colgroup>
        <thead className="font-sans">
          <tr className="border-b border-border bg-surface text-left text-xs text-muted">
            <th scope="col" className="px-2 py-1">
              <span className="sr-only">{from} line number</span>
              <span aria-hidden="true">#</span>
            </th>
            <th scope="col" className="px-2 py-1">
              {from}
            </th>
            <th scope="col" className="border-l border-border px-2 py-1">
              <span className="sr-only">{to} line number</span>
              <span aria-hidden="true">#</span>
            </th>
            <th scope="col" className="px-2 py-1">
              {to}
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) =>
            row.kind === 'skipped' ? (
              <SkippedLines key={i} row={row} from={from} to={to} />
            ) : (
              <tr key={i} data-change={row.kind} className="border-b border-border last:border-b-0">
                <Cells cell={row.left} change={cellChange(row, 'left')} side="left" />
                <Cells cell={row.right} change={cellChange(row, 'right')} side="right" />
              </tr>
            ),
          )}
        </tbody>
      </table>
    </div>
  )
}
