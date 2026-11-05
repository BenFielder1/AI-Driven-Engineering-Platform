/**
 * FR-221, FR-222: the marketing routes, their metadata (title, description) and the sitemap. Pure data (no React), so
 * server layouts, the sitemap route and tests can import it. Titles: `<Page> · Tevrin`, and `Tevrin — <tagline>` for `/`.
 */
import { TAGLINE } from '../../../components/brand-geometry'
import { marketingPaths } from '../../../lib/app-paths'

export { TAGLINE }

export interface MarketingPage {
  path: MarketingPath
  /** Short name (nav label, page h1 fallback). */
  name: string
  /** The document title. */
  title: string
  description: string
}

/** The marketing routes in nav order (the list lives with the app's route table, src/lib/app-paths.ts). */
export const marketingPathList = marketingPaths

export type MarketingPath = (typeof marketingPathList)[number]

const page = (path: MarketingPath, name: string, description: string): MarketingPage => ({
  path,
  name,
  title: path === '/' ? `Tevrin — ${TAGLINE}` : `${name} · Tevrin`,
  description,
})

export const marketingPages: Record<MarketingPath, MarketingPage> = {
  '/': page(
    '/',
    'Home',
    'Tevrin coordinates AI agents through refinement, specification, planning, execution, review and release, with approval gates, versioned artifacts and rollback.',
  ),
  '/product': page(
    '/product',
    'Product',
    'Lifecycle and gates, versioned artifacts, agents and LLM providers, Git hosts, issue-tracker imports, deployments, notifications, admin and the CLI.',
  ),
  '/how-it-works': page(
    '/how-it-works',
    'How it works',
    'From idea to release: each phase’s input, agent, artifact and approval gate, plus review cycles, release versions and rollback.',
  ),
  '/integrations': page(
    '/integrations',
    'Integrations',
    'The Git hosts, issue trackers, LLM providers, deployment platforms, databases and notification channels Tevrin works with.',
  ),
  '/self-host': page(
    '/self-host',
    'Self-host',
    'Run Tevrin yourself on Railway, Docker Desktop Kubernetes or the local dev loop. Free while in preview.',
  ),
  '/security': page(
    '/security',
    'Security',
    'How Tevrin handles secrets, Git access, artifacts, audit events and browser security.',
  ),
  '/faq': page('/faq', 'FAQ', 'Answers to common questions about Tevrin: gates, rollback, models, keys, self-hosting and more.'),
  '/changelog': page('/changelog', 'Changelog', 'What changed in each Tevrin version, newest first.'),
}
