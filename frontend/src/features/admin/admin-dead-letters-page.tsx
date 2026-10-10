import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { Controller, useForm } from 'react-hook-form'
import { z } from 'zod'

import { Alert } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { SelectField } from '../../components/ui/select-field'
import { errorMessage } from '../../lib/errors'
import { useDocumentTitle } from '../../lib/use-document-title'
import { RelativeTime } from '../projects/relative-time'
import { ADMIN_PAGE_SIZE, useDeadLetters, useRetryDeadLetter, type DeadLetter, type DeadLettersQuery } from './admin-api'
import { deadLetterStatusLabels, jobTypeLabel, jobTypeLabels, jobTypes } from './admin-labels'
import type { DeadLettersSearch } from './admin-search'
import { AdminTable, bodyCell, FilterForm, PagedList, ProjectCell, ProjectSelect } from './admin-shared'

const filterSchema = z.object({
  projectId: z.string(),
  type: z.union([z.enum(jobTypes), z.literal('')]),
  includeRetried: z.boolean(),
})

type FilterValues = z.infer<typeof filterSchema>

function toFormValues(s: DeadLettersSearch): FilterValues {
  return { projectId: s.projectId ?? '', type: s.type ?? '', includeRetried: s.includeRetried === true }
}

function deadLettersQuery(s: DeadLettersSearch): DeadLettersQuery {
  return {
    page: s.page ?? 1,
    limit: ADMIN_PAGE_SIZE,
    ...(s.projectId ? { projectId: s.projectId } : {}),
    ...(s.type ? { type: s.type } : {}),
    ...(s.includeRetried ? { includeRetried: true } : {}),
  }
}

function DeadLetterRow({ item, onRetry }: { item: DeadLetter; onRetry: () => void }) {
  const label = jobTypeLabel(item.type)
  const deadAt = item.deadLetteredAt ?? item.createdAt
  return (
    <tr className="border-b border-border last:border-b-0">
      <th scope="row" className={`${bodyCell} text-left font-normal`}>
        <span className="font-medium">{label}</span>
        <code className="mt-0.5 block text-xs text-muted">{item.id}</code>
        <details className="mt-1">
          <summary className="cursor-pointer text-xs text-link underline underline-offset-4">Payload</summary>
          <pre className="mt-1 max-h-60 max-w-md overflow-auto whitespace-pre-wrap break-words rounded-md border border-border bg-canvas p-2 text-xs">
            {JSON.stringify(item.payload, null, 2)}
          </pre>
        </details>
      </th>
      <td className={bodyCell}>
        <ProjectCell id={item.projectId} name={item.projectName} />
      </td>
      <td className={bodyCell}>
        <Badge tone={item.status === 'dead' ? 'danger' : 'neutral'}>{deadLetterStatusLabels[item.status] ?? item.status}</Badge>
      </td>
      <td className={`${bodyCell} text-right tabular-nums`}>{item.attempts}</td>
      <td className={`${bodyCell} max-w-xs`}>
        {item.lastError ? (
          <p className="whitespace-pre-wrap break-words text-xs">{item.lastError}</p>
        ) : (
          <span className="text-muted">No error recorded</span>
        )}
      </td>
      <td className={bodyCell}>
        <RelativeTime iso={deadAt} />
      </td>
      <td className="py-2 align-top">
        {item.status === 'dead' ? (
          <Button variant="secondary" onClick={onRetry} aria-label={`Retry ${label} job ${item.id}`}>
            Retry
          </Button>
        ) : (
          <span className="text-xs text-muted">Already retried</span>
        )}
      </td>
    </tr>
  )
}

/**
 * /admin/dead-letters (FR-51): jobs that failed every attempt, with their last error, filtered by project and job type
 * (retried ones optionally included) and paged. Retry (confirmed) re-enqueues the original job.
 */
