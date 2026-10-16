/**
 * FR-56: a stable key for a review cycle's report. Cycle numbers restart after a rollback, so the cycle alone can
 * repeat; trend points carry no report id, so a point and a report summary are matched on checkpoint, cycle and time.
 */
export const reportKey = (p: { checkpoint: number; cycle: number; createdAt: string }) =>
  `${String(p.checkpoint)}-${String(p.cycle)}-${p.createdAt}`
