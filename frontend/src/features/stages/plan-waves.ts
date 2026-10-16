import type { PlanPreviewStep } from '../artifacts/plan-block'

/**
 * Dependency waves: a step runs in the wave after the latest of its dependencies (unknown or forward dependencies
 * count as none; the server validates the order, FR-29).
 */
export function dependencyWaves(steps: readonly Pick<PlanPreviewStep, 'number' | 'dependencies'>[]): number[][] {
  const wave = new Map<number, number>()
  for (const step of [...steps].sort((a, b) => a.number - b.number)) {
    const deps = step.dependencies.map((d) => wave.get(d)).filter((w): w is number => w !== undefined)
    wave.set(step.number, deps.length === 0 ? 1 : Math.max(...deps) + 1)
  }
  const waves: number[][] = []
  for (const [number, w] of wave) {
    ;(waves[w - 1] ??= []).push(number)
  }
  return waves.filter((w) => w.length > 0)
}
