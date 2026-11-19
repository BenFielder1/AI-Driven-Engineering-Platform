import { useEffect, useRef, useState } from 'react'

const entries = (n: number) => `${String(n)} new timeline ${n === 1 ? 'entry' : 'entries'}`

/**
 * SPEC §7: a polite live-region message for timeline entries that arrive without the user asking (live updates
 * refetch the timeline). Entries the user loads with "Load more" or the replay stepper aren't announced: wrap that
 * fetch in `paging`. A page load that fails or brings nothing clears the flag, so the next live entry is announced.
 */
export function useNewEntriesAnnouncement(count: number, ready: boolean) {
  const [announcement, setAnnouncement] = useState('')
  const seen = useRef<number | null>(null)
  /** The count when a page load started, while it is in flight. */
  const pagingFrom = useRef<number | null>(null)

  useEffect(() => {
    if (!ready) {
      return
    }
    if (seen.current !== null && count > seen.current && pagingFrom.current === null) {
      setAnnouncement(entries(count - seen.current))
    }
    seen.current = count
    if (pagingFrom.current !== null && count > pagingFrom.current) {
      pagingFrom.current = null
    }
  }, [count, ready])

  const paging = (load: () => Promise<{ isError: boolean; data?: { pages: { items: unknown[] }[] } }>) => {
    const from = count
    pagingFrom.current = from
    void load().then((result) => {
      const loaded = result.data?.pages.reduce((n, page) => n + page.items.length, 0) ?? 0
      if (result.isError || loaded <= from) {
        pagingFrom.current = null
      }
    })
  }

  return { announcement, paging }
}
