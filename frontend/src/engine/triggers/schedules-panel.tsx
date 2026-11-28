import { zodResolver } from '@hookform/resolvers/zod'
import { useEffect, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import { ErrorSummary } from '../../components/ui/alert'
import { Badge } from '../../components/ui/badge'
import { Button } from '../../components/ui/button'
import { ConfirmDialog } from '../../components/ui/confirm-dialog'
import { fieldErrorSummary } from '../../components/ui/form-errors'
import { SelectField } from '../../components/ui/select-field'
import { EmptyState, ErrorState, LoadingState } from '../../components/ui/states'
import { TextField } from '../../components/ui/text-field'
import { errorMessage } from '../../lib/errors'
import {
  useCreatePipelineSchedule,
  useDeletePipelineSchedule,
  usePipelineSchedules,
  useSchedulePreview,
  useSetSchedulePaused,
  type PipelineSchedule,
} from '../api/trigger-hooks'
import type { PipelineDefinition } from '../api/types'
import { browserTimeZone, cronShape, formatInZone } from './trigger-format'

const scheduleSchema = z.object({
  pipelineId: z.string().min(1, { error: 'Choose a pipeline.' }),
  cron: z.string().trim().regex(cronShape, { error: 'Enter five fields: minute hour day-of-month month day-of-week.' }),
  timezone: z.string().trim().min(1, { error: 'Enter a timezone, e.g. Europe/London.' }),
})
type ScheduleValues = z.infer<typeof scheduleSchema>

/** Waits until typing pauses before previewing (one request per settled cron and timezone). */
function useSettled<T>(value: T, ms = 300): T {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => {
      setSettled(value)
    }, ms)
    return () => {
      clearTimeout(timer)
    }
  }, [value, ms])
  return settled
}

function FireTimes({ times, timezone, label }: { times: readonly string[]; timezone: string; label: string }) {
  return (
    <ol aria-label={label} className="flex flex-col gap-0.5 text-sm tabular-nums">
      {times.map((t) => (
        <li key={t}>
          <time dateTime={t}>{formatInZone(t, timezone)}</time>
        </li>
      ))}
    </ol>
  )
}

/** FR-366: the next 3 fire times of the cron and timezone being typed, before saving. */
function SchedulePreview({ projectId, cron, timezone }: { projectId: string; cron: string; timezone: string }) {
  const settled = useSettled(`${cron.trim()}\n${timezone.trim()}`)
  const [c = '', tz = ''] = settled.split('\n')
  const ready = cronShape.test(c) && tz !== ''
  const preview = useSchedulePreview(projectId, c, tz, ready)
  let body
  if (!ready) {
    body = <p className="text-sm text-muted-foreground">Enter a cron expression and a timezone to see when it fires.</p>
  } else if (preview.isError) {
    body = (
      <p className="text-sm text-destructive" role="alert">
        {errorMessage(preview.error, 'pipelineTriggers')}
      </p>
    )
  } else if (!preview.data) {
    body = <LoadingState label="Working out the fire times…" />
  } else {
    body = <FireTimes times={preview.data.nextFireTimes} timezone={preview.data.timezone} label="Next fire times" />
  }
  return (
    <div className="rounded-lg border border-border/60 p-3" aria-live="polite">
      <p className="mb-1 text-sm font-medium">Next 3 fire times ({tz || 'no timezone'})</p>
      {body}
    </div>
  )
}

function CreateSchedule({ projectId, pipelines }: { projectId: string; pipelines: readonly PipelineDefinition[] }) {
  const create = useCreatePipelineSchedule(projectId)
  const [serverError, setServerError] = useState<string | null>(null)
  const form = useForm<ScheduleValues>({
    resolver: zodResolver(scheduleSchema),
    defaultValues: { pipelineId: pipelines[0]?.pipelineId ?? '', cron: '', timezone: browserTimeZone() },
  })
  const [cron, timezone] = useWatch({ control: form.control, name: ['cron', 'timezone'] })
  const { errors, isSubmitted } = form.formState
  const ids = { pipelineId: 'schedule-pipeline', cron: 'schedule-cron', timezone: 'schedule-timezone' }

  const onSubmit = form.handleSubmit((values) => {
    setServerError(null)
    create.mutate(
      { pipelineId: values.pipelineId, cron: values.cron.trim(), timezone: values.timezone.trim() },
      {
        onSuccess: () => {
          form.reset({ pipelineId: values.pipelineId, cron: '', timezone: values.timezone })
          toast.success('Schedule added.')
        },
        onError: (error) => {
          setServerError(errorMessage(error, 'pipelineTriggers'))
        },
      },
    )
  })

  return (
    <form noValidate onSubmit={(e) => void onSubmit(e)} className="flex flex-col gap-3" aria-labelledby="schedule-create-title">
      <h5 id="schedule-create-title" className="text-sm font-semibold">
        Add a schedule
      </h5>
      <ErrorSummary
        title="We couldn't add the schedule"
        errors={[...(serverError ? [{ message: serverError }] : []), ...(isSubmitted ? fieldErrorSummary(errors, ids) : [])]}
      />
      <SelectField id={ids.pipelineId} label="Pipeline" error={errors.pipelineId?.message} {...form.register('pipelineId')}>
        {pipelines.map((p) => (
          <option key={p.pipelineId} value={p.pipelineId}>
            {p.title}
          </option>
        ))}
      </SelectField>
      <div className="grid gap-3 sm:grid-cols-2">
        <TextField
          id={ids.cron}
          label="Cron expression"
          hint="Minute hour day-of-month month day-of-week, e.g. 0 7 * * 1 for Mondays at 07:00."
          autoComplete="off"
          spellCheck={false}
          error={errors.cron?.message}
          {...form.register('cron')}
        />
        <TextField
          id={ids.timezone}
          label="Timezone"
          hint="An IANA timezone, e.g. Europe/London. Fire times follow its daylight saving."
          autoComplete="off"
          spellCheck={false}
          error={errors.timezone?.message}
          {...form.register('timezone')}
        />
      </div>
      <SchedulePreview projectId={projectId} cron={cron} timezone={timezone} />
      <div>
        <Button type="submit" pending={create.isPending} pendingLabel="Adding…">
          Add schedule
        </Button>
      </div>
    </form>
  )
}

