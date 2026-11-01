import { zodResolver } from '@hookform/resolvers/zod'
import { Controller, useForm } from 'react-hook-form'
import { z } from 'zod'

import { Badge } from '../../components/ui/badge'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { useDocumentTitle } from '../../lib/use-document-title'
import { describeActivity, fr155EventTypes } from '../activity/activity-summary'
import { eventCategories, eventCategoryLabels } from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { ADMIN_PAGE_SIZE, useAdminEvents, type AdminEvent, type AdminEventsQuery } from './admin-api'
import { eventOutcomeLabels, eventOutcomes } from './admin-labels'
import { endOfDay, startOfDay, type AdminEventsSearch } from './admin-search'
import { AdminTable, bodyCell, FilterForm, PagedList, ProjectCell, ProjectSelect } from './admin-shared'

const filterSchema = z
  .object({
    projectId: z.string(),
    type: z
      .string()
      .trim()
      .max(100, { error: 'Event types are at most 100 characters.' })
      .refine((v) => v === '' || /^[A-Za-z][A-Za-z0-9_]*$/.test(v), {
        error: 'Enter an event type such as RUN_FAILED: letters, digits and underscores, starting with a letter.',
      }),
    category: z.union([z.enum(eventCategories), z.literal('')]),
    outcome: z.union([z.enum(eventOutcomes), z.literal('')]),
    from: z.string(),
    to: z.string(),
  })
  .refine((v) => !v.from || !v.to || v.from <= v.to, {
    path: ['to'],
    error: 'The end date must be on or after the start date.',
  })

type FilterValues = z.infer<typeof filterSchema>

function toFormValues(s: AdminEventsSearch): FilterValues {
  return {
    projectId: s.projectId ?? '',
    type: s.type ?? '',
    category: s.category ?? '',
    outcome: s.outcome ?? '',
    from: s.from ?? '',
    to: s.to ?? '',
  }
}

/** The API query for the current URL (dates are whole local days, inclusive). */
function eventsQuery(s: AdminEventsSearch): AdminEventsQuery {
  // The search schema already drops impossible dates; the helpers are defensive too, so a bad date is never sent.
  const from = s.from ? startOfDay(s.from) : undefined
  const to = s.to ? endOfDay(s.to) : undefined
  return {
    page: s.page ?? 1,
    limit: ADMIN_PAGE_SIZE,
    ...(s.projectId ? { projectId: s.projectId } : {}),
    ...(s.type ? { type: s.type } : {}),
    ...(s.category ? { category: s.category } : {}),
    ...(s.outcome ? { outcome: s.outcome } : {}),
    ...(from ? { from } : {}),
    ...(to ? { to } : {}),
  }
}

function EventRow({ event }: { event: AdminEvent }) {
  return (
    <tr className="border-b border-border/40 transition-colors last:border-b-0 hover:bg-muted/40">
      <th scope="row" className={`${bodyCell} text-left font-normal`}>
        <span className="font-medium">{fr155EventTypes.has(event.type) ? describeActivity(event) : event.summary}</span>
        <code className="mt-0.5 block text-xs text-muted-foreground">{event.type}</code>
        <details className="mt-1">
          <summary className="cursor-pointer text-xs text-primary underline underline-offset-4">Payload</summary>
          <pre className="mt-1 max-h-60 max-w-md overflow-auto whitespace-pre-wrap break-words rounded-lg border border-border/60 bg-background/60 p-2 font-mono text-xs">
            {JSON.stringify(event.payload, null, 2)}
          </pre>
        </details>
      </th>
      <td className={bodyCell}>
        <ProjectCell id={event.projectId} name={event.projectName} />
      </td>
      <td className={bodyCell}>{event.category ? eventCategoryLabels[event.category] : <span className="text-muted-foreground">Operations</span>}</td>
      <td className={bodyCell}>
        <Badge tone={event.outcome === 'failure' ? 'danger' : 'neutral'}>{eventOutcomeLabels[event.outcome]}</Badge>
      </td>
      <td className={bodyCell}>{event.actor.isSystem ? `${event.actor.name} (system)` : event.actor.name}</td>
      <td className="py-2 align-top">
        <RelativeTime iso={event.createdAt} />
      </td>
    </tr>
  )
}

/**
 * /admin/events (FR-51): the audit events of every project, newest first, filtered by project, type, category, outcome
 * and date range (all in the URL) and paged.
 */
export function AdminEventsPage({
  search,
  onSearchChange,
}: {
  search: AdminEventsSearch
  onSearchChange: (next: AdminEventsSearch) => void
}) {
  useDocumentTitle('Events · Admin')
  const events = useAdminEvents(eventsQuery(search))
  const { register, control, handleSubmit, reset, formState } = useForm<FilterValues>({
    resolver: zodResolver(filterSchema),
    values: toFormValues(search),
  })
  const errors = formState.errors
  const filtered = Boolean(search.projectId ?? search.type ?? search.category ?? search.outcome ?? search.from ?? search.to)

  const clear = () => {
    reset(toFormValues({}))
    onSearchChange({})
  }

  const onSubmit = handleSubmit((v) => {
    onSearchChange({
      projectId: v.projectId || undefined,
      type: v.type ? v.type.toUpperCase() : undefined,
      category: v.category || undefined,
      outcome: v.outcome || undefined,
      from: v.from || undefined,
      to: v.to || undefined,
    })
  })

  return (
    <section aria-labelledby="admin-events-title">
      <h2 id="admin-events-title" className="mb-4 text-xl">
        Events
      </h2>
      <FilterForm label="Filter events" onSubmit={(e) => void onSubmit(e)} onClear={clear}>
        <Controller
          control={control}
          name="projectId"
          render={({ field }) => <ProjectSelect id="events-project" value={field.value} onChange={field.onChange} />}
        />
        <TextField
          id="events-type"
          label="Event type"
          hint="Exact type, e.g. RUN_FAILED"
          autoComplete="off"
          spellCheck={false}
          error={errors.type?.message}
          {...register('type')}
        />
        <SelectField id="events-category" label="Category" {...register('category')}>
          <option value="">All categories</option>
          {eventCategories.map((c) => (
            <option key={c} value={c}>
              {eventCategoryLabels[c]}
            </option>
          ))}
        </SelectField>
        <SelectField id="events-outcome" label="Outcome" {...register('outcome')}>
          <option value="">Any outcome</option>
          {eventOutcomes.map((o) => (
            <option key={o} value={o}>
              {eventOutcomeLabels[o]}
            </option>
          ))}
        </SelectField>
        <TextField id="events-from" type="date" label="From (inclusive)" error={errors.from?.message} {...register('from')} />
        <TextField id="events-to" type="date" label="To (inclusive)" error={errors.to?.message} {...register('to')} />
      </FilterForm>
      <PagedList
        query={events}
        noun="event"
        loadingLabel="Loading events…"
        errorTitle="The events couldn't load"
        filtered={filtered}
        emptyTitle="No events yet"
        emptyText="Events appear here as projects move through their lifecycle."
        onClearFilters={clear}
        onPage={(page) => {
          onSearchChange({ ...search, page: page > 1 ? page : undefined })
        }}
      >
        {(items) => (
          <AdminTable
            caption="Events of every project, newest first. Each event's payload can be expanded."
            headers={['Event', 'Project', 'Category', 'Outcome', 'Actor', 'When']}
            minWidth="52rem"
          >
            {items.map((e) => (
              <EventRow key={e.id} event={e} />
            ))}
          </AdminTable>
        )}
      </PagedList>
    </section>
  )
}
