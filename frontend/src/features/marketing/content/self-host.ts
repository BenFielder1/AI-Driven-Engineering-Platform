import type { FaqItem } from './faq'
import { docsUrl, type NavLink } from './site-nav'

/**
 * FR-216, FR-212, FR-222: Self-host page copy (D-V6-5: no pricing page). The commands themselves live in
 * self-host-commands.ts, the one source checked against the docs. Sources: docs/getting-started, docs/operations,
 * docs/guides, SPEC.md §1 and §9.
 */
export const PREVIEW_NOTE = 'Free while in preview. No billing.'

/** Requirements (FR-216), also the short list on Home (FR-212). */
export const selfHostRequirements: readonly string[] = ['Docker', 'PostgreSQL 18', 'An LLM provider key, or Ollama (not on Railway)']

export const pathSummaries: Record<'railway' | 'kubernetes' | 'dev-loop', string> = {
  railway:
    'A public web service, a private API service with a data volume, and Railway Postgres, in preview and production environments defined as code and deployed by GitHub Actions.',
  kubernetes:
    'Kustomize manifests for the Docker Desktop Kubernetes cluster. One script builds the images, migrates the database, rolls out and smoke-tests at http://localhost:8080.',
  'dev-loop':
    'PostgreSQL in Docker Compose, the API with dotnet run and the web app with npm, for working on Tevrin itself.',
}

export interface ConfigureItem {
  title: string
  body: string
  link: NavLink
}

/** What you configure after installing (FR-216). */
export const configureItems: readonly ConfigureItem[] = [
  {
    title: 'Git sign-in',
    body: 'Email and password work out of the box. Add GitHub, GitLab or Bitbucket sign-in when you register their OAuth apps.',
    link: { label: 'Connections and sign-in', href: docsUrl('guides/connections/'), external: true },
  },
  {
    title: 'GitHub App',
    body: 'Register the Tevrin GitHub App in one click from Admin, Integrations, then install it on the repositories Tevrin should use.',
    link: { label: 'GitHub App guide', href: docsUrl('guides/github-app/'), external: true },
  },
  {
    title: 'LLM and platform providers',
    body: 'Set system keys or let each person add their own. Turn providers on or off per deployment, and connect deployment platforms per user.',
    link: { label: 'LLM providers guide', href: docsUrl('guides/llm-providers/'), external: true },
  },
  {
    title: 'Notifications',
    body: 'Set SMTP for email and a signing key for one-click approval links. Slack, Teams and webhooks are set per project.',
    link: { label: 'Notifications guide', href: docsUrl('guides/notifications/'), external: true },
  },
]

/** The Self-host FAQ: upgrades and data (FR-216). */
export const selfHostFaqs: readonly FaqItem[] = [
  {
    id: 'upgrade',
    question: 'How do I upgrade?',
    answer: [
      'Deploy the new version the same way you installed it. Database changes ship as EF Core migrations, which run before the new API starts: as a migration job on Kubernetes and a pre-deploy step on Railway. Migrations are forward-only.',
      'Moving local data from PostgreSQL 17 to 18 is a dump and restore; the upgrade guide has the commands.',
    ],
    link: { label: 'PostgreSQL 18 upgrade', href: docsUrl('operations/postgres-18-upgrade/'), external: true },
  },
  {
    id: 'data',
    question: 'Where is my data, and what should I back up?',
    answer: [
      'Everything lives in your PostgreSQL database, plus a data volume that holds the working copies and the Data Protection keys that encrypt stored credentials.',
      'Back up both together. Losing the keys makes saved secrets unreadable, so people would need to add their connections again.',
    ],
    link: { label: 'Railway operations', href: docsUrl('operations/railway/'), external: true },
  },
  {
    id: 'teardown',
    question: 'Can I remove Tevrin and keep my data?',
    answer: [
      'Yes. On Kubernetes, the teardown script removes the app and keeps the database and volume. Only --wipe-data deletes them, after you confirm.',
    ],
    link: { label: 'Local Kubernetes', href: docsUrl('operations/local-kubernetes/'), external: true },
  },
]
