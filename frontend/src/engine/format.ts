/** Number, money, time and duration formatting for the engine views (the same rules as the SDLC pages). */

const numberFormat = new Intl.NumberFormat()
export const formatNumber = (n: number) => numberFormat.format(n)

const averageFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 })
/** Averages keep one decimal place ("1,234.5"). */
export const formatAverage = (n: number) => averageFormat.format(n)

const costFormat = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
})
export const formatCost = (usd: number) => costFormat.format(usd)

const percentFormat = new Intl.NumberFormat(undefined, { style: 'percent', maximumFractionDigits: 1 })
/** 0.25 → "25%". */
export const formatRate = (rate: number) => percentFormat.format(rate)

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
export const formatDateTime = (iso: string) => dateTimeFormat.format(new Date(iso))

/** "850 ms", "48.3 s", "3 min 05 s", "2 h 04 min". */
export function formatDuration(ms: number): string {
  if (ms < 1000) {
    return `${String(Math.round(ms))} ms`
  }
  const seconds = ms / 1000
  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`
  }
  const whole = Math.round(seconds)
  if (whole < 3600) {
    return `${String(Math.floor(whole / 60))} min ${String(whole % 60).padStart(2, '0')} s`
  }
  const minutes = Math.round(whole / 60)
  return `${String(Math.floor(minutes / 60))} h ${String(minutes % 60).padStart(2, '0')} min`
}

/** A key from the data (`some_key`, `some-key`, `some.key`) as words: "Some key". For fallbacks only. */
export function humanise(key: string): string {
  const words = key.replace(/[_.-]+/g, ' ').trim()
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : key
}

/** Prompt versions are bare numbers in the template front matter: show them as "v1". */
export const formatPromptVersion = (version: string) => (/^\d+$/.test(version) ? `v${version}` : version)

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
const relativeUnits: readonly [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 31_536_000],
  ['month', 2_592_000],
  ['week', 604_800],
  ['day', 86_400],
  ['hour', 3_600],
  ['minute', 60],
]

/** How long ago `iso` was: "3 hours ago", "yesterday", "just now". */
export function formatAge(iso: string, now: number = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000)
  for (const [unit, size] of relativeUnits) {
    if (Math.abs(seconds) >= size) {
      return relativeFormat.format(Math.trunc(seconds / size), unit)
    }
  }
  return 'just now'
}
