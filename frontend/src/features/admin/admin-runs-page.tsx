import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '@tanstack/react-router'
import { useRef } from 'react'
import { Controller, useForm } from 'react-hook-form'
import { z } from 'zod'

import { Drawer } from '../../components/ui/drawer'
import { SelectField } from '../../components/ui/select-field'
import { TextField } from '../../components/ui/text-field'
import { useDocumentTitle } from '../../lib/use-document-title'
import {
  agentKindLabels,
  agentKinds,
  formatCost,
  formatDateTime,
  formatDuration,
  formatNumber,
  llmProviderLabels,
  runStatusLabels,
} from '../projects/labels'
import { RelativeTime } from '../projects/relative-time'
import { RunStatusBadge } from '../runs/run-badges'
import { RunDetails } from '../runs/run-details'
import { isUnfinished } from '../runs/run-format'
import { ADMIN_PAGE_SIZE, useAdminRuns, type AdminRun, type AdminRunsQuery } from './admin-api'
import { llmProviders, runStatuses } from './admin-labels'
import type { AdminRunsSearch } from './admin-search'
import { AdminTable, bodyCell, FilterForm, PagedList, ProjectCell, ProjectSelect } from './admin-shared'

const filterSchema = z.object({
  projectId: z.string(),
  agent: z.union([z.enum(agentKinds), z.literal('')]),
  status: z.union([z.enum(runStatuses), z.literal('')]),
  provider: z.union([z.enum(llmProviders), z.literal('')]),
  model: z.string().trim().max(200, { error: 'Model names are at most 200 characters.' }),
})

type FilterValues = z.infer<typeof filterSchema>

function toFormValues(s: AdminRunsSearch): FilterValues {
  return {
    projectId: s.projectId ?? '',
    agent: s.agent ?? '',
    status: s.status ?? '',
    provider: s.provider ?? '',
    model: s.model ?? '',
  }
}

function runsQuery(s: AdminRunsSearch): AdminRunsQuery {
  return {
    page: s.page ?? 1,
    limit: ADMIN_PAGE_SIZE,
    ...(s.projectId ? { projectId: s.projectId } : {}),
    ...(s.agent ? { agent: s.agent } : {}),
    ...(s.status ? { status: s.status } : {}),
    ...(s.provider ? { provider: s.provider } : {}),
    ...(s.model ? { model: s.model } : {}),
  }
}

function RunRow({ run, onOpen }: { run: AdminRun; onOpen: () => void }) {
  const unfinished = isUnfinished(run)
  return (
    <tr className="border-b border-border last:border-b-0">
      <th scope="row" className={`${bodyCell} text-left font-medium`}>
        <button
          type="button"
          onClick={onOpen}
          aria-label={`${agentKindLabels[run.agent]} run in ${run.projectName}, started ${formatDateTime(run.createdAt)}`}
          className="min-h-8 text-left text-link underline underline-offset-4"
        >
          {agentKindLabels[run.agent]}
        </button>
      </th>
      <td className={bodyCell}>
        <ProjectCell id={run.projectId} name={run.projectName} />
      </td>
      <td className={bodyCell}>
        <code className="text-xs">{run.model}</code>
        <span className="block text-xs text-muted">{llmProviderLabels[run.provider]}</span>
      </td>
      <td className={`${bodyCell} text-right tabular-nums`}>
        {unfinished ? '—' : `${formatNumber(run.inputTokens)} / ${formatNumber(run.outputTokens)}`}
      </td>
      <td className={`${bodyCell} text-right tabular-nums`}>{unfinished ? '—' : formatCost(run.costEstimate)}</td>
      <td className={`${bodyCell} text-right tabular-nums`}>{unfinished ? 'Running…' : formatDuration(run.durationMs)}</td>
      <td className={bodyCell}>
        <RunStatusBadge status={run.status} />
      </td>
      <td className="py-2 align-top">
        <RelativeTime iso={run.createdAt} />
      </td>
    </tr>
  )
}

/**
 * /admin/runs (FR-51, FR-23): agent runs of every project, newest first, filtered by project, agent, status, provider
 * and model (in the URL) and paged. A row opens the run's details (inputs, error, output) in a drawer.
 */
