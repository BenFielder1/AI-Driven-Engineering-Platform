import type { ArtifactType } from './artifacts-api'

/** The plan artifact types (FR-29): their Markdown ends with the structured ```aide-plan block. */
export const planTypes: readonly ArtifactType[] = ['implementation_plan', 'optimization_plan']

export const isPlanType = (type: ArtifactType) => planTypes.includes(type)

/** The fence language of the block (backend PlanDocument.BlockLanguage). */
export const PLAN_BLOCK_LANGUAGE = 'aide-plan'

export interface PlanPreviewStep {
  number: number
  title: string
  files: { path: string; action: string }[]
  dependencies: number[]
  /** FR-76: the preset specialist that runs the step (absent for the base Code Generator). */
  agent: string | null
  /** FR-76: the requirement ids the step covers. */
  covers: string[]
  /** FR-244, D-0.6.1-10: `'tevrin'` on a step Tevrin added to the plan (§4.41); null on agent-written steps. */
  addedBy: 'tevrin' | null
  /** FR-244: why Tevrin added the step (e.g. "frontend/vercel.json was missing from the plan"); null when absent. */
  addedReason: string | null
}

/** Same shape as the backend's BlockPattern: a line ```aide-plan, the JSON, then a closing ``` line. */
const blockPattern = /^```aide-plan[ \t]*\n([\s\S]*?)\n```[ \t]*$/gm

const asRecord = (value: unknown): Record<string, unknown> | null =>
  typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null

const asString = (value: unknown): string => (typeof value === 'string' ? value : '')

/**
 * FR-244, D-0.6.1-10: a step's provenance. Only `addedBy: "tevrin"` counts (any other value is ignored, like the
 * backend's PlanDocument), and the reason is read only for such a step.
 */
function readProvenance(step: Record<string, unknown>): Pick<PlanPreviewStep, 'addedBy' | 'addedReason'> {
  if (typeof step.addedBy !== 'string' || step.addedBy.trim() !== 'tevrin') {
    return { addedBy: null, addedReason: null }
  }
  const reason = typeof step.addedReason === 'string' ? step.addedReason.trim() : ''
  return { addedBy: 'tevrin', addedReason: reason || null }
}

/**
 * A read-only preview of a plan draft's steps, read from its single ```aide-plan block (FR-29, D6). Display only:
 * the server validates the block when a draft is saved or approved (INVALID_PLAN), so this returns `null` for anything
 * it can't read rather than reporting rule violations of its own.
 */
export function readPlanBlock(markdown: string): PlanPreviewStep[] | null {
  return readPlanDocument(markdown)?.steps ?? null
}

/** The plan block's steps plus its coverage warnings (FR-76: `coverageWarnings` or `coverage.warnings`, if present). */
export function readPlanDocument(markdown: string): { steps: PlanPreviewStep[]; coverageWarnings: string[] } | null {
  const blocks = [...markdown.replaceAll('\r\n', '\n').matchAll(blockPattern)]
  const json = blocks.length === 1 ? blocks[0]?.[1] : undefined
  if (json === undefined) {
    return null
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(json)
  } catch {
    return null
  }
  const root = asRecord(parsed)
  const steps = Array.isArray(parsed) ? parsed : root?.steps
  if (!Array.isArray(steps)) {
    return null
  }
  const warnings = root?.coverageWarnings ?? asRecord(root?.coverage)?.warnings
  const coverageWarnings = Array.isArray(warnings) ? warnings.filter((w): w is string => typeof w === 'string') : []
  const out: PlanPreviewStep[] = []
  for (const item of steps) {
    const step = asRecord(item)
    if (!step || typeof step.number !== 'number') {
      return null
    }
    const files = Array.isArray(step.files) ? step.files : Array.isArray(step.declaredFiles) ? step.declaredFiles : []
    out.push({
      number: step.number,
      title: asString(step.title),
      files: files.flatMap((f) => {
        const file = asRecord(f)
        return file ? [{ path: asString(file.path), action: asString(file.action) }] : []
      }),
      dependencies: Array.isArray(step.dependencies) ? step.dependencies.filter((d): d is number => typeof d === 'number') : [],
      agent: typeof step.agent === 'string' && step.agent ? step.agent : null,
      covers: Array.isArray(step.covers) ? step.covers.filter((c): c is string => typeof c === 'string') : [],
      ...readProvenance(step),
    })
  }
  return { steps: out, coverageWarnings }
}
