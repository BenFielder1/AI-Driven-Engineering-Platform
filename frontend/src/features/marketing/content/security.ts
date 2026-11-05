import { docsUrl, type NavLink } from './site-nav'

/**
 * FR-217, FR-222: factual security statements only, each with the docs page that backs it. Sources: docs/guides
 * (connections, github-app, notifications), docs/reference (architecture, testing), v1 §4.3/§4.6, v2 FR-71, v3
 * security headers, SPEC §8.
 */
export interface SecurityFact {
  id: string
  title: string
  body: string
  icon: 'key' | 'user' | 'clock' | 'eye' | 'lock' | 'list' | 'shield' | 'globe' | 'flask'
  link: NavLink
}

export const securityFacts: readonly SecurityFact[] = [
  {
    id: 'secrets',
    title: 'Secrets are encrypted and never shown again',
    body: 'Git, LLM, tracker and platform tokens, webhook URLs and the GitHub App’s keys are checked when added, encrypted at rest with ASP.NET Core Data Protection, and never returned by the API.',
    icon: 'key',
    link: { label: 'Connections guide', href: docsUrl('guides/connections/'), external: true },
  },
  {
    id: 'personal-connections',
    title: 'Connections are personal',
    body: 'Each person adds their own connections. A project Owner can bind only their own connection to a project; nobody can use someone else’s.',
    icon: 'user',
    link: { label: 'Connections guide', href: docsUrl('guides/connections/'), external: true },
  },
  {
    id: 'github-tokens',
    title: 'Short-lived GitHub App tokens',
    body: 'Background Git work uses GitHub App installation tokens minted per job, scoped to the repository and cached for at most 55 minutes. They never touch disk, remote URLs or logs.',
    icon: 'clock',
    link: { label: 'GitHub App guide', href: docsUrl('guides/github-app/'), external: true },
  },
  {
    id: 'repo-tools',
    title: 'Read-only repository tools, with secret redaction',
    body: 'Agents read code through list, read and search tools on a credential-free snapshot. They can’t run commands. Lines that look like secrets are redacted before they reach a model, and generated code is scanned for secrets before it is committed.',
    icon: 'eye',
    link: { label: 'v2 notes (repository tools)', href: docsUrl('history/v2/'), external: true },
  },
  {
    id: 'artifacts',
    title: 'Locked artifacts are immutable and hashed',
    body: 'Approving an artifact locks it with a SHA-256 content hash. New content is a new version; rollback and invalidation never rewrite history, and a hash mismatch raises an integrity alert.',
    icon: 'lock',
    link: { label: 'Architecture', href: docsUrl('reference/architecture/'), external: true },
  },
  {
    id: 'audit',
    title: 'Every change is an audit event',
    body: 'Transitions, gate decisions, artifact changes, agent runs and releases are recorded as append-only events, with a per-project activity feed and an admin event log.',
    icon: 'list',
    link: { label: 'Architecture', href: docsUrl('reference/architecture/'), external: true },
  },
  {
    id: 'headers',
    title: 'Content Security Policy and security headers',
    body: 'Every page gets a nonce-based Content Security Policy, frame-ancestors none, X-Content-Type-Options nosniff and a strict referrer policy. Approval links are signed and single-use, and webhooks are HMAC-signed.',
    icon: 'shield',
    link: { label: 'Architecture', href: docsUrl('reference/architecture/'), external: true },
  },
  {
    id: 'no-third-party',
    title: 'No third-party scripts',
    body: 'This site and the app load no third-party scripts, fonts, images or analytics. Fonts are self-hosted.',
    icon: 'globe',
    link: { label: 'Configuration', href: docsUrl('reference/configuration/'), external: true },
  },
  {
    id: 'tests',
    title: 'Agents are faked in tests',
    body: 'Every agent call sits behind one provider abstraction. Tests use a deterministic Fake provider and stub HTTP, so no test sends your data to a real model or platform.',
    icon: 'flask',
    link: { label: 'Testing', href: docsUrl('reference/testing/'), external: true },
  },
]
