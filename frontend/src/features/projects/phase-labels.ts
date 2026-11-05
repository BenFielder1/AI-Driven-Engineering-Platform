import type { Schemas } from '../../api/client'

/**
 * User-facing phase names. A module of its own (re-exported by ./labels) so the marketing previews and PhaseBadge can
 * use them without bundling the rest of the projects copy into `/` (SPEC §8 first-load JS budget).
 */
export const phaseLabels: Record<Schemas['Phase'], string> = {
  analysis: 'Analysis',
  refinement: 'Refinement',
  specification: 'Specification',
  planning: 'Planning',
  execution: 'Execution',
  review: 'Review',
  release: 'Release',
}
