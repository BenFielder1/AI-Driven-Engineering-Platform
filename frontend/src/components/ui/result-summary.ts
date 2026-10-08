/** "Showing 21–40 of 57 runs". */
export function resultSummary(page: number, limit: number, count: number, total: number, noun: string): string {
  const from = (page - 1) * limit + 1
  const to = from + count - 1
  return `Showing ${String(from)}–${String(to)} of ${String(total)} ${noun}${total === 1 ? '' : 's'}`
}
