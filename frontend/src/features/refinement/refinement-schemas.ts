import { z } from 'zod'

/** RefinementRules.MaxAnswerLength. */
export const REFINEMENT_ANSWER_MAX = 4000

/** FR-27: one answer field per clarifying question, in question order; blank means unanswered. */
export const answersSchema = z.object({
  answers: z.array(
    z.string().max(REFINEMENT_ANSWER_MAX, { error: `Answers are at most ${REFINEMENT_ANSWER_MAX.toLocaleString('en')} characters.` }),
  ),
})

export type AnswersValues = z.infer<typeof answersSchema>
