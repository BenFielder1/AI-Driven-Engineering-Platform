import { http, HttpResponse } from 'msw'

import type { Schemas } from '../../api/client'
import { nextId } from './auth'
import { problem } from './problem'
import { access, type StoredExecutionStep, type StoredProject } from './projects'

type Artifact = Schemas['ArtifactDto']
type FileAction = Schemas['FileAction']

const planTypes: readonly Schemas['ArtifactType'][] = ['implementation_plan', 'optimization_plan']

export interface MockPlanStep {
  number: number
  title: string
  task: string
  files: { path: string; action: FileAction }[]
  dependencies: number[]
  userInstructions?: string | null
}

/** A plan document as the backend renders it (PlanDocument.Render): a readable list, then the aide-plan block. */
export function planMarkdown(steps: readonly MockPlanStep[], title = 'Implementation plan'): string {
  const prose = steps.map((s) => `## Step ${String(s.number)}: ${s.title}\n\n${s.task}`).join('\n\n')
  const json = JSON.stringify({ format: 1, steps }, null, 2)
  return `# ${title}\n\n${prose}\n\n## Plan data\n\n\`\`\`aide-plan\n${json}\n\`\`\`\n`
}

/** `count` valid steps; step n depends on step n − 1. */
export function planSteps(count: number): MockPlanStep[] {
  return Array.from({ length: count }, (_, i) => ({
    number: i + 1,
    title: `Step ${String(i + 1)} title`,
    task: `Do part ${String(i + 1)}.`,
    files: [{ path: `src/part-${String(i + 1)}.ts`, action: 'create' as const }],
    dependencies: i === 0 ? [] : [i],
  }))
}

const protectedPrefixes = ['.git/', '.aide/', '.github/workflows/', '.circleci/', '.azure-pipelines/']
const lockFiles = ['package-lock.json', 'yarn.lock', 'pnpm-lock.yaml', 'packages.lock.json', 'cargo.lock', 'poetry.lock']

/** A subset of the backend's PathRules.IsProtected (§4.6): .git/.aide, CI workflows, lockfiles and env files. */
export function isProtectedPath(path: string): boolean {
  const p = path.trim().replaceAll('\\', '/').replace(/^(\.\/)+/, '').toLowerCase()
  if (protectedPrefixes.some((prefix) => `${p}/`.startsWith(prefix))) {
    return true
  }
  const name = p.split('/').at(-1) ?? ''
  return lockFiles.includes(name) || name === '.env' || name.startsWith('.env.')
}

const blockPattern = /^```aide-plan[ \t]*\n([\s\S]*?)\n```[ \t]*$/gm

/**
 * A subset of the backend's PlanDocument.ParseMarkdown + PlanValidator (FR-29): exactly one block, readable JSON,
 * steps numbered 1..n, titles, at least one file, and dependencies on earlier steps only. Every reason is returned.
 */
export function validatePlan(markdown: string): { steps: MockPlanStep[] } | { reasons: string[] } {
  const blocks = [...markdown.replaceAll('\r\n', '\n').matchAll(blockPattern)]
  if (blocks.length === 0) {
    return { reasons: ['The plan has no ```aide-plan block with the structured steps.'] }
  }
  if (blocks.length > 1) {
    return { reasons: [`The plan has ${String(blocks.length)} \`\`\`aide-plan blocks; keep exactly one.`] }
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(blocks[0]?.[1] ?? '')
  } catch (e) {
    return { reasons: [`The plan is not valid JSON (${e instanceof Error ? e.message : 'unreadable'}).`] }
  }
  const raw = Array.isArray(parsed) ? parsed : (parsed as { steps?: unknown } | null)?.steps
  if (!Array.isArray(raw)) {
    return { reasons: ['The plan JSON needs a "steps" array.'] }
  }
  const steps = raw as Partial<MockPlanStep>[]
  const reasons: string[] = []
  if (steps.length === 0) {
    reasons.push('The plan has no steps.')
  }
  steps.forEach((step, i) => {
    const n = String(step.number ?? '?')
    if (step.number !== i + 1) {
      reasons.push(`Steps must be numbered 1..n in order: position ${String(i + 1)} has number ${n}.`)
    }
    if (!(step.title ?? '').trim()) {
      reasons.push(`Step ${n} has no title.`)
    }
    for (const dep of step.dependencies ?? []) {
      if (dep >= (step.number ?? 0) || dep < 1) {
        reasons.push(`Step ${n} depends on step ${String(dep)}; dependencies must point to earlier steps.`)
      }
    }
    if ((step.files ?? []).length === 0) {
      reasons.push(`Step ${n} declares no files; every step must create or modify at least one file.`)
    }
  })
  return reasons.length > 0 ? { reasons } : { steps: steps as MockPlanStep[] }
}

/** 400 INVALID_PLAN as the API sends it: the first reason in the title, every reason in `details`. */
export const invalidPlan = (reasons: string[]) =>
  problem(400, 'INVALID_PLAN', `The plan is invalid: ${reasons[0] ?? ''}`, { details: reasons })

/** FR-30: one pending step per plan step for the approved plan version (once per version). */
export function createExecutionSteps(project: StoredProject, plan: Artifact, steps: readonly MockPlanStep[]) {
  if (project.executionSteps.some((s) => s.planArtifactId === plan.id)) {
    return
  }
  project.executionSteps.push(
    ...steps.map(
      (s): StoredExecutionStep => ({
        id: nextId(),
        planArtifactId: plan.id,
        number: s.number,
        title: s.title,
        task: s.task,
        declaredFiles: s.files.map((f) => ({ ...f, protected: isProtectedPath(f.path) })),
        dependencies: s.dependencies,
        userInstructions: s.userInstructions ?? null,
        status: 'pending',
        attempt: 0,
        violations: null,
        branchName: null,
        commitSha: null,
        startedAt: null,
        completedAt: null,
      }),
    ),
  )
}

export const executionHandlers = [
  // FR-30: the steps of a plan version (default: the current locked implementation plan).
  http.get('*/api/projects/:id/execution/steps', ({ request, params }) => {
    const a = access(params.id)
    if (a instanceof Response) {
      return a
    }
    const q = new URL(request.url).searchParams
    const type = (q.get('planType') ?? 'implementation_plan') as Schemas['ArtifactType']
    if (!planTypes.includes(type)) {
      return problem(400, 'VALIDATION', 'One or more validation errors occurred.', {
        errors: { planType: ['Steps belong to implementation_plan or optimization_plan versions.'] },
      })
    }
    const versionParam = q.get('planVersion')
    const plans = a.project.artifacts.filter((x) => x.type === type)
    const plan =
      versionParam === null
        ? plans.filter((x) => x.status === 'locked').sort((x, y) => y.version - x.version)[0]
        : plans.find((x) => x.version === Number(versionParam))
    if (!plan) {
      return versionParam === null
        ? HttpResponse.json<Schemas['ExecutionStepsDto']>({ plan: null, steps: [] })
        : problem(404, 'NOT_FOUND', `Version ${versionParam} of the ${type} was not found.`)
    }
    const steps = a.project.executionSteps
      .filter((s) => s.planArtifactId === plan.id)
      .sort((x, y) => x.number - y.number)
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      .map(({ planArtifactId, ...dto }) => dto)
    return HttpResponse.json<Schemas['ExecutionStepsDto']>({
      plan: { artifactId: plan.id, type: plan.type, version: plan.version, status: plan.status },
      steps,
    })
  }),
]
