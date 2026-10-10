import { z } from 'zod'

/** CreateArtifactDraftRequest.content maxLength. */
export const ARTIFACT_CONTENT_MAX = 1_000_000
/** DecideArtifactRequest.comment maxLength. */
export const ARTIFACT_COMMENT_MAX = 4000

/** FR-18: the edited Markdown of a new draft. */
export const draftSchema = z.object({
  content: z
    .string()
    .refine((v) => v.trim().length > 0, { error: 'Write some Markdown before saving.' })
    .refine((v) => v.length <= ARTIFACT_CONTENT_MAX, {
      error: `Artifacts are at most ${ARTIFACT_CONTENT_MAX.toLocaleString('en')} characters.`,
    }),
})

/** FR-17: the optional approve/reject comment. */
export const decisionSchema = z.object({
  comment: z
    .string()
    .max(ARTIFACT_COMMENT_MAX, { error: `Comments are at most ${String(ARTIFACT_COMMENT_MAX)} characters.` }),
})
