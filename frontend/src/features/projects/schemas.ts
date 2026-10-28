import { z } from 'zod'

import { checkRootDirectory, checkVercelProjectName } from '../deployments/vercel-project-rules'
import { checkNewVersion, DEFAULT_INITIAL_VERSION, normalizeVersion } from '../versions/semver'
import { approvalModes, type agentKeys } from './labels'

/**
 * Client-side validation mirroring the API's limits (backend ProjectLimits / ProjectDefaults). Numeric inputs are
 * kept as strings in the forms and converted when the request is built; the API re-validates everything.
 */
export const limits = {
  nameMax: 100,
  ideaMax: 10_000,
  thresholdMin: 0.5,
  thresholdMax: 1,
  filesMin: 1,
  filesMax: 100,
  cyclesMin: 1,
  cyclesMax: 10,
  budgetMin: 1000,
  urlMax: 2048,
  branchMax: 255,
  secretMax: 1024,
  modelMax: 200,
  emailMax: 256,
} as const

/** FR-5 defaults. */
export const projectDefaults = {
  approvalMode: 'advisory',
  complianceThreshold: '0.90',
  maxFilesPerStep: '20',
  maxReviewCycles: '3',
  defaultBranch: 'main',
} as const

/** `https://github.com/OWNER/REPO`, optionally ending in `.git` (FR-35). */
export const GITHUB_REPO_URL = /^https:\/\/github\.com\/[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\/[A-Za-z0-9._-]+$/

type Check = string | null

export function checkName(v: string): Check {
  const t = v.trim()
  if (!t) {
    return 'Enter a project name.'
  }
  return t.length > limits.nameMax ? `Project names are at most ${String(limits.nameMax)} characters.` : null
}

export function checkThreshold(v: string): Check {
  const n = Number(v)
  const ok =
    v.trim() !== '' &&
    Number.isFinite(n) &&
    n >= limits.thresholdMin &&
    n <= limits.thresholdMax &&
    /^\d+(\.\d{1,2})?$/.test(v.trim())
  return ok ? null : 'Enter a compliance threshold from 0.50 to 1.00 (two decimal places at most).'
}

function checkWholeNumber(v: string, min: number, max: number, label: string): Check {
  const n = Number(v)
  const ok = /^\d+$/.test(v.trim()) && n >= min && n <= max
  return ok ? null : `Enter a whole number from ${String(min)} to ${String(max)} for ${label}.`
}

export const checkMaxFiles = (v: string) =>
  checkWholeNumber(v, limits.filesMin, limits.filesMax, 'max files per step')

export const checkMaxCycles = (v: string) =>
  checkWholeNumber(v, limits.cyclesMin, limits.cyclesMax, 'max review cycles')

/** Empty means unlimited. */
export function checkTokenBudget(v: string): Check {
  if (v.trim() === '') {
    return null
  }
  const ok = /^\d+$/.test(v.trim()) && Number(v) >= limits.budgetMin && Number.isSafeInteger(Number(v))
  return ok ? null : `Enter a whole number of at least ${limits.budgetMin.toLocaleString()} tokens, or leave it empty for no limit.`
}

export function checkBranch(v: string): Check {
  const t = v.trim()
  if (!t) {
    return 'Enter the default branch, such as "main".'
  }
  return t.length > limits.branchMax ? `Branch names are at most ${String(limits.branchMax)} characters.` : null
}

export function checkRepoUrl(v: string): Check {
  const t = v.trim()
  if (!t) {
    return 'Enter the GitHub repository URL.'
  }
  return GITHUB_REPO_URL.test(t) && t.length <= limits.urlMax
    ? null
    : 'Enter a URL like https://github.com/owner/repo.'
}

export function checkSecret(v: string, label: string): Check {
  return v.length > limits.secretMax ? `The ${label} is at most ${String(limits.secretMax)} characters.` : null
}

/** Parses an optional budget input into the request value (`null` = unlimited). */
export const toBudget = (v: string): number | null => (v.trim() === '' ? null : Number(v))

/** FR-66: the initial version is semver (a leading `v` is stripped). */
export function checkInitialVersion(v: string): Check {
  const check = checkNewVersion(v, null)
  return check.ok ? null : check.message
}

const str = z.string()

/** Adds `message` at `path` when the check fails. */
function addCheck(ctx: z.RefinementCtx, path: string, message: Check) {
  if (message) {
    ctx.addIssue({ code: 'custom', path: [path], message })
  }
}

// ---- New-project wizard (FR-5) ---------------------------------------------------------------------------------

/** FR-73: the released-version radio value for "Never released" (sent as `releasedTag: null`). */
export const NEVER_RELEASED = '__never_released__'

/** FR-73: the released tag the wizard sends, or `null` for "Never released" (the radio option or an empty field). */
export function releasedTagOf(v: Pick<WizardValues, 'releasedTag'>): string | null {
  const tag = v.releasedTag.trim()
  return tag === '' || tag === NEVER_RELEASED ? null : tag
}

export const localSourcePathMax = 1024

/** FR-159: the wizard's per-agent model fields (flat, so the error summary and field ids stay one level). */
export const wizardAgentModelFields = {
  ideaRefinement: 'llmModelIdeaRefinement',
  specGenerator: 'llmModelSpecGenerator',
  planGenerator: 'llmModelPlanGenerator',
  codeGenerator: 'llmModelCodeGenerator',
  codeReview: 'llmModelCodeReview',
} as const satisfies Record<(typeof agentKeys)[number], string>

const modelTooLong = `Model ids are at most ${String(limits.modelMax)} characters.`

/** The wizard's fields, without the cross-field refinement (also used to check a restored session-storage draft). */
export const wizardShape = z.object({
  /** FR-73: start from scratch, or import an existing repository. */
  source: z.enum(['scratch', 'import']),
  name: str,
  idea: str,
  approvalMode: z.enum(approvalModes),
  complianceThreshold: str,
  maxFilesPerStep: str,
  maxReviewCycles: str,
  tokenBudget: str,
  /** FR-82, FR-86: `github_app` (picker / create), `github_pat` (URL + token) or `local`. */
  gitMode: z.enum(['github_app', 'github_pat', 'local']),
  repoUrl: str,
  defaultBranch: str,
  gitToken: str,
  /** `github_app`: the picked repository (`owner/name`) and the installation that grants it (a number, as text). */
  repoFullName: str,
  installationId: str,
  /** `github_app`: the picked repository already has commits (FR-86 warning). */
  repoHasCode: z.boolean(),
  initialVersion: str,
  /** FR-73 import: the released tag (a tag name, NEVER_RELEASED, or '' typed as "never released"). */
  releasedTag: str,
  /** FR-73 import: the released tag's semver ('' when never released or not a version), kept in sync by the wizard. */
  releasedVersion: str,
  /** FR-73 import: the version to develop. */
  developmentVersion: str,
  /** FR-73 import with `local` (development only): the server path of the repository to import. */
  localSourcePath: str,
  /** FR-74 legacy presets (retiring in v4): a preset id from GET /api/presets, or '' for none. */
  presetId: str,
  /**
   * FR-114 Stack step: "No stack (general)", a composition from the five sub-steps, or a legacy preset (`presetId`).
   * The composition is one option id per choice ('' = not chosen yet) and the layer paths (FR-119).
   */
  stackKind: z.enum(['general', 'compose', 'preset']),
  stackFrontend: str,
  stackBackend: str,
  stackData: str,
  stackVcs: str,
  stackFrontendHost: str,
  stackBackendHost: str,
  stackDatabaseHost: str,
  stackLocal: str,
  stackOriginModel: str,
  stackFrontendPath: str,
  stackBackendPath: str,
  /** FR-118: the identity of the import detection last applied to the stack fields ('' = none). */
  stackDetection: str,
  /** FR-88 Deployment step (optional): skip, or Vercel with one of the caller's own connections. */
  deploymentMode: z.enum(['none', 'vercel']),
  vercelConnectionId: str,
  /** Create a new Vercel project (name, root directory) or link an existing one (id or name). */
  vercelProjectMode: z.enum(['create', 'link']),
  vercelProjectName: str,
  vercelRootDirectory: str,
  vercelProjectId: str,
  /** FR-159 AI model section: '' until a usable provider is chosen (an unusable one is never preselected). */
  llmProvider: z.enum(['anthropic', 'openai', 'ollama', 'fake', '']),
  /** One of the caller's own connections for the provider, or '' (system key, keyless, or none shown). */
  llmCredentialId: str,
  llmDefaultModel: str,
  llmModelIdeaRefinement: str,
  llmModelSpecGenerator: str,
  llmModelPlanGenerator: str,
  llmModelCodeGenerator: str,
  llmModelCodeReview: str,
})

/** One loose object plus one refinement, so every field is checked together (per-step checks use RHF `trigger`). */
export const wizardSchema = wizardShape.superRefine((v, ctx) => {
  const importing = v.source === 'import'
  addCheck(ctx, 'name', checkName(v.name))
  if (importing) {
    const releasedTag = releasedTagOf(v)
    const released = releasedTag === null ? null : v.releasedVersion || normalizeVersion(releasedTag)
    if (releasedTag !== null && !released) {
      addCheck(
        ctx,
        'releasedTag',
        "This tag isn't a semantic version (for example v2.3.0). Enter the released version's tag, or leave it empty.",
      )
    }
    const check = checkNewVersion(v.developmentVersion, released)
    addCheck(ctx, 'developmentVersion', check.ok ? null : check.message)
    if (v.gitMode === 'local') {
      const path = v.localSourcePath.trim()
      addCheck(
        ctx,
        'localSourcePath',
        !path
          ? 'Enter the path of the repository to import.'
          : path.length > localSourcePathMax
            ? `The path is at most ${String(localSourcePathMax)} characters.`
            : null,
      )
    }
  } else {
    addCheck(ctx, 'initialVersion', checkInitialVersion(v.initialVersion))
  }
  addCheck(
    ctx,
    'idea',
    !v.idea.trim()
      ? importing
        ? 'Describe what this version should deliver.'
        : 'Describe your idea: what should the software do, and for whom?'
      : v.idea.length > limits.ideaMax
        ? `The ${importing ? 'request' : 'idea'} is at most ${limits.ideaMax.toLocaleString()} characters.`
        : null,
  )
  addCheck(ctx, 'complianceThreshold', checkThreshold(v.complianceThreshold))
  addCheck(ctx, 'maxFilesPerStep', checkMaxFiles(v.maxFilesPerStep))
  addCheck(ctx, 'maxReviewCycles', checkMaxCycles(v.maxReviewCycles))
  addCheck(ctx, 'tokenBudget', checkTokenBudget(v.tokenBudget))
  addCheck(ctx, 'defaultBranch', checkBranch(v.defaultBranch))
  if (v.gitMode === 'github_pat') {
    addCheck(ctx, 'repoUrl', checkRepoUrl(v.repoUrl))
    addCheck(ctx, 'gitToken', checkSecret(v.gitToken, 'access token'))
  }
  if (v.gitMode === 'github_app' && (!v.repoFullName || !v.installationId)) {
    addCheck(ctx, 'repoFullName', 'Pick a repository, or create one.')
  }
  // FR-114: a composition needs a choice (or None) for each layer up to VCS; compatibility is the server's (D-V4-19).
  if (v.stackKind === 'compose') {
    addCheck(ctx, 'stackFrontend', v.stackFrontend ? null : 'Choose the frontend, or None.')
    addCheck(ctx, 'stackBackend', v.stackBackend ? null : 'Choose the backend, or None.')
    addCheck(ctx, 'stackData', v.stackData ? null : 'Choose the data layer, or None.')
    addCheck(ctx, 'stackVcs', v.stackVcs ? null : 'Choose the version control host.')
  }
  if (v.stackKind === 'preset') {
    addCheck(ctx, 'presetId', v.presetId ? null : 'Pick a legacy preset, or choose No stack (general).')
  }
  // FR-88: the server decides whether a Local project can deploy (VALIDATION unless it runs the Fake provider).
  if (v.deploymentMode === 'vercel') {
    addCheck(ctx, 'vercelConnectionId', v.vercelConnectionId ? null : 'Pick one of your Vercel connections, or skip deployment.')
    if (v.vercelProjectMode === 'create') {
      addCheck(ctx, 'vercelProjectName', checkVercelProjectName(v.vercelProjectName))
      addCheck(ctx, 'vercelRootDirectory', checkRootDirectory(v.vercelRootDirectory))
    } else {
      addCheck(ctx, 'vercelProjectId', v.vercelProjectId.trim() ? null : 'Enter the Vercel project id (prj_…) or name.')
    }
  }
  // FR-159: the AI model section.
  addCheck(ctx, 'llmProvider', v.llmProvider ? null : 'Choose an AI provider you can use, or add a connection for one.')
  const model = v.llmDefaultModel.trim()
  addCheck(ctx, 'llmDefaultModel', !model ? 'Choose the default model, or enter its id.' : model.length > limits.modelMax ? modelTooLong : null)
  for (const field of Object.values(wizardAgentModelFields)) {
    addCheck(ctx, field, v[field].trim().length > limits.modelMax ? modelTooLong : null)
  }
})

export type WizardValues = z.infer<typeof wizardSchema>

export const wizardDefaults: WizardValues = {
  source: 'scratch',
  name: '',
  idea: '',
  approvalMode: projectDefaults.approvalMode,
  complianceThreshold: projectDefaults.complianceThreshold,
  maxFilesPerStep: projectDefaults.maxFilesPerStep,
  maxReviewCycles: projectDefaults.maxReviewCycles,
  tokenBudget: '',
  gitMode: 'github_pat',
  repoUrl: '',
  defaultBranch: projectDefaults.defaultBranch,
  gitToken: '',
  repoFullName: '',
  installationId: '',
  repoHasCode: false,
  initialVersion: DEFAULT_INITIAL_VERSION,
  releasedTag: '',
  releasedVersion: '',
  developmentVersion: DEFAULT_INITIAL_VERSION,
  localSourcePath: '',
  presetId: '',
  stackKind: 'general',
  stackFrontend: '',
  stackBackend: '',
  stackData: '',
  stackVcs: '',
  stackFrontendHost: '',
  stackBackendHost: '',
  stackDatabaseHost: '',
  stackLocal: '',
  stackOriginModel: '',
  stackFrontendPath: 'frontend',
  stackBackendPath: 'backend',
  stackDetection: '',
  deploymentMode: 'none',
  vercelConnectionId: '',
  vercelProjectMode: 'create',
  vercelProjectName: '',
  vercelRootDirectory: '',
  vercelProjectId: '',
  llmProvider: '',
  llmCredentialId: '',
  llmDefaultModel: '',
  llmModelIdeaRefinement: '',
  llmModelSpecGenerator: '',
  llmModelPlanGenerator: '',
  llmModelCodeGenerator: '',
  llmModelCodeReview: '',
}

// ---- Settings → General (FR-7) ---------------------------------------------------------------------------------

export const generalSchema = z
  .object({
    name: str,
    approvalMode: z.enum(approvalModes),
    complianceThreshold: str,
    maxFilesPerStep: str,
    maxReviewCycles: str,
    tokenBudget: str,
  })
  .superRefine((v, ctx) => {
    addCheck(ctx, 'name', checkName(v.name))
    addCheck(ctx, 'complianceThreshold', checkThreshold(v.complianceThreshold))
    addCheck(ctx, 'maxFilesPerStep', checkMaxFiles(v.maxFilesPerStep))
    addCheck(ctx, 'maxReviewCycles', checkMaxCycles(v.maxReviewCycles))
    addCheck(ctx, 'tokenBudget', checkTokenBudget(v.tokenBudget))
  })

export type GeneralValues = z.infer<typeof generalSchema>
