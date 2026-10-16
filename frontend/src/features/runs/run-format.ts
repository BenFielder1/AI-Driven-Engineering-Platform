import { errorMessages, type ErrorCode } from '../../lib/errors'
import type { AgentRun } from './runs-api'

/**
 * A failed run's `error` is stored as `CODE: message` (AgentRunner.FailAsync). Returns the user copy for a known code
 * (from lib/errors.ts) and the server's message; unknown shapes come back as the raw text.
 */
export function describeRunError(error: string): { title: string | null; message: string } {
  const match = /^([A-Z][A-Z_]*): ([\s\S]*)$/.exec(error)
  const code = match?.[1]
  if (match && code && Object.hasOwn(errorMessages, code)) {
    return { title: errorMessages[code as ErrorCode], message: match[2] ?? '' }
  }
  return { title: null, message: error }
}

/** Queued and running runs have no final counts yet (nothing streams in v1, SPEC §8). */
export const isUnfinished = (run: Pick<AgentRun, 'status'>) => run.status === 'queued' || run.status === 'running'

/** Prompt template versions are bare numbers in the template front matter: show them as "v1". */
export const formatPromptVersion = (version: string) => (/^\d+$/.test(version) ? `v${version}` : version)

/** FR-70, FR-71: the warnings recorded on a run, in words. */
export const runWarningLabels: Readonly<Record<string, string>> = {
  tools_unsupported:
    "The model doesn't support tool use, so the agent got the context in its prompt instead of reading the repository.",
  snapshot_unavailable: "The repository snapshot wasn't available, so the agent ran without the repository tools.",
  summary_wait_timeout:
    'The codebase summary wasn’t ready after 10 minutes, so the run continued without it.',
  summary_unavailable: 'There was no codebase summary for this version, so the run continued without it.',
}

export const runWarningText = (code: string) => runWarningLabels[code] ?? code
