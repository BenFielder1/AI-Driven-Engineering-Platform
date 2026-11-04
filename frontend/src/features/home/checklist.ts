/**
 * SPEC §4.30 onboarding checklist (FR-207). Pure, so the rule table is unit-tested (checklist.test.ts).
 *
 * Inputs, adapted to what the existing endpoints return:
 * - `connections`: the user's Git host connections. `github` is GET /api/me/connections/github (`connected` is the
 *   user's GitHub link; `installations` are the instance GitHub App's installations the user can use) or `null` when no
 *   App is registered; `githubLinked` is `UserDto.githubLinked` (OAuth sign-in link); `gitlab` and `bitbucket` are
 *   GET /api/me/connections/gitlab|bitbucket (sign-in links and tokens).
 * - `llmConnections`: GET /api/me/connections/llm. Only saved credentials count; the always-on keyless providers
 *   (Ollama, Fake) are not a connection the user added.
 * - `platformConnections`: GET /api/me/connections/platforms. Issue trackers (Jira, Linear) aren't deployment platforms.
 * - `projects`: GET /api/projects rows. Membership is `myRole !== null` (an Admin sees projects they aren't in).
 */
export interface GitConnectionsInput {
  githubLinked: boolean
  github: { connected: boolean; installations: readonly unknown[] } | null
  gitlab: readonly unknown[]
  bitbucket: readonly unknown[]
}

export type ChecklistItemKey = 'git' | 'llm' | 'project' | 'platform'

export interface ChecklistItem {
  key: ChecklistItemKey
  label: string
  description: string
  /** Where the item's action goes. */
  href: '/connections' | '/projects/new'
  actionLabel: string
  optional: boolean
  done: boolean
}

export interface Checklist {
  items: ChecklistItem[]
  /** Items done, out of `items.length` (4). */
  done: number
  /** The first required item not done, or null. */
  highlighted: ChecklistItemKey | null
  /** Items 1–3 done (the optional item doesn't block). */
  complete: boolean
}

export function hasGitConnection(git: GitConnectionsInput): boolean {
  return (
    git.githubLinked ||
    (git.github !== null && (git.github.connected || git.github.installations.length > 0)) ||
    git.gitlab.length > 0 ||
    git.bitbucket.length > 0
  )
}

export function checklist(
  connections: GitConnectionsInput,
  llmConnections: { credentials: readonly unknown[] },
  platformConnections: readonly { provider: string }[],
  projects: readonly { myRole: string | null }[],
): Checklist {
  const items: ChecklistItem[] = [
    {
      key: 'git',
      label: 'Connect a Git host',
      description: 'Link GitHub, GitLab or Bitbucket so agents can read and write your repositories.',
      href: '/connections',
      actionLabel: 'Go to Connections',
      optional: false,
      done: hasGitConnection(connections),
    },
    {
      key: 'llm',
      label: 'Add an AI provider',
      description: 'Add an Anthropic or OpenAI key for the agents to use.',
      href: '/connections',
      actionLabel: 'Go to Connections',
      optional: false,
      done: llmConnections.credentials.length > 0,
    },
    {
      key: 'project',
      label: 'Create or import a project',
      description: 'Start from an idea or import an existing repository.',
      href: '/projects/new',
      actionLabel: 'New project',
      optional: false,
      done: projects.some((p) => p.myRole !== null),
    },
    {
      key: 'platform',
      label: 'Connect a deployment platform',
      description: 'Optional: deploy previews and releases to Vercel, Netlify, Cloudflare, Render or Railway.',
      href: '/connections',
      actionLabel: 'Go to Connections',
      optional: true,
      done: platformConnections.some((c) => c.provider !== 'jira' && c.provider !== 'linear'),
    },
  ]
  const highlighted = items.find((i) => !i.optional && !i.done)?.key ?? null
  return {
    items,
    done: items.filter((i) => i.done).length,
    highlighted,
    complete: highlighted === null,
  }
}
