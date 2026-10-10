import { formatDateTime, formatRelativeTime } from './labels'

/** "3 hours ago", with the absolute date and time as the title and the ISO value for machines. */
export function RelativeTime({ iso }: { iso: string }) {
  return (
    <time dateTime={iso} title={formatDateTime(iso)}>
      {formatRelativeTime(iso)}
    </time>
  )
}
