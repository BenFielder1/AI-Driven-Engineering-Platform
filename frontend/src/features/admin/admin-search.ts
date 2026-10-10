import { z } from 'zod'

import { agentKinds, eventCategories } from '../projects/labels'
import { eventOutcomes, jobTypes, llmProviders, runStatuses } from './admin-labels'

/**
 * URL search params of the admin pages (FR-51, FR-3), parsed with Zod. Every field drops to `undefined` when it is
 * missing or invalid (a hand-edited URL never breaks the page), and defaults are left out of the URL.
 */

/** Stored event types, e.g. `RUN_FAILED` (mirrors the API's `type` rule). */
export const eventTypePattern = /^[A-Z][A-Z0-9_]*$/
/** `YYYY-MM-DD` from an `<input type="date">`. */
const datePattern = /^(\d{4})-(\d{2})-(\d{2})$/

/**
 * True when `value` is `YYYY-MM-DD` and names a real calendar day (`2026-13-45` and `2026-02-30` are rejected).
 * Round-trips the parts through a UTC date, so month/day overflow is caught instead of silently rolled over.
 */
export function isCalendarDate(value: string): boolean {
  const match = datePattern.exec(value)
  if (!match) {
    return false
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])]
  const d = new Date(Date.UTC(year, month - 1, day))
  d.setUTCFullYear(year) // Date.UTC maps years 0–99 to 1900–1999.
  return d.getUTCFullYear() === year && d.getUTCMonth() === month - 1 && d.getUTCDate() === day
}

const calendarDate = z.string().refine(isCalendarDate)

const optional = <T extends z.ZodType>(schema: T) => schema.optional().catch(undefined)

const page = optional(z.coerce.number().int().min(2))
const projectId = optional(z.uuid())
const text = (max: number) =>
  optional(
    z
      .string()
      .trim()
      .max(max)
      .transform((v) => (v === '' ? undefined : v)),
  )
const flag = optional(z.union([z.literal(true), z.literal('true')]).transform(() => true as const))

export const adminEventsSearchSchema = z
  .object({
    projectId,
    type: optional(z.string().max(100).regex(eventTypePattern)),
    category: optional(z.enum(eventCategories)),
    outcome: optional(z.enum(eventOutcomes)),
    from: optional(calendarDate),
    to: optional(calendarDate),
    page,
  })
  // An inverted range (only possible in a hand-edited URL) is dropped rather than sent (the API answers 400).
  .transform((s) => (s.from && s.to && s.from > s.to ? { ...s, from: undefined, to: undefined } : s))
export type AdminEventsSearch = z.output<typeof adminEventsSearchSchema>

export const adminRunsSearchSchema = z.object({
  projectId,
  agent: optional(z.enum(agentKinds)),
  status: optional(z.enum(runStatuses)),
  provider: optional(z.enum(llmProviders)),
  model: text(200),
  page,
  /** Opens the detail drawer for a run on the current page. */
  run: optional(z.uuid()),
})
export type AdminRunsSearch = z.output<typeof adminRunsSearchSchema>

export const deadLettersSearchSchema = z.object({
  projectId,
  type: optional(z.enum(jobTypes)),
  includeRetried: flag,
  page,
})
export type DeadLettersSearch = z.output<typeof deadLettersSearchSchema>

export const adminUsersSearchSchema = z.object({
  search: text(200),
  page,
})
export type AdminUsersSearch = z.output<typeof adminUsersSearchSchema>

/** Converts a local date-time to an ISO instant, or `undefined` when the date is not a real calendar day. */
function toInstant(date: string, time: string): string | undefined {
  if (!isCalendarDate(date)) {
    return undefined
  }
  const instant = new Date(`${date}T${time}`)
  return Number.isNaN(instant.getTime()) ? undefined : instant.toISOString()
}

/** Start of a local calendar day as an instant (`from` is inclusive); `undefined` for an invalid date. */
export function startOfDay(date: string): string | undefined {
  return toInstant(date, '00:00:00')
}

/** End of a local calendar day as an instant (`to` is inclusive); `undefined` for an invalid date. */
export function endOfDay(date: string): string | undefined {
  return toInstant(date, '23:59:59.999')
}