export function AdminDeadLettersPage({
  search,
  onSearchChange,
}: {
  search: DeadLettersSearch
  onSearchChange: (next: DeadLettersSearch) => void
}) {
  useDocumentTitle('Dead letters · Admin')
  const deadLetters = useDeadLetters(deadLettersQuery(search))
  const retry = useRetryDeadLetter()
  const [confirming, setConfirming] = useState<DeadLetter | null>(null)
  const [outcome, setOutcome] = useState<{ tone: 'success' | 'error'; text: string } | null>(null)
  const { register, control, handleSubmit, reset } = useForm<FilterValues>({
    resolver: zodResolver(filterSchema),
    values: toFormValues(search),
  })
  const filtered = Boolean(search.projectId ?? search.type ?? search.includeRetried)

  const clear = () => {
    reset(toFormValues({}))
    onSearchChange({})
  }

  const onSubmit = handleSubmit((v) => {
    onSearchChange({
      projectId: v.projectId || undefined,
      type: v.type || undefined,
      includeRetried: v.includeRetried ? true : undefined,
    })
  })

  const confirmRetry = () => {
    if (!confirming) {
      return
    }
    const label = jobTypeLabel(confirming.type)
    retry.mutate(confirming.id, {
      onSuccess: (job) => {
        setOutcome({
          tone: 'success',
          text: `The ${label.toLowerCase()} job was queued again as job ${job.id}. It runs as soon as a worker is free.`,
        })
      },
      onError: (error) => {
        setOutcome({ tone: 'error', text: errorMessage(error, 'retryDeadLetter') })
      },
      onSettled: () => {
        setConfirming(null)
      },
    })
  }

  return (
    <section aria-labelledby="admin-dead-letters-title">
      <h2 id="admin-dead-letters-title" className="mb-4 text-xl font-semibold">
        Dead letters
      </h2>
      <FilterForm label="Filter dead letters" onSubmit={(e) => void onSubmit(e)} onClear={clear}>
        <Controller
          control={control}
          name="projectId"
          render={({ field }) => <ProjectSelect id="dead-letters-project" value={field.value} onChange={field.onChange} />}
        />
        <SelectField id="dead-letters-type" label="Job type" {...register('type')}>
          <option value="">All job types</option>
          {jobTypes.map((t) => (
            <option key={t} value={t}>
              {jobTypeLabels[t]}
            </option>
          ))}
        </SelectField>
        <div className="flex items-end">
          <label className="flex min-h-10 items-center gap-2 text-sm font-medium">
            <input type="checkbox" className="h-4 w-4" {...register('includeRetried')} />
            Include dead letters already retried
          </label>
        </div>
      </FilterForm>
      <div role={outcome?.tone === 'error' ? 'alert' : 'status'} aria-live={outcome?.tone === 'error' ? 'assertive' : 'polite'}>
        {outcome && (
          <Alert tone={outcome.tone} title={outcome.tone === 'success' ? 'Job queued again' : 'The retry didn’t go through'} className="mb-4">
            <p>{outcome.text}</p>
          </Alert>
        )}
      </div>
      <PagedList
        query={deadLetters}
        noun="dead letter"
        loadingLabel="Loading dead letters…"
        errorTitle="The dead letters couldn't load"
        filtered={filtered}
        emptyTitle="No dead letters"
        emptyText="Every background job has either succeeded or is still being retried."
        onClearFilters={clear}
        onPage={(page) => {
          onSearchChange({ ...search, page: page > 1 ? page : undefined })
        }}
      >
        {(items) => (
          <AdminTable
            caption="Dead-lettered jobs, most recently dead-lettered first, with their last error."
            headers={['Job', 'Project', 'Status', { label: 'Attempts', align: 'right' }, 'Last error', 'Dead-lettered', 'Action']}
            minWidth="56rem"
          >
            {items.map((d) => (
              <DeadLetterRow
                key={d.id}
                item={d}
                onRetry={() => {
                  setOutcome(null)
                  setConfirming(d)
                }}
              />
            ))}
          </AdminTable>
        )}
      </PagedList>
      <ConfirmDialog
        open={confirming !== null}
        title="Retry this dead letter?"
        confirmLabel="Retry job"
        pendingLabel="Retrying…"
        pending={retry.isPending}
        onConfirm={confirmRetry}
        onCancel={() => {
          setConfirming(null)
        }}
      >
        {confirming && (
          <p>
            A new {jobTypeLabel(confirming.type).toLowerCase()} job
            {confirming.projectName ? ` for ${confirming.projectName}` : ''} is queued with the same payload, and this dead
            letter is marked as retried. If the cause of the failure hasn’t been fixed, it may fail again.
          </p>
        )}
      </ConfirmDialog>
    </section>
  )
}
