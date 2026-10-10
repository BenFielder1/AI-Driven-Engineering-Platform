import { z } from 'zod'

import { approvalModes } from './labels'

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

const str = z.string()

/** Adds `message` at `path` when the check fails. */
function addCheck(ctx: z.RefinementCtx, path: string, message: Check) {
  if (message) {
    ctx.addIssue({ code: 'custom', path: [path], message })
  }
}

// ---- New-project wizard (FR-5) ---------------------------------------------------------------------------------

/** One loose object plus one refinement, so every field is checked together (per-step checks use RHF `trigger`). */
export const wizardSchema = z
  .object({
    name: str,
    idea: str,
    approvalMode: z.enum(approvalModes),
    complianceThreshold: str,
    maxFilesPerStep: str,
    maxReviewCycles: str,
    tokenBudget: str,
    gitProvider: z.enum(['github', 'local']),
    repoUrl: str,
    defaultBranch: str,
    gitToken: str,
  })
  .superRefine((v, ctx) => {
    addCheck(ctx, 'name', checkName(v.name))
    addCheck(
      ctx,
      'idea',
      !v.idea.trim()
        ? 'Describe your idea: what should the software do, and for whom?'
        : v.idea.length > limits.ideaMax
          ? `The idea is at most ${limits.ideaMax.toLocaleString()} characters.`
          : null,
    )
    addCheck(ctx, 'complianceThreshold', checkThreshold(v.complianceThreshold))
    addCheck(ctx, 'maxFilesPerStep', checkMaxFiles(v.maxFilesPerStep))
    addCheck(ctx, 'maxReviewCycles', checkMaxCycles(v.maxReviewCycles))
    addCheck(ctx, 'tokenBudget', checkTokenBudget(v.tokenBudget))
    addCheck(ctx, 'defaultBranch', checkBranch(v.defaultBranch))
    if (v.gitProvider === 'github') {
      addCheck(ctx, 'repoUrl', checkRepoUrl(v.repoUrl))
      addCheck(ctx, 'gitToken', checkSecret(v.gitToken, 'access token'))
    }
  })

export type WizardValues = z.infer<typeof wizardSchema>

export const wizardDefaults: WizardValues = {
  name: '',
  idea: '',
  approvalMode: projectDefaults.approvalMode,
  complianceThreshold: projectDefaults.complianceThreshold,
  maxFilesPerStep: projectDefaults.maxFilesPerStep,
  maxReviewCycles: projectDefaults.maxReviewCycles,
  tokenBudget: '',
  gitProvider: 'github',
  repoUrl: '',
  defaultBranch: projectDefaults.defaultBranch,
  gitToken: '',
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
