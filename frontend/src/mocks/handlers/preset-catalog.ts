import type { Schemas } from '../../api/client'

type PresetDetail = Schemas['PresetDetailDto']

/**
 * The two v2 presets (FR-74, §4.12) as the API returns them, condensed from
 * backend/src/Aide.Infrastructure/Agents/Presets/<id>/preset.json. No dependencies, so both the projects and the
 * presets handlers can read it.
 */
const catalog = (): PresetDetail[] => [
  {
    id: 'nextjs-frontend',
    name: 'Next.js frontend',
    version: '1.0.0',
    description:
      'Frontend web app on Next.js (App Router) and Tailwind, with data from external APIs or mocks, delivered through GitHub Actions and Vercel.',
    stack: ['Next.js (App Router)', 'TypeScript strict', 'Tailwind', 'Vitest', 'Vercel'],
    deployTargets: ['vercel'],
    contentHash: 'a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90',
    agents: [
      { key: 'project-scaffolder', name: 'Project scaffolder', description: 'Sets up the app skeleton.', owns: ['package.json', 'app/layout.tsx'], skills: ['nextjs-ui'] },
      { key: 'api-engineer', name: 'API engineer (BFF)', description: 'Thin, Zod-validated route handlers.', owns: ['app/api/**', 'lib/api/**'], skills: ['nextjs-api-route'] },
      { key: 'ui-engineer', name: 'UI engineer', description: 'Pages and components.', owns: ['app/**', '!app/api/**', 'components/**'], skills: ['nextjs-ui'] },
    ],
    specSections: [
      { title: 'Routes & Rendering Strategy', guidance: 'Every route with its rendering mode (static, dynamic, streaming).' },
      { title: 'Environment Variables', guidance: 'Each variable, where it is used and whether it is public.' },
    ],
    requiredEnv: [{ key: 'API_BASE_URL', targets: ['production', 'preview'], sensitive: false, description: 'The consumed API.' }],
    review: {
      checklist: [{ category: 'security', item: 'No secret env names in "use client" files.', severity: 'critical' }],
      deterministic: ['client-secret-env'],
    },
    protectedPaths: [{ glob: '.github/workflows/**', existingOnBase: true, except: [], reason: 'CI is reviewed by people.' }],
  },
  {
    id: 'nextjs-supabase',
    name: 'Next.js + Supabase',
    version: '1.0.0',
    description:
      'Full-stack web app on Next.js (App Router) with Supabase for Postgres, Auth, Row Level Security and Realtime, delivered through GitHub Actions and Vercel.',
    stack: ['Next.js (App Router)', 'TypeScript strict', 'Supabase (Postgres, Auth, RLS, Realtime)', 'Vercel'],
    deployTargets: ['vercel'],
    contentHash: 'f0e1d2c3b4a5968778695a4b3c2d1e0ff0e1d2c3b4a5968778695a4b3c2d1e0f',
    agents: [
      { key: 'project-scaffolder', name: 'Project scaffolder', description: 'Sets up the app skeleton.', owns: ['package.json', 'lib/supabase/**'], skills: ['nextjs-ui'] },
      { key: 'db-engineer', name: 'Database engineer', description: 'Migrations and RLS policies.', owns: ['supabase/migrations/**'], skills: ['supabase-migration'] },
      { key: 'ui-engineer', name: 'UI engineer', description: 'Pages and components.', owns: ['app/**', '!app/api/**', 'components/**'], skills: ['nextjs-ui'] },
    ],
    specSections: [
      { title: 'Data Model & RLS Policies', guidance: 'Every table with its columns and row level security policies.' },
      { title: 'Auth Flows', guidance: 'Sign-up, sign-in and session handling.' },
    ],
    requiredEnv: [
      { key: 'NEXT_PUBLIC_SUPABASE_URL', targets: ['production', 'preview', 'development'], sensitive: false, description: 'The Supabase project URL.' },
      { key: 'SUPABASE_SERVICE_ROLE_KEY', targets: ['production'], sensitive: true, description: 'Server-only service key.' },
    ],
    review: {
      checklist: [{ category: 'security', item: 'Every table has RLS enabled.', severity: 'critical' }],
      deterministic: ['rls-enabled', 'client-secret-env'],
    },
    protectedPaths: [{ glob: 'supabase/migrations/**', existingOnBase: true, except: [], reason: 'Applied migrations never change.' }],
  },
]

export const presetCatalog = { presets: catalog() }

export function resetPresetCatalog() {
  presetCatalog.presets = catalog()
}

export const findPreset = (id: string | null | undefined): PresetDetail | undefined =>
  id ? presetCatalog.presets.find((p) => p.id === id) : undefined

/** ReleaseVersionDto.presetName: the preset's name, or its id when it is no longer shipped. */
export const presetNameOf = (id: string | null | undefined): string | null => (id ? (findPreset(id)?.name ?? id) : null)

export function toPresetSummary(p: PresetDetail): Schemas['PresetSummaryDto'] {
  const { id, name, version, description, stack, deployTargets, contentHash } = p
  return { id, name, version, description, stack, deployTargets, contentHash }
}
