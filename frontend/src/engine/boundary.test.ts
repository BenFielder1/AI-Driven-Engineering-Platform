import { ESLint } from 'eslint'
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import tseslint from 'typescript-eslint'
import { describe, expect, it } from 'vitest'

import { ENGINE_BOUNDARY_MESSAGE, ENGINE_BOUNDARY_PATTERN, engineBoundary } from '../../eslint.engine-boundary.js'

const frontend = join(import.meta.dirname, '..', '..')
const engineDir = join(frontend, 'src', 'engine')

/** ESLint with only the boundary rule (no type-aware parsing, so linting a snippet is quick and needs no tsconfig). */
function boundaryLinter() {
  return new ESLint({
    cwd: frontend,
    overrideConfigFile: true,
    overrideConfig: [{ files: ['**/*.{ts,tsx}'], languageOptions: { parser: tseslint.parser } }, engineBoundary],
  })
}

async function boundaryMessages(code: string, filePath: string): Promise<string[]> {
  const [result] = await boundaryLinter().lintText(code, { filePath: join(frontend, filePath) })
  return (result?.messages ?? []).map((m) => `${m.ruleId ?? 'parse'}: ${m.message}`)
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    return entry.isDirectory() ? sourceFiles(path) : /\.(ts|tsx)$/.test(entry.name) ? [path] : []
  })
}

describe('FR-304: the engine UI lint boundary (D-0.7.0-14)', () => {
  it.each([
    ["import { useProjectAccess } from '../../features/projects/project-context'", 'src/engine/pages/x.tsx'],
    ["import { RunsPage } from '../features/runs/runs-page'", 'src/engine/x.ts'],
    ["import Page from '../../app/(app)/projects/[projectId]/pipeline/page'", 'src/engine/pages/x.tsx'],
    ["import { x } from '@/features/live/live-connection'", 'src/engine/live/x.ts'],
    ["export { x } from '../../features/runs/run-format'", 'src/engine/components/x.ts'],
    ["import { x } from '../../lib/../features/runs/run-format'", 'src/engine/components/x.ts'],
    ["import { x } from './../../app/(app)/layout'", 'src/engine/components/x.ts'],
    ["export const load = () => import('../../features/runs/runs-page')", 'src/engine/components/x.ts'],
    ["export const load = () => import('../../lib/../app/(app)/layout')", 'src/engine/components/x.ts'],
    ["export type Page = typeof import('../../features/runs/runs-page')", 'src/engine/components/x.ts'],
    ["export type Ctx = import('@/features/projects/project-context').ProjectAccess", 'src/engine/components/x.ts'],
  ])('FR304_boundary_forbids %s', async (code, file) => {
    const messages = await boundaryMessages(code, file)
    expect(messages).toHaveLength(1)
    expect(messages[0]).toMatch(/^no-restricted-(?:imports|syntax): /)
    expect(messages[0]).toContain(ENGINE_BOUNDARY_MESSAGE)
  })

  it.each([
    ["import { api } from '../../api/client'", 'src/engine/api/x.ts'],
    ["import { Badge } from '../../components/ui/badge'", 'src/engine/components/x.tsx'],
    ["import { toApiError } from '../../lib/errors'", 'src/engine/api/x.ts'],
    ["import { usePipelineRuns } from '../api/hooks'", 'src/engine/pages/x.tsx'],
    ["export const load = () => import('../markdown/markdown-view')", 'src/engine/pages/x.tsx'],
    ["export type Hooks = typeof import('../api/hooks')", 'src/engine/pages/x.tsx'],
    ["import { appPaths } from '../../lib/app-paths'", 'src/engine/pages/x.tsx'],
  ])('FR304_boundary_allows %s', async (code, file) => {
    expect(await boundaryMessages(code, file)).toEqual([])
  })

  it('FR304_boundary_applies_only_inside_src_engine: the SDLC features may import the engine and each other', async () => {
    expect(await boundaryMessages("import { x } from '../runs/run-format'", 'src/features/artifacts/x.ts')).toEqual([])
  })

  it('FR304_boundary_is_in_the_project_lint_config', async () => {
    const config = (await new ESLint({ cwd: frontend }).calculateConfigForFile(join(engineDir, 'pages', 'pipeline-view.tsx'))) as {
      rules?: Record<string, unknown>
    }
    expect(JSON.stringify(config.rules?.['no-restricted-imports'])).toContain('features|app')
    expect(JSON.stringify(config.rules?.['no-restricted-syntax'])).toContain('ImportExpression')
    expect(JSON.stringify(config.rules?.['no-restricted-syntax'])).toContain('TSImportType')
  })

  it('FR304_engine_sources_pass_the_boundary', async () => {
    const files = sourceFiles(engineDir)
    expect(files.length).toBeGreaterThan(10)
    const results = await boundaryLinter().lintFiles(files)
    const problems = results.flatMap((r) => r.messages.map((m) => `${relative(frontend, r.filePath)}: ${m.message}`))
    expect(problems).toEqual([])
    // Belt and braces: no static, dynamic or type import path names the SDLC features or the routes.
    const forbidden = new RegExp(ENGINE_BOUNDARY_PATTERN)
    for (const file of files.filter((f) => !/\.test\.tsx?$/.test(f))) {
      const paths = [...readFileSync(file, 'utf8').matchAll(/(?:\bfrom\s*|\bimport\s*\(\s*|^\s*import\s+)['"]([^'"]+)['"]/gm)].map((m) => m[1] ?? '')
      expect(paths.filter((p) => forbidden.test(p)), file).toEqual([])
    }
  })
})
