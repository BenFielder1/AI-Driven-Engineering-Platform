import { z } from 'zod'

import { escalationDecisions } from '../projects/labels'

/** DecideGateRequest.comment maxLength. */
export const GATE_COMMENT_MAX = 4000

export const commentSchema = z.object({
  comment: z
    .string()
    .max(GATE_COMMENT_MAX, { error: `Comments are at most ${String(GATE_COMMENT_MAX)} characters.` }),
})

/** FR-14 / §4.8: the escalation decision. Rollback needs a checkpoint below the current one; the comment is optional. */
export function escalationSchema(currentCheckpoint: number) {
  return z
    .object({
      // Empty (or null, from React Hook Form's radios) until a decision is picked.
      decision: z.string().nullable().pipe(z.enum(escalationDecisions, { error: 'Choose a decision.' })),
      targetCheckpoint: z.string(),
      comment: z
        .string()
        .max(GATE_COMMENT_MAX, { error: `Comments are at most ${String(GATE_COMMENT_MAX)} characters.` }),
    })
    .superRefine((values, ctx) => {
      if (values.decision === 'rollback') {
        const raw = values.targetCheckpoint.trim()
        const n = Number(raw)
        let message: string | null = null
        if (currentCheckpoint <= 1) {
          message = 'There’s no earlier checkpoint to roll back to.'
        } else if (!raw) {
          message = 'Enter the checkpoint to roll back to.'
        } else if (!/^\d+$/.test(raw) || n < 1 || n >= currentCheckpoint) {
          message = `Enter a whole checkpoint number from 1 to ${String(currentCheckpoint - 1)}.`
        }
        if (message) {
          ctx.addIssue({ code: 'custom', path: ['targetCheckpoint'], message })
        }
      }
    })
}

