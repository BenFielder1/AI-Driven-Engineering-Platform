import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { toast } from 'sonner'

import { Button } from '../../components/ui/button'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { useGateInbox } from '../api/gate-hooks'
import { usePipelines } from '../api/hooks'
import type { GateInboxItem, PipelineDefinition } from '../api/types'
import { GateDecision, type GateDecisionCopy } from './gate-decision'
import { gateTitle, GateInboxRow, type GateInboxLinks } from './gate-inbox-row'

export type { GateInboxLinks } from './gate-inbox-row'
export type { GateDecisionCopy } from './gate-decision'

export interface GateInboxProps {
  links: GateInboxLinks
  /** The host's copy for a gate's decision dialog (its name and what approving or rejecting does), if it has any. */
  decisionCopy?: (item: GateInboxItem) => GateDecisionCopy | undefined
  /** Shown on a stage gate the user may not decide (the host's permission rule, e.g. Owners only). */
  cannotDecideNote: string
  /** After a decision the API accepted (the host refreshes its own views of the project). */
  onDecided?: (item: GateInboxItem) => void
}

type Open = { item: GateInboxItem; action: 'approve' | 'reject'; definition: PipelineDefinition | undefined } | null

/**
 * FR-307 GateInbox: pending gates across the projects the user can see, newest first, with "Load more". Each row shows
 * the project, pipeline, gate title, gated artifact, age and overdue flag; stage gates are approved or rejected here by
 * those who may decide them, other gates link to their project. Labels come from the run's definition (FR-305).
 */
export function GateInbox({ links, decisionCopy, cannotDecideNote, onDecided }: GateInboxProps) {
  const [pipelineId, setPipelineId] = useState<string | null>(null)
  const [open, setOpen] = useState<Open>(null)
  // Bumped after each decision: once the refetch lands, focus moves (once) to the list, or to the empty state if the
  // decision emptied the inbox (NFR-A11Y: the decided row and its buttons are gone).
  const [decided, setDecided] = useState(0)
  const focusPending = useRef(false)
  const listRef = useRef<HTMLUListElement>(null)
  const emptyRef = useRef<HTMLDivElement>(null)
  const pipelines = usePipelines()
  const inbox = useGateInbox(pipelineId)

  const items = useMemo(() => inbox.data?.pages.flatMap((p) => p.items) ?? [], [inbox.data])
  const settled = !inbox.isFetching

  useEffect(() => {
    if (focusPending.current && settled) {
      focusPending.current = false
      ;(items.length === 0 ? emptyRef.current : listRef.current)?.focus()
    }
  }, [decided, settled, items.length])

  let body: ReactNode
  if (inbox.isPending) {
    body = <LoadingState label="Loading gates…" />
  } else if (inbox.isError) {
    body = <ErrorState error={inbox.error} onRetry={() => void inbox.refetch()} title="The gates couldn't load" />
  } else if (items.length === 0) {
    body = (
      <div ref={emptyRef} tabIndex={-1} className="focus:outline-none" data-testid="gate-inbox-empty">
        <EmptyState title="No gates waiting for you" />
      </div>
    )
  } else {
    body = (
      <>
        <ul ref={listRef} tabIndex={-1} aria-label="Pending gates" className="divide-y divide-border/60 focus:outline-none">
          {items.map((item) => (
            <GateInboxRow
              key={item.gate.id}
              item={item}
              pipelines={pipelines.data}
              links={links}
              cannotDecideNote={cannotDecideNote}
              onDecide={(action, definition) => {
                setOpen({ item, action, definition })
              }}
            />
          ))}
        </ul>
        {inbox.hasNextPage && (
          <Button
            variant="secondary"
            className="self-start"
            pending={inbox.isFetchingNextPage}
            pendingLabel="Loading…"
            onClick={() => void inbox.fetchNextPage()}
          >
            Load more
          </Button>
        )}
      </>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      {(pipelines.data?.length ?? 0) > 1 && (
        <SelectField
          label="Pipeline"
          className="max-w-xs"
          value={pipelineId ?? ''}
          onChange={(e) => {
            setPipelineId(e.target.value === '' ? null : e.target.value)
          }}
        >
          <option value="">All pipelines</option>
          {pipelines.data?.map((p) => (
            <option key={p.id} value={p.id}>
              {p.title}
            </option>
          ))}
        </SelectField>
      )}
      {body}
      {open && (
        <GateDecision
          gate={open.item.gate}
          action={open.action}
          gateLabel={gateTitle(open.item, open.definition)}
          copy={decisionCopy?.(open.item)}
          fallbackFocusRef={listRef}
          onClose={() => {
            setOpen(null)
          }}
          onDecided={(message) => {
            const item = open.item
            setOpen(null)
            focusPending.current = true
            setDecided((n) => n + 1)
            toast.success(message)
            onDecided?.(item)
          }}
        />
      )}
    </div>
  )
}