export function AdminRunsPage({
  search,
  onSearchChange,
  onOpenRun,
  onCloseRun,
}: {
  search: AdminRunsSearch
  onSearchChange: (next: AdminRunsSearch) => void
  onOpenRun: (runId: string) => void
  onCloseRun: () => void
}) {
  useDocumentTitle('Agent runs · Admin')
  const runs = useAdminRuns(runsQuery(search))
  const headingRef = useRef<HTMLHeadingElement>(null)
  const { register, control, handleSubmit, reset, formState } = useForm<FilterValues>({
    resolver: zodResolver(filterSchema),
    values: toFormValues(search),
  })
  const filtered = Boolean(search.projectId ?? search.agent ?? search.status ?? search.provider ?? search.model)
  const openRun = search.run ? runs.data?.items.find((r) => r.id === search.run) : undefined

  const clear = () => {
    reset(toFormValues({}))
    onSearchChange({})
  }

  const onSubmit = handleSubmit((v) => {
    onSearchChange({
      projectId: v.projectId || undefined,
      agent: v.agent || undefined,
      status: v.status || undefined,
      provider: v.provider || undefined,
      model: v.model || undefined,
    })
  })

  return (
    <section aria-labelledby="admin-runs-title">
      <h2 id="admin-runs-title" ref={headingRef} tabIndex={-1} className="mb-4 text-xl font-semibold">
        Agent runs
      </h2>
      <FilterForm label="Filter agent runs" onSubmit={(e) => void onSubmit(e)} onClear={clear}>
        <Controller
          control={control}
          name="projectId"
          render={({ field }) => <ProjectSelect id="runs-project" value={field.value} onChange={field.onChange} />}
        />
        <SelectField id="runs-agent" label="Agent" {...register('agent')}>
          <option value="">All agents</option>
          {agentKinds.map((k) => (
            <option key={k} value={k}>
              {agentKindLabels[k]}
            </option>
          ))}
        </SelectField>
        <SelectField id="runs-status" label="Status" {...register('status')}>
          <option value="">Any status</option>
          {runStatuses.map((s) => (
            <option key={s} value={s}>
              {runStatusLabels[s].replace('…', '')}
            </option>
          ))}
        </SelectField>
        <SelectField id="runs-provider" label="Provider" {...register('provider')}>
          <option value="">All providers</option>
          {llmProviders.map((p) => (
            <option key={p} value={p}>
              {llmProviderLabels[p]}
            </option>
          ))}
        </SelectField>
        <TextField
          id="runs-model"
          label="Model"
          hint="Exact model name"
          autoComplete="off"
          spellCheck={false}
          error={formState.errors.model?.message}
          {...register('model')}
        />
      </FilterForm>
      <PagedList
        query={runs}
        noun="run"
        loadingLabel="Loading runs…"
        errorTitle="The runs couldn't load"
        filtered={filtered}
        emptyTitle="No agent runs yet"
        emptyText="Runs appear here when an agent works on any project."
        onClearFilters={clear}
        onPage={(page) => {
          onSearchChange({ ...search, page: page > 1 ? page : undefined, run: undefined })
        }}
      >
        {(items) => (
          <AdminTable
            caption="Agent runs of every project, newest first. Open a run to see all its details."
            headers={['Agent', 'Project', 'Model', { label: 'Tokens in / out', align: 'right' }, { label: 'Est. cost', align: 'right' }, { label: 'Duration', align: 'right' }, 'Status', 'Started']}
            minWidth="52rem"
          >
            {items.map((r) => (
              <RunRow
                key={r.id}
                run={r}
                onOpen={() => {
                  onOpenRun(r.id)
                }}
              />
            ))}
          </AdminTable>
        )}
      </PagedList>
      <Drawer
        open={openRun !== undefined}
        title={openRun ? `${agentKindLabels[openRun.agent]} run · ${openRun.projectName}` : 'Agent run'}
        fallbackFocusRef={headingRef}
        onClose={onCloseRun}
      >
        {openRun && (
          <>
            <p className="mb-3 text-sm">
              <Link
                to="/projects/$projectId/runs"
                params={{ projectId: openRun.projectId }}
                search={{ run: openRun.id }}
                className="text-link underline underline-offset-4"
              >
                Open in the project’s runs
              </Link>
            </p>
            <RunDetails projectId={openRun.projectId} runId={openRun.id} summary={undefined} />
          </>
        )}
      </Drawer>
    </section>
  )
}
