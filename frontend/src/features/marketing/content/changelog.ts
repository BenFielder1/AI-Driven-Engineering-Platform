import { docsUrl } from './site-nav'

/**
 * FR-219, FR-222, FR-229: one entry per version, newest first, labelled with its semver release version (0.N.0 for the
 * versions specified as vN). Dates are the merge dates of each version's PR on GitHub (`gh pr view <n> --json mergedAt`):
 * 0.1.0 #1, 0.2.0 #13, 0.3.0 #23 (the last of #16, #20–#23), 0.4.0 #27, 0.5.0 #28. From 0.6.0 on, the version's final
 * PR sets the date (the day it merges and is released, FR-230), since the tagged commit carries it. The newest entry's
 * version must equal backend/Directory.Build.props (scripts/ci/check-version.sh). Highlights come from the version's
 * spec and docs/history/<x.y.z>.md. 0.1.0–0.5.0 shipped under the name AIDE.
 */
export interface ChangelogEntry {
  version: string
  /** ISO date (YYYY-MM-DD) of the merge or release, or null until the version is released. */
  date: string | null
  title: string
  /** Shipped under the old name (0.1.0–0.5.0). */
  asAide: boolean
  highlights: readonly string[]
  specHref: string
}

export const IN_PREVIEW = 'In preview'

export const changelog: readonly ChangelogEntry[] = [
  {
    version: '0.6.0',
    date: '2026-11-08',
    title: 'Tevrin',
    asAide: false,
    highlights: [
      'The product is now Tevrin. The CLI command is tevrin, and the old command keeps working as a deprecated alias.',
      'A new brand and design system: the hexagon mark, slate surfaces, Geist type, and light and dark themes that follow your OS.',
      'A Home dashboard with a setup checklist, and a tidier app shell.',
      'This public site, and redesigned sign-in and sign-up pages.',
      'A documentation site published to GitHub Pages.',
      'Semver version numbers, with each release tagged and published on GitHub.',
    ],
    specHref: docsUrl('history/0.6.0/'),
  },
  {
    version: '0.5.0',
    date: '2026-11-04',
    title: 'Trackers, co-authorship and stack inference',
    asAide: true,
    highlights: [
      'Issue tracker imports from Jira, Linear, GitHub Issues and GitLab Issues, as read-only snapshots.',
      'Commits, merges and tags credit the accountable person with a Co-authored-by trailer.',
      'An optional Analysis phase: a Stack Analyst infers the stack from an existing repository.',
      'Shared deployment projects: parts on one platform are set up together.',
      'PostgreSQL 18 for the product’s own database.',
    ],
    specHref: docsUrl('history/0.5.0/'),
  },
  {
    version: '0.4.0',
    date: '2026-11-01',
    title: 'Stacks, Git hosts and databases',
    asAide: true,
    highlights: [
      'Stack composition from a versioned catalog, with compatibility rules, replacing presets.',
      'Specialist agents that load a pack skill for their layer.',
      'GitLab and Bitbucket Cloud join GitHub.',
      'Vercel, Netlify, Cloudflare, GitHub Pages, Render and Railway deployments, with preview environments.',
      'Managed databases: Supabase, Render Postgres and Railway Postgres.',
      'LLM provider and model chosen per project, with streaming and continued long answers.',
    ],
    specHref: docsUrl('history/0.4.0/'),
  },
  {
    version: '0.3.0',
    date: '2026-10-24',
    title: 'Next.js and Railway',
    asAide: true,
    highlights: [
      'The web app moved to Next.js, with a Node server that proxies the API and live updates.',
      'Deploys to Railway preview and production environments, defined as code.',
      'Smoke tests after every deploy, from GitHub Actions.',
    ],
    specHref: docsUrl('history/0.3.0/'),
  },
  {
    version: '0.2.0',
    date: '2026-10-22',
    title: 'Release versions and integrations',
    asAide: true,
    highlights: [
      'Semver release versions with internal checkpoints, and new versions after a release.',
      'Import an existing GitHub repository; a Repository Analyst summarises it first.',
      'Read-only repository tools for agents, with secret redaction.',
      'The GitHub App, with short-lived installation tokens and GitHub Releases.',
      'Per-user LLM connections, and Vercel preview and production deployments.',
      'One page per lifecycle stage, with execution and review history.',
    ],
    specHref: docsUrl('history/0.2.0/'),
  },
  {
    version: '0.1.0',
    date: '2026-10-10',
    title: 'The governed lifecycle',
    asAide: true,
    highlights: [
      'The six-phase lifecycle with approval gates in strict, advisory and autonomous modes.',
      'Versioned, hash-checked artifacts with diffs, invalidation and integrity alerts.',
      'Five agents behind one LLM abstraction: Anthropic, OpenAI and Ollama.',
      'Step-by-step code generation with the Execution Guardian, review cycles, merge and tag.',
      'Live updates, notifications with one-click approval links, admin pages and the CLI.',
    ],
    specHref: docsUrl('history/0.1.0/'),
  },
]

/** "4 Nov 2026" (UTC, so it never shifts a day), or "In preview". */
export function formatChangelogDate(date: string | null): string {
  if (date === null) {
    return IN_PREVIEW
  }
  return new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }).format(
    new Date(`${date}T00:00:00Z`),
  )
}

/** FR-219: the suffix on the 0.1.0–0.5.0 titles. */
export const AS_AIDE = '(as AIDE)'
