import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react'

export interface TabItem<T extends string> {
  id: T
  label: string
}

interface TabsProps<T extends string> {
  label: string
  tabs: readonly TabItem<T>[]
  selected: T
  onSelect: (id: T) => void
  /** Content of the selected tab's panel. */
  children: ReactNode
}

/**
 * WAI-ARIA tabs with automatic activation: Left/Right (and Home/End) move between tabs, Tab moves into the panel.
 * Only the selected panel is rendered.
 */
export function Tabs<T extends string>({ label, tabs, selected, onSelect, children }: TabsProps<T>) {
  const base = useId()
  const refs = useRef(new Map<T, HTMLButtonElement>())
  const tabId = (id: T) => `${base}-tab-${id}`
  const panelId = `${base}-panel`

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const index = tabs.findIndex((t) => t.id === selected)
    let next: number | null = null
    if (event.key === 'ArrowRight') {
      next = (index + 1) % tabs.length
    } else if (event.key === 'ArrowLeft') {
      next = (index - 1 + tabs.length) % tabs.length
    } else if (event.key === 'Home') {
      next = 0
    } else if (event.key === 'End') {
      next = tabs.length - 1
    }
    const target = next === null ? undefined : tabs[next]
    if (target) {
      event.preventDefault()
      onSelect(target.id)
      refs.current.get(target.id)?.focus()
    }
  }

  return (
    <div>
      <div
        role="tablist"
        aria-label={label}
        onKeyDown={onKeyDown}
        className="mb-5 flex flex-wrap gap-1 border-b border-border"
      >
        {tabs.map((tab) => {
          const isSelected = tab.id === selected
          return (
            <button
              key={tab.id}
              ref={(el) => {
                if (el) {
                  refs.current.set(tab.id, el)
                } else {
                  refs.current.delete(tab.id)
                }
              }}
              type="button"
              role="tab"
              id={tabId(tab.id)}
              aria-selected={isSelected}
              aria-controls={panelId}
              tabIndex={isSelected ? 0 : -1}
              onClick={() => {
                onSelect(tab.id)
              }}
              className={
                '-mb-px min-h-10 rounded-t-md border-b-2 px-3 py-2 text-sm font-medium ' +
                (isSelected ? 'border-primary text-fg' : 'border-transparent text-muted hover:text-fg')
              }
            >
              {tab.label}
            </button>
          )
        })}
      </div>
      <div role="tabpanel" id={panelId} aria-labelledby={tabId(selected)} tabIndex={0} className="focus:outline-none">
        {children}
      </div>
    </div>
  )
}
