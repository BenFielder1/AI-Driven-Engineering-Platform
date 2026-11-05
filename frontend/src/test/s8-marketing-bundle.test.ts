import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * SPEC §8 Performance (first-load JS for `/` ≤ 200 KB gzipped, measured in CI by scripts/ci/check-js-budget.mjs): a
 * static guard on what `/` imports. It follows the *static* imports (not `import()`, which loads on demand) from the
 * root layout, root error boundary, the marketing layout and the Home page, and checks that the app-only client code
 * stays out: the query layer, toasts, Radix dialogs, the error copy table, Zod and the app providers.
 */
const SRC = path.resolve(process.cwd(), 'src')
const ENTRIES = ['app/layout.tsx', 'app/error.tsx', 'app/(marketing)/layout.tsx', 'app/(marketing)/(home)/page.tsx']

const STATIC_IMPORT = /^\s*(?:import|export)\s+(?!type\b)(?:[^'";]*?\sfrom\s+)?['"]([^'"]+)['"]/gm

function resolveLocal(fromFile: string, spec: string): string | null {
  const base = path.resolve(path.dirname(fromFile), spec)
  for (const candidate of [base, `${base}.ts`, `${base}.tsx`, path.join(base, 'index.ts'), path.join(base, 'index.tsx')]) {
    if (existsSync(candidate) && /\.tsx?$/.test(candidate)) {
      return candidate
    }
  }
  return null
}

/** Every local module and package reachable from the entries through static, non-type imports. */
function staticGraph(): { modules: Set<string>; packages: Set<string> } {
  const modules = new Set<string>()
  const packages = new Set<string>()
  const queue = ENTRIES.map((e) => path.join(SRC, e))
  while (queue.length > 0) {
    const file = queue.pop()
    if (file === undefined || modules.has(file)) {
      continue
    }
    modules.add(file)
    const source = readFileSync(file, 'utf8')
    for (const match of source.matchAll(STATIC_IMPORT)) {
      const spec = match[1] ?? ''
      if (spec.startsWith('.')) {
        const resolved = resolveLocal(file, spec)
        if (resolved) {
          queue.push(resolved)
        }
      } else {
        packages.add(spec)
      }
    }
  }
  return { modules, packages }
}

const rel = (file: string) => path.relative(SRC, file).split(path.sep).join('/')

describe('SPEC §8 first-load JS for /', () => {
  it('S8_marketing_bundle: / statically imports none of the app-only client code', () => {
    const { modules, packages } = staticGraph()
    const local = [...modules].map(rel)

    expect(local).toContain('features/marketing/marketing-shell.tsx')
    for (const forbidden of ['app/providers.tsx', 'lib/errors.ts', 'lib/query-client.ts', 'features/auth/auth-api.ts', 'components/ui/sheet.tsx', 'components/ui/sonner.tsx', 'features/projects/labels.ts']) {
      expect(local, forbidden).not.toContain(forbidden)
    }
    for (const forbidden of ['@tanstack/react-query', 'sonner', 'radix-ui', 'zod', 'react-hook-form']) {
      expect([...packages], forbidden).not.toContain(forbidden)
    }
  })

  it('S8_app_providers: the (app) and (public) layouts render the client providers; the root layout does not', () => {
    const read = (file: string) => readFileSync(path.join(SRC, file), 'utf8')

    expect(read('app/layout.tsx')).not.toMatch(/from '\.\/providers'/)
    for (const layout of ['app/(app)/layout.tsx', 'app/(public)/layout.tsx']) {
      expect(read(layout), layout).toMatch(/import \{ Providers \} from '\.\.\/providers'/)
      expect(read(layout), layout).toMatch(/<Providers>/)
    }
  })
})
