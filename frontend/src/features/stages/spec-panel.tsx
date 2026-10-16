import { useState } from 'react'

import {
  markdownOutline,
  requirementChangeEntries,
  requirementChanges,
  type RequirementChange,
} from '../artifacts/markdown-outline'

const changeLabels: Record<RequirementChange, string> = {
  new: 'New',
  changed: 'Changed',
  removed: 'Removed',
  unchanged: 'Unchanged',
}

type Filter = RequirementChange | 'all'

/** FR-55, §4.13: the requirements this version tags new / changed / removed (/ unchanged), filterable. */
function ChangesInVersion({ content }: { content: string }) {
  const entries = requirementChangeEntries(markdownOutline(content))
  const [filter, setFilter] = useState<Filter>('all')
  if (entries.length === 0) {
    return null
  }
  const counts = (c: RequirementChange) => entries.filter((e) => e.change === c).length
  const shown = filter === 'all' ? entries.filter((e) => e.change !== 'unchanged') : entries.filter((e) => e.change === filter)
  const options: { key: Filter; label: string }[] = [
    { key: 'all', label: `All changes (${String(entries.length - counts('unchanged'))})` },
    ...requirementChanges.map((c) => ({ key: c, label: `${changeLabels[c]} (${String(counts(c))})` })),
  ]
  return (
    <section aria-labelledby="spec-changes-title" className="flex flex-col gap-2 rounded-lg border border-border/60 p-3">
      <h4 id="spec-changes-title" className="text-sm font-semibold">
        Changes in this version
      </h4>
      <div role="group" aria-label="Filter requirements by change" className="flex flex-wrap gap-2">
        {options.map((o) => (
          <button
            key={o.key}
            type="button"
            aria-pressed={filter === o.key}
            onClick={() => {
              setFilter(o.key)
            }}
            className={`min-h-9 rounded-full border px-3 text-xs ${filter === o.key ? 'border-primary bg-primary/15 font-medium' : 'border-border'}`}
          >
            {o.label}
          </button>
        ))}
      </div>
      {shown.length === 0 ? (
        <p className="text-sm text-muted-foreground">No requirements match this filter.</p>
      ) : (
        <ul aria-label="Requirements in this filter" className="flex flex-col gap-1 text-sm">
          {shown.map((e) => (
            <li key={e.id} className="flex flex-wrap items-baseline gap-2">
              <span className="rounded-full border border-border px-2 text-xs">{changeLabels[e.change]}</span>
              <a href={`#${e.id}`} className="text-primary underline">
                {e.title}
              </a>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

/** FR-55 Specification panel: a section outline navigator (from the headings) and, for iterations, the changes filter. */
export function SpecPanel({ content }: { content: string }) {
  const outline = markdownOutline(content).filter((e) => e.level <= 3)
  return (
    <div className="grid gap-3 md:grid-cols-2">
      {outline.length > 0 && (
        <nav aria-label="Section outline" className="rounded-lg border border-border/60 p-3">
          <h4 className="mb-2 text-sm font-semibold">Sections</h4>
          <ol className="flex max-h-72 flex-col gap-1 overflow-y-auto text-sm">
            {outline.map((e) => (
              <li key={e.id} style={{ paddingLeft: `${String((e.level - 1) * 0.75)}rem` }}>
                <a href={`#${e.id}`} className="text-primary underline">
                  {e.text}
                </a>
              </li>
            ))}
          </ol>
        </nav>
      )}
      <ChangesInVersion content={content} />
    </div>
  )
}
