import { isAgentAuthored } from './artifact-author'
import type { ArtifactSummary } from './artifacts-api'

/**
 * FR-20: whether `version` is the current version of its type — the open draft or the highest locked version — which
 * is the only case where the server requests regeneration of a corrupted agent-authored version.
 *
 * A corrupted row's status is already `corrupted`, so its status before the mark is unknown here. The best reading of
 * the history: it is current when every newer version is a draft (an open draft sits above the highest locked
 * version); any newer locked, rejected, superseded or invalidated version means it was replaced. A corrupted latest
 * version that had been rejected or invalidated reads as current (the API doesn't expose the previous status).
 */
export function isCurrentVersion(version: number, history: readonly Pick<ArtifactSummary, 'version' | 'status'>[]): boolean {
  return history.every((a) => a.version <= version || a.status === 'draft')
}

export interface IntegrityCopy {
  title: string
  body: string
}

/**
 * FR-20: what to say about a version that failed its integrity check. "Regeneration was requested" only when the
 * server actually enqueued it (agent-authored and current); a current human edit has no agent, so the user replaces it.
 */
export function corruptedCopy(artifact: Pick<ArtifactSummary, 'author'>, current: boolean): IntegrityCopy {
  const title = 'This version failed its integrity check'
  const what =
    "Its content no longer matches the stored SHA-256 hash, so it may have been changed outside AIDE. Don't rely on it. An integrity alert was raised"
  if (current && isAgentAuthored(artifact.author)) {
    return { title, body: `${what} and regeneration was requested; the new version appears here when it's ready.` }
  }
  if (current) {
    return { title, body: `${what}. Edit and save a new draft to replace it.` }
  }
  return { title, body: `${what}.` }
}
