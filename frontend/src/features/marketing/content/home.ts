import type { ProductSectionId } from './product'

/**
 * FR-212, FR-222: Home copy (sections in the spec's order). Lists shared with other pages (integrations, lifecycle,
 * changelog, FAQ, self-host) come from their own modules. Sources as in product.ts and how-it-works.ts.
 */
export const heroSubhead =
  'Tevrin coordinates AI agents through six phases (refinement, specification, planning, execution, review and release), with approval gates between them, versioned artifacts you can diff, and rollback when you need it.'

export const worksWithNote = 'Works with the Git hosts, trackers, platforms and models you already use. Not a customer list.'

export interface WhyCard {
  title: string
  body: string
  icon: 'gate' | 'versions' | 'undo'
}

export const whyCards: readonly WhyCard[] = [
  {
    title: 'Governed',
    body: 'Approval gates stop the work for a person’s decision, and strict, advisory or autonomous modes set how often.',
    icon: 'gate',
  },
  {
    title: 'Versioned',
    body: 'Every artifact is versioned, hashed and immutable once locked, with a diff between any two versions.',
    icon: 'versions',
  },
  {
    title: 'Reversible',
    body: 'Roll back to any earlier checkpoint. History is never rewritten, and every change is in the audit trail.',
    icon: 'undo',
  },
]

export interface BentoTile {
  title: string
  body: string
  /** The Product section it links to (`/product#<anchor>`). */
  anchor: ProductSectionId
  icon: 'gate' | 'diff' | 'bot' | 'git' | 'tracker' | 'cloud'
}

export const bentoTiles: readonly BentoTile[] = [
  { title: 'Approval gates', body: 'Spec, plan, pre-release and escalation gates, with one-click approval links.', anchor: 'lifecycle-and-gates', icon: 'gate' },
  { title: 'Artifacts and diffs', body: 'Hashed, immutable versions of every spec and plan, compared side by side.', anchor: 'artifacts', icon: 'diff' },
  { title: 'Agents and providers', body: 'Specialist agents on Anthropic, OpenAI or Ollama, with your own keys.', anchor: 'agents', icon: 'bot' },
  { title: 'Git hosts and PRs', body: 'GitHub, GitLab and Bitbucket: one pull request per version, merged and tagged on release.', anchor: 'git', icon: 'git' },
  { title: 'Issue-tracker imports', body: 'Bring Jira, Linear, GitHub or GitLab issues in as a read-only snapshot.', anchor: 'issue-trackers', icon: 'tracker' },
  { title: 'Deployments and databases', body: 'Preview in review, production on release, with managed Postgres per environment.', anchor: 'deployments', icon: 'cloud' },
]

export const closingCta = {
  title: 'Take your next idea to a release',
  lead: 'Create an account, connect a Git host and an LLM key, and approve your first project request.',
} as const