function ScheduleRow({
  schedule,
  title,
  onDelete,
  onTogglePaused,
  toggling,
}: {
  schedule: PipelineSchedule
  title: string
  onDelete: () => void
  onTogglePaused: () => void
  toggling: boolean
}) {
  const times = schedule.nextFireTimes ?? [schedule.nextFireAt]
  return (
    <li className="flex flex-col gap-2 border-b border-border py-3 last:border-b-0 sm:flex-row sm:items-start sm:justify-between">
      <div className="flex min-w-0 flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{title}</span>
          <Badge tone={schedule.paused ? 'warning' : 'success'}>{schedule.paused ? 'Paused' : 'Active'}</Badge>
        </div>
        <p className="text-sm">
          <code className="font-mono">{schedule.cron}</code> in {schedule.timezone}
        </p>
        <p className="text-xs text-muted-foreground">{schedule.paused ? 'Would fire next, once resumed:' : 'Fires next:'}</p>
        <FireTimes times={times} timezone={schedule.timezone} label={`Next fire times of ${title} (${schedule.cron})`} />
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" onClick={onTogglePaused} pending={toggling} pendingLabel={schedule.paused ? 'Resuming…' : 'Pausing…'}>
          {schedule.paused ? 'Resume' : 'Pause'}
        </Button>
        <Button variant="ghost" onClick={onDelete}>
          Delete
        </Button>
      </div>
    </li>
  )
}

/**
 * FR-366 schedules: the project's schedules with their next 3 fire times in their own timezone, pause, resume and
 * delete, and a form for a pipeline that declares a schedule trigger, previewing its fire times before saving (Owner).
 */
export function SchedulesPanel({
  projectId,
  pipelines,
  titles,
}: {
  projectId: string
  /** The engine pipelines whose definitions declare `trigger: { kind: schedule }`. */
  pipelines: readonly PipelineDefinition[]
  /** Pipeline titles by id, for listing. */
  titles: ReadonlyMap<string, string>
}) {
  const schedules = usePipelineSchedules(projectId)
  const setPaused = useSetSchedulePaused(projectId)
  const remove = useDeletePipelineSchedule(projectId)
  const [deleting, setDeleting] = useState<PipelineSchedule | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const onError = (error: unknown) => {
    setActionError(errorMessage(error, 'pipelineTriggers'))
  }

  let list
  if (schedules.isPending) {
    list = <LoadingState label="Loading schedules…" />
  } else if (schedules.isError) {
    list = <ErrorState error={schedules.error} onRetry={() => void schedules.refetch()} title="The schedules couldn't load" />
  } else if (schedules.data.length === 0) {
    list = <EmptyState title="No schedules">A schedule starts a run at set times.</EmptyState>
  } else {
    list = (
      <ul aria-label="Schedules">
        {schedules.data.map((s) => (
          <ScheduleRow
            key={s.id}
            schedule={s}
            title={titles.get(s.pipelineId) ?? s.pipelineId}
            toggling={setPaused.isPending && setPaused.variables.scheduleId === s.id}
            onTogglePaused={() => {
              setActionError(null)
              setPaused.mutate(
                { scheduleId: s.id, paused: !s.paused },
                {
                  onSuccess: () => toast.success(s.paused ? 'Schedule resumed.' : 'Schedule paused.'),
                  onError,
                },
              )
            }}
            onDelete={() => {
              setActionError(null)
              setDeleting(s)
            }}
          />
        ))}
      </ul>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      {actionError && (
        <p role="alert" className="text-sm text-destructive">
          {actionError}
        </p>
      )}
      {list}
      {pipelines.length > 0 ? (
        <CreateSchedule projectId={projectId} pipelines={pipelines} />
      ) : (
        <p className="text-sm text-muted-foreground">No pipeline here declares a schedule trigger.</p>
      )}
      <ConfirmDialog
        open={deleting !== null}
        title="Delete this schedule?"
        confirmLabel="Delete"
        pendingLabel="Deleting…"
        pending={remove.isPending}
        destructive
        onCancel={() => {
          setDeleting(null)
        }}
        onConfirm={() => {
          if (deleting) {
            remove.mutate(deleting.id, {
              onSuccess: () => toast.success('Schedule deleted.'),
              onError,
              onSettled: () => {
                setDeleting(null)
              },
            })
          }
        }}
      >
        <p>It stops starting runs. Runs it already started carry on.</p>
      </ConfirmDialog>
    </div>
  )
}
