import type { ProjectStatus } from '../projects/projects-api'
import type { ReleaseVersion } from './versions-api'

/** StartReleaseVersionRequest.request maxLength (the project idea limit). */
export const VERSION_REQUEST_MAX = 10_000

export interface StartVersionBlock {
  code: 'VERSION_IN_PROGRESS' | 'RELEASE_NOT_MERGED' | 'NOT_RELEASED'
  /** The tooltip / description on the disabled button (FR-60). */
  reason: string
}

/**
 * FR-60: why "Start new version" is unavailable, in the domain's order (ReleaseVersionRules.CanStart): a version
 * awaiting merge (RELEASE_NOT_MERGED) or in development (VERSION_IN_PROGRESS), then a project that isn't released
 * (NOT_RELEASED). `null` when a new version can start.
 */
export function startVersionBlock(status: ProjectStatus, versions: readonly ReleaseVersion[]): StartVersionBlock | null {
  const development = versions.find((v) => v.status === 'in_development' || v.status === 'awaiting_merge')
  if (development?.status === 'awaiting_merge') {
    return {
      code: 'RELEASE_NOT_MERGED',
      reason: `The ${development.version} release is waiting for its pull request to be merged. Merge it first.`,
    }
  }
  if (development) {
    return { code: 'VERSION_IN_PROGRESS', reason: `Version ${development.version} is already in development.` }
  }
  if (status !== 'released') {
    return { code: 'NOT_RELEASED', reason: 'The project isn’t released yet. Release the version in development first.' }
  }
  return null
}

/** The preset select's "keep the current preset" and "No preset" values (FR-74). */
export const KEEP_PRESET = ':keep'
export const NO_PRESET = ''

/**
 * FR-74: `presetId` for the request: omitted when the choice keeps the base version's preset (including picking that
 * same preset again), `null` for "No preset", else the picked id.
 */
export function presetIdFor(choice: string, currentPresetId: string | null): { presetId?: string | null } {
  if (choice === KEEP_PRESET || choice === (currentPresetId ?? NO_PRESET)) {
    return {}
  }
  return { presetId: choice === NO_PRESET ? null : choice }
}
