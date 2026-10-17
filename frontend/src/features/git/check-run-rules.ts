import type { Schemas } from '../../api/client'

/** A check run or commit status (FR-84 `CheckRunDto`, or FR-85 `ExternalCheckDto`, which has no `status`). */
export interface CheckRunLike {
  name: string
  status?: Schemas['CheckRunStatus']
  conclusion: Schemas['CheckRunConclusion']
  required: boolean
  detailsUrl: string | null
}

/** Failed, timed out or needing action: what blocks a merge or costs review points when the check is required. */
export const isFailingCheck = (c: CheckRunLike) =>
  c.conclusion === 'failure' || c.conclusion === 'timed_out' || c.conclusion === 'action_required'
