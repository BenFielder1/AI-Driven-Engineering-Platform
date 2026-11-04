import type { BrandIconSlug } from './brand-icons'
import { docsUrl } from './site-nav'

/**
 * FR-212, FR-215, FR-222: what Tevrin works with. Every name here is implemented in the backend (the provider enums
 * listed in pages/integrations-page.test.tsx); nothing planned or unimplemented is listed. Sources: docs/guides/*.md and the
 * v1–v5 specs.
 */

/** Marks for brands Simple Icons doesn't carry: a neutral icon, never a lookalike logo. */
export type GenericIcon = 'openai' | 'slack' | 'teams' | 'email' | 'webhook' | 'in-app'

export type IntegrationIcon = { brand: BrandIconSlug } | { generic: GenericIcon }

export interface Integration {
  name: string
  icon: IntegrationIcon
  /** One line: what Tevrin does with it. */
  does: string
}

export interface IntegrationGroup {
  id: 'git-hosts' | 'issue-trackers' | 'llm-providers' | 'deployment-platforms' | 'databases' | 'notifications'
  title: string
  summary: string
  docsHref: string
  items: readonly Integration[]
}

export const integrationGroups: readonly IntegrationGroup[] = [
  {
    id: 'git-hosts',
    title: 'Git hosts',
    summary: 'Sign in with your Git host, pick or import a repository, and let Tevrin open, check and merge the pull request.',
    docsHref: docsUrl('guides/connections/'),
    items: [
      {
        name: 'GitHub',
        icon: { brand: 'github' },
        does: 'Sign-in, repo picker and creation, pull requests, check runs, merge, tags and GitHub Releases through the Tevrin GitHub App.',
      },
      {
        name: 'GitLab',
        icon: { brand: 'gitlab' },
        does: 'gitlab.com and allow-listed self-managed instances: sign-in, merge requests, CI status, releases and import.',
      },
      {
        name: 'Bitbucket',
        icon: { brand: 'bitbucket' },
        does: 'Bitbucket Cloud: sign-in, repo picker, pull requests, build status, merge and import.',
      },
      {
        name: 'Local Git',
        icon: { brand: 'git' },
        does: 'A Git repository on the Tevrin server, for trying the lifecycle without a Git host.',
      },
    ],
  },
  {
    id: 'issue-trackers',
    title: 'Issue trackers',
    summary: 'Import work items as a read-only snapshot that the agents read with your idea. Tevrin only reads from trackers.',
    docsHref: docsUrl('guides/issue-trackers/'),
    items: [
      { name: 'Jira', icon: { brand: 'jira' }, does: 'Imports Jira Cloud issues by key or JQL query, with a scoped read-only token.' },
      { name: 'Linear', icon: { brand: 'linear' }, does: 'Imports Linear issues with a read-only API key.' },
      { name: 'GitHub Issues', icon: { brand: 'github' }, does: 'Imports issues through your GitHub connection and the GitHub App.' },
      { name: 'GitLab Issues', icon: { brand: 'gitlab' }, does: 'Imports issues through your GitLab connection.' },
    ],
  },
  {
    id: 'llm-providers',
    title: 'LLM providers',
    summary: 'Every agent runs behind one provider abstraction. Choose the provider and model per project, with your own key.',
    docsHref: docsUrl('guides/llm-providers/'),
    items: [
      { name: 'Anthropic', icon: { brand: 'anthropic' }, does: 'Claude models for every agent, with streaming and continued long answers.' },
      { name: 'OpenAI', icon: { generic: 'openai' }, does: 'GPT models for every agent, with your own key or an allowed system key.' },
      { name: 'Ollama', icon: { brand: 'ollama' }, does: 'Local models with no API key, on Kubernetes and the dev loop (turned off on Railway).' },
    ],
  },
  {
    id: 'deployment-platforms',
    title: 'Deployment platforms',
    summary: 'Preview deployments during review and production deployments on release, built from your Git host.',
    docsHref: docsUrl('guides/deployments-and-databases/'),
    items: [
      { name: 'Vercel', icon: { brand: 'vercel' }, does: 'Frontend previews in review and production on release, with promote and redeploy.' },
      { name: 'Netlify', icon: { brand: 'netlify' }, does: 'Frontend preview and production deployments.' },
      { name: 'Cloudflare', icon: { brand: 'cloudflare' }, does: 'Workers Builds and Pages deployments for preview and production.' },
      { name: 'GitHub Pages', icon: { brand: 'githubpages' }, does: 'Static frontends, deployed through the Tevrin GitHub App.' },
      { name: 'Render', icon: { brand: 'render' }, does: 'Static sites and web services, with separate preview services.' },
      { name: 'Railway', icon: { brand: 'railway' }, does: 'Frontend, backend and database in one shared Railway project (repositories on GitHub).' },
    ],
  },
  {
    id: 'databases',
    title: 'Databases',
    summary: 'Provision or link a database per environment, wire it into env vars, and migrate it safely.',
    docsHref: docsUrl('guides/deployments-and-databases/'),
    items: [
      { name: 'Supabase', icon: { brand: 'supabase' }, does: 'Creates or links a project (BaaS or host mode) and applies the version’s migrations on release.' },
      { name: 'Render Postgres', icon: { brand: 'render' }, does: 'Creates or links a Postgres database per environment and wires its URL in.' },
      { name: 'Railway Postgres', icon: { brand: 'railway' }, does: 'Creates or links a Postgres database per environment and wires its URL in.' },
      { name: 'PostgreSQL', icon: { brand: 'postgresql' }, does: 'Tevrin keeps its own state in PostgreSQL 18.' },
    ],
  },
  {
    id: 'notifications',
    title: 'Notification channels',
    summary: 'Gates waiting, releases, failures and connection problems reach the right people, with signed one-click approval links.',
    docsHref: docsUrl('guides/notifications/'),
    items: [
      { name: 'In-app', icon: { generic: 'in-app' }, does: 'A notification list in Tevrin, updated live.' },
      { name: 'Email', icon: { generic: 'email' }, does: 'Email over your SMTP server, including one-click approval links.' },
      { name: 'Slack', icon: { generic: 'slack' }, does: 'Project messages to a Slack incoming webhook.' },
      { name: 'Microsoft Teams', icon: { generic: 'teams' }, does: 'Project messages to a Teams incoming webhook.' },
      { name: 'Webhook', icon: { generic: 'webhook' }, does: 'Signed (HMAC-SHA256) JSON deliveries to your own endpoint.' },
    ],
  },
]

/** FR-212: the "Works with" strip on Home, in the spec's order. Not customers. */
export const worksWith: readonly { name: string; icon: IntegrationIcon }[] = [
  { name: 'GitHub', icon: { brand: 'github' } },
  { name: 'GitLab', icon: { brand: 'gitlab' } },
  { name: 'Bitbucket', icon: { brand: 'bitbucket' } },
  { name: 'Jira', icon: { brand: 'jira' } },
  { name: 'Linear', icon: { brand: 'linear' } },
  { name: 'Railway', icon: { brand: 'railway' } },
  { name: 'Render', icon: { brand: 'render' } },
  { name: 'Vercel', icon: { brand: 'vercel' } },
  { name: 'Netlify', icon: { brand: 'netlify' } },
  { name: 'Cloudflare', icon: { brand: 'cloudflare' } },
  { name: 'Supabase', icon: { brand: 'supabase' } },
  { name: 'Anthropic', icon: { brand: 'anthropic' } },
  { name: 'OpenAI', icon: { generic: 'openai' } },
  { name: 'Ollama', icon: { brand: 'ollama' } },
]
