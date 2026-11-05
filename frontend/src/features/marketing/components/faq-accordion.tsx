import { ChevronDown } from 'lucide-react'
import { useId, useRef, useState, type KeyboardEvent } from 'react'

import { cn } from '../../../lib/utils'
import type { FaqItem } from '../content/faq'
import { SiteLink } from './site-link'

/**
 * FR-218: an accessible accordion (WAI-ARIA APG pattern). Each question is a heading wrapping a button with
 * `aria-expanded` and `aria-controls`; the answer is a `region` labelled by its button and `hidden` while closed.
 * Enter/Space toggle (native buttons); ArrowDown/ArrowUp move between questions, Home/End jump to the first/last.
 */
export function FaqAccordion({
  items,
  headingLevel = 'h2',
  anchored = false,
}: {
  items: readonly FaqItem[]
  headingLevel?: 'h2' | 'h3'
  /** Give each question its id as an anchor (`/faq#free`). */
  anchored?: boolean
}) {
  const baseId = useId()
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set())
  const buttons = useRef<(HTMLButtonElement | null)[]>([])
  const Heading = headingLevel

  function toggle(id: string) {
    setOpen((current) => {
      const next = new Set(current)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const last = items.length - 1
    const target =
      event.key === 'ArrowDown'
        ? (index + 1) % items.length
        : event.key === 'ArrowUp'
          ? (index - 1 + items.length) % items.length
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? last
              : null
    if (target !== null) {
      event.preventDefault()
      buttons.current[target]?.focus()
    }
  }

  return (
    <div className="divide-y divide-border rounded-xl border border-border bg-card" data-testid="faq-accordion">
      {items.map((item, index) => {
        const isOpen = open.has(item.id)
        const buttonId = `${baseId}-${item.id}-button`
        const panelId = `${baseId}-${item.id}-panel`
        return (
          <div key={item.id} id={anchored ? item.id : undefined} className="scroll-mt-20">
            <Heading className="text-base font-medium">
              <button
                ref={(el) => {
                  buttons.current[index] = el
                }}
                id={buttonId}
                type="button"
                aria-expanded={isOpen}
                aria-controls={panelId}
                onClick={() => {
                  toggle(item.id)
                }}
                onKeyDown={(event) => {
                  onKeyDown(event, index)
                }}
                className="flex min-h-12 w-full items-center justify-between gap-4 px-5 py-3 text-left hover:bg-accent/60"
              >
                {item.question}
                <ChevronDown
                  aria-hidden="true"
                  className={cn('size-4 shrink-0 text-muted-foreground transition-transform', isOpen && 'rotate-180')}
                />
              </button>
            </Heading>
            <div id={panelId} role="region" aria-labelledby={buttonId} hidden={!isOpen} className="px-5 pb-5">
              {item.answer.map((paragraph) => (
                <p key={paragraph} className="mt-2 text-sm text-muted-foreground first:mt-0">
                  {paragraph}
                </p>
              ))}
              {item.link && (
                <p className="mt-3 text-sm">
                  <SiteLink link={item.link} className="inline-flex min-h-6 items-center font-medium text-primary underline-offset-4 hover:underline" />
                </p>
              )}
            </div>
          </div>
        )
      })}
    </div>
  )
}
