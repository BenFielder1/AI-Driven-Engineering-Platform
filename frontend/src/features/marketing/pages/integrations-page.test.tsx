import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { axeViolations, setTheme, THEMES } from '../../../test/axe'
import { renderRoute } from '../../../test/render'
import { brandIcons } from '../content/brand-icons'
import { integrationGroups, worksWith } from '../content/integrations'

afterEach(() => {
  setTheme('light')
})

/**
 * FR-215 / FR-222: every integration the site names is implemented. Derived by hand from the backend enums (update
 * this list when they change, never to admit something unimplemented):
 * - Git hosts: `GitProvider` (GithubPat, GithubApp, Gitlab, Bitbucket, Local) in Aide.Domain/Entities/EntityEnums.cs
 * - Issue trackers: `TrackerProvider` (Jira, Linear, Github, Gitlab) in Aide.Domain/Trackers/SourceItem.cs
 * - LLM providers: `LlmProvider` (Anthropic, Openai, Ollama; Fake is test-only) in EntityEnums.cs
 * - Deployment platforms: `DeploymentProvider` (Vercel, Netlify, Cloudflare, Render, Railway, GithubPages; not Fake) in
 *   Aide.Domain/Entities/Deployment.cs
 * - Databases: `ManagedDatabaseProvider` (Supabase, Render, Railway; External and Fake not listed) in
 *   Aide.Domain/Entities/ManagedDatabase.cs, plus PostgreSQL, Tevrin's own database (D-V5-1)
 * - Notification channels: `NotificationChannelType` (Email, Slack, Teams, Webhook) in EntityEnums.cs, plus in-app (FR-47)
 */
const BACKEND_SUPPORTED: Record<string, readonly string[]> = {
  'git-hosts': ['GitHub', 'GitLab', 'Bitbucket', 'Local Git'],
  'issue-trackers': ['Jira', 'Linear', 'GitHub Issues', 'GitLab Issues'],
  'llm-providers': ['Anthropic', 'OpenAI', 'Ollama'],
  'deployment-platforms': ['Vercel', 'Netlify', 'Cloudflare', 'GitHub Pages', 'Render', 'Railway'],
  databases: ['Supabase', 'Render Postgres', 'Railway Postgres', 'PostgreSQL'],
  notifications: ['In-app', 'Email', 'Slack', 'Microsoft Teams', 'Webhook'],
}

const ALL_SUPPORTED = new Set(Object.values(BACKEND_SUPPORTED).flat())

describe('FR-215 Integrations', () => {
  it('FR215_names_supported: every listed name is in the backend-supported list for its group', () => {
    for (const group of integrationGroups) {
      const supported = BACKEND_SUPPORTED[group.id] ?? []
      for (const item of group.items) {
        expect(supported, `${group.id}: ${item.name}`).toContain(item.name)
      }
    }
  })

  it('FR212_FR215_works_with_supported: every "Works with" name on Home is supported', () => {
    for (const item of worksWith) {
      expect(ALL_SUPPORTED.has(item.name), item.name).toBe(true)
    }
  })

  it('FR222_brand_marks: brand marks are Simple Icons path data; every icon resolves', () => {
    for (const item of [...integrationGroups.flatMap((g) => g.items), ...worksWith]) {
      if ('brand' in item.icon) {
        expect(brandIcons[item.icon.brand], item.name).toMatch(/^[MmZzLlHhVvCcSsQqTtAa0-9.,\s-]+$/)
      }
    }
  })

  it('FR215_page: grouped cards with a one-line description each, and a docs link per group', async () => {
    await renderRoute('/integrations')
    await screen.findByRole('heading', { level: 1, name: 'Works with the tools you already use' })

    expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual(
      expect.arrayContaining(['Git hosts', 'Issue trackers', 'LLM providers', 'Deployment platforms', 'Databases', 'Notification channels']),
    )
    const cards = screen.getAllByTestId('integration-card')
    expect(cards).toHaveLength(integrationGroups.reduce((n, g) => n + g.items.length, 0))
    for (const card of cards) {
      expect(within(card).getByRole('heading', { level: 3 }).textContent).not.toBe('')
      expect(card.querySelectorAll('p')).toHaveLength(1)
    }
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
  })

  it.each(THEMES.map((t) => [t]))('FR215_axe: /integrations has no axe violations (%s)', async (theme) => {
    setTheme(theme)
    const { container } = await renderRoute('/integrations')
    await screen.findByRole('heading', { level: 1 })

    expect(await axeViolations(container)).toEqual([])
  })
})
