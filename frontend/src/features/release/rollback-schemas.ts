import { z } from 'zod'

/** RollbackRequest.reason maxLength (FR-44). */
export const ROLLBACK_REASON_MAX = 500

/** FR-44: the rollback form. `targetVersion` is the picker's value ('' until a version is chosen). */
export const rollbackSchema = z.object({
  targetVersion: z
    .string()
    .regex(/^\d+$/, { error: 'Choose the version to roll back to.' })
    .transform(Number)
    .pipe(z.number().int().min(1, { error: 'Choose the version to roll back to.' })),
  reason: z
    .string()
    .trim()
    .min(1, { error: 'Enter the reason for the rollback.' })
    .max(ROLLBACK_REASON_MAX, { error: `The reason is at most ${String(ROLLBACK_REASON_MAX)} characters.` }),
})

export type RollbackFormValues = z.input<typeof rollbackSchema>
export type RollbackValues = z.output<typeof rollbackSchema>
