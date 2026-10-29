import type { DetectStackRequest, StackCompositionRequest, StackDetection } from '../stack/stack-api'
import { stackChoiceFieldId, stackPathFieldId } from '../stack/stack-composer'
import { toStackRequest, type StackSelection } from '../stack/stack-model'
import { releasedTagOf, type WizardValues } from './schemas'

/** The wizard's flat stack fields per selection key (flat, so the error summary and field ids stay one level). */
export const wizardStackFields = {
  frontend: 'stackFrontend',
  backend: 'stackBackend',
  data: 'stackData',
  vcs: 'stackVcs',
  frontendHost: 'stackFrontendHost',
  backendHost: 'stackBackendHost',
  databaseHost: 'stackDatabaseHost',
  local: 'stackLocal',
  originModel: 'stackOriginModel',
  frontendPath: 'stackFrontendPath',
  backendPath: 'stackBackendPath',
} as const satisfies Record<keyof StackSelection, keyof WizardValues>

export type WizardStackField = (typeof wizardStackFields)[keyof StackSelection]

export const wizardStackFieldList = Object.values(wizardStackFields)

export const WIZARD_STACK_PREFIX = 'project-stack'

/** Error-summary targets of the stack fields. */
export const wizardStackFieldIds: Record<WizardStackField | 'stackKind', string> = {
  stackKind: 'project-stack-kind',
  stackFrontend: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'frontend'),
  stackBackend: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'backend'),
  stackData: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'data'),
  stackVcs: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'vcs'),
  stackFrontendHost: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'frontendHost'),
  stackBackendHost: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'backendHost'),
  stackDatabaseHost: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'databaseHost'),
  stackLocal: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'local'),
  stackOriginModel: stackChoiceFieldId(WIZARD_STACK_PREFIX, 'originModel'),
  stackFrontendPath: stackPathFieldId(WIZARD_STACK_PREFIX, 'frontend'),
  stackBackendPath: stackPathFieldId(WIZARD_STACK_PREFIX, 'backend'),
}

/** The wizard fields of each sub-step (catalog layer), for per-sub-step validation. */
export const wizardStackLayerFields: Readonly<Record<string, readonly WizardStackField[]>> = {
  frontend: ['stackFrontend', 'stackFrontendPath'],
  backend: ['stackBackend', 'stackBackendPath'],
  data: ['stackData'],
  vcs: ['stackVcs'],
  deploy: ['stackFrontendHost', 'stackBackendHost', 'stackDatabaseHost', 'stackLocal', 'stackOriginModel'],
}

export function selectionOfValues(v: Pick<WizardValues, WizardStackField>): StackSelection {
  return Object.fromEntries(
    Object.entries(wizardStackFields).map(([key, field]) => [key, v[field]]),
  ) as unknown as StackSelection
}

/** The repository host's name for copy (GitHub for both GitHub modes, GitLab), or `null` for Local. */
export function repoHostLabelOf(gitMode: WizardValues['gitMode']): string | null {
  return gitMode === 'local' ? null : gitMode === 'gitlab' ? 'GitLab' : 'GitHub'
}

/** §4.17 R5: the VCS option id fixed by the repository's host, or `null` for Local (any). */
export function repoVcsOf(gitMode: WizardValues['gitMode']): string | null {
  switch (gitMode) {
    case 'local':
      return null
    case 'gitlab':
      return 'vcs-gitlab'
    default:
      return 'vcs-github'
  }
}

/** FR-114/FR-117: `stack` for CreateProject; the VCS is omitted when the repository sets it (R5). */
export function toWizardStackRequest(v: WizardValues): StackCompositionRequest {
  return toStackRequest(selectionOfValues(v), { omitVcs: repoVcsOf(v.gitMode) !== null })
}

/**
 * FR-118: the repository to detect the stack of, at the import's base (the released tag, else the default branch), or
 * `null` while it isn't known.
 */
export function detectRequestOf(v: WizardValues): DetectStackRequest | null {
  const ref = releasedTagOf(v) ?? (v.defaultBranch.trim() || null)
  switch (v.gitMode) {
    case 'github_app':
      return v.repoFullName ? { git: { provider: 'github_app', repo: v.repoFullName, ref } } : null
    case 'github_pat':
      return v.repoUrl.trim()
        ? { git: { provider: 'github_pat', repo: v.repoUrl.trim(), ref, token: v.gitToken ? v.gitToken : null } }
        : null
    case 'local':
      return v.localSourcePath.trim() ? { git: { provider: 'local', repo: v.localSourcePath.trim(), ref } } : null
    case 'gitlab':
      // FR-141: the picked GitLab project (by id) on its instance, read with the chosen connection.
      return v.gitlabRepoId && v.gitlabInstanceId && v.gitlabConnectionId
        ? {
            git: {
              provider: 'gitlab',
              repo: v.gitlabRepoId,
              ref,
              instanceId: v.gitlabInstanceId,
              connectionId: v.gitlabConnectionId,
            },
          }
        : null
  }
}

/** FR-118: the identity of a detection result (the repository, ref and commit it read; never the token). */
export function detectionIdOf(request: DetectStackRequest, detection: StackDetection): string {
  const { provider, instanceId, connectionId, repo, ref } = request.git
  return JSON.stringify({ provider, instanceId, connectionId, repo, ref, commit: detection.commit })
}

/**
 * FR-118: the stack fields a user edits (kind, frontend, backend, data, paths), to tell whether the stack is untouched
 * since the last applied detection. Fields the step sets itself (the VCS fixed by the repo, deployment defaults) are
 * left out.
 */
export function stackSnapshot(v: Pick<WizardValues, 'stackKind' | WizardStackField>): string {
  return JSON.stringify([v.stackKind, v.stackFrontend, v.stackBackend, v.stackData, v.stackFrontendPath, v.stackBackendPath])
}
