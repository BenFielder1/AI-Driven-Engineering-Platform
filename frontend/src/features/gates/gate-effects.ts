import { phaseLabels } from '../projects/labels'
import type { Gate } from './gates-api'

/** What approving each non-escalation gate does (SPEC §4.2). */
export const approveEffect: Record<Exclude<Gate['type'], 'escalation'>, string> = {
  spec_approval: `The project moves to ${phaseLabels.planning}.`,
  plan_approval: `The project moves to ${phaseLabels.execution}.`,
  pre_release: `The project moves to ${phaseLabels.release} and the release starts.`,
}

/** What rejecting does (FR-13, §4.2: the producing agent re-runs with the comment as feedback). */
export const rejectEffect: Record<Exclude<Gate['type'], 'escalation'>, string> = {
  spec_approval: 'The phase stays the same and the Spec generator runs again with your comment as feedback.',
  plan_approval: 'The phase stays the same and the Plan generator runs again with your comment as feedback.',
  pre_release: 'The phase stays the same and the release doesn’t start.',
}
