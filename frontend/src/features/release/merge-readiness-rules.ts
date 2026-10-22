import type { ProjectState } from '../projects/projects-api'

/** FR-84: readiness is checked in the review and release phases, until the version is released. */
export const mergeReadinessApplies = (state: Pick<ProjectState, 'phase' | 'status' | 'developmentVersion'>) =>
  (state.phase === 'review' || state.phase === 'release') && state.status !== 'released' && state.developmentVersion !== null
