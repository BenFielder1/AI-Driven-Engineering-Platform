import type { Schemas } from '../../../api/client'

/**
 * FR-220, FR-222: fixture data for the product previews (static, no API calls). A made-up example project, shown as
 * the real app would show it; captions say what each preview shows.
 */
export const previewProject = {
  name: 'Inventory tracker',
  releasedVersion: '1.1.0',
  developmentVersion: '1.2.0',
  checkpoint: 14,
} as const

export type RailState = 'done' | 'current' | 'upcoming'

export const previewRail: readonly { phase: Schemas['Phase']; state: RailState }[] = [
  { phase: 'refinement', state: 'done' },
  { phase: 'specification', state: 'current' },
  { phase: 'planning', state: 'upcoming' },
  { phase: 'execution', state: 'upcoming' },
  { phase: 'review', state: 'upcoming' },
  { phase: 'release', state: 'upcoming' },
]

export const previewGate = {
  title: 'Spec approval',
  artifact: 'Technical specification v3',
  requestedBy: 'Spec Generator',
  reason: 'Adds stock alerts and a low-stock report to the 1.2.0 spec.',
} as const

/** A side-by-side diff between spec v2 and v3, in the API's line format (FR-21). */
export const previewDiff: Schemas['ArtifactDiffDto'] = {
  from: 2,
  to: 3,
  unifiedDiff: '',
  lines: [
    { kind: 'context', oldLine: 12, newLine: 12, text: '## 3. Functional requirements', skipped: null },
    { kind: 'removed', oldLine: 13, newLine: null, text: '- FR-7 Stock counts per item.', skipped: null },
    { kind: 'added', oldLine: null, newLine: 13, text: '- FR-7 Stock counts per item and site.', skipped: null },
    { kind: 'added', oldLine: null, newLine: 14, text: '- FR-8 Alert when stock falls below a threshold.', skipped: null },
    { kind: 'context', oldLine: 14, newLine: 15, text: '- FR-9 Export to CSV.', skipped: null },
  ],
}

export const previewArtifactHash = 'sha256 9f2c…41ab'

export const previewRuns: readonly { agent: string; model: string; status: 'approved' | 'running' | 'in_review'; label: string }[] = [
  { agent: 'Idea Refinement', model: 'claude-sonnet-5-5', status: 'approved', label: 'Succeeded' },
  { agent: 'Spec Generator', model: 'claude-opus-5-5', status: 'in_review', label: 'Draft ready' },
  { agent: 'Code Generator · step 4/9', model: 'gpt-6-astra', status: 'running', label: 'Running' },
]

export const previewDeployments: readonly { part: string; platform: string; env: string; status: 'approved' | 'running' }[] = [
  { part: 'Frontend', platform: 'Vercel', env: 'preview', status: 'approved' },
  { part: 'Backend', platform: 'Render', env: 'preview', status: 'running' },
  { part: 'Database', platform: 'Supabase', env: 'preview', status: 'approved' },
]
