import type { Rollback } from './release-api'

const plural = (n: number, one: string, many = `${one}s`) => `${String(n)} ${n === 1 ? one : many}`

/** What a rollback changed (§4.8), as short phrases: "2 artifacts invalidated", … */
export function rollbackEffects(r: Rollback): string[] {
  return [
    `${plural(r.invalidatedArtifactIds.length, 'artifact')} invalidated`,
    `${plural(r.resetStepIds.length, 'step')} reset`,
    `${plural(r.requeuedStepIds.length, 'step')} queued again`,
    `${plural(r.cancelledGateIds.length, 'gate')} cancelled`,
    `${plural(r.cancelledJobIds.length, 'job')} cancelled`,
  ]
}
