import { describe, expect, it } from 'vitest'

import { checklist, type GitConnectionsInput } from './checklist'

const noGit: GitConnectionsInput = { githubLinked: false, github: null, gitlab: [], bitbucket: [] }
const withGit: GitConnectionsInput = { ...noGit, gitlab: [{ id: 'g1' }] }
const llm = (n: number) => ({ credentials: Array.from({ length: n }, (_, i) => ({ id: String(i) })) })
const projects = (n: number) => Array.from({ length: n }, () => ({ myRole: 'owner' }))
const vercel = [{ provider: 'vercel' }]

describe('FR-207 / §4.30: onboarding checklist', () => {
  it('FR207_checklist_row1: nothing set up shows 0/4 with item 1 highlighted, not complete', () => {
    const c = checklist(noGit, llm(0), [], [])
    expect(c.items.map((i) => i.key)).toEqual(['git', 'llm', 'project', 'platform'])
    expect(c.done).toBe(0)
    expect(c.highlighted).toBe('git')
    expect(c.complete).toBe(false)
  })

  it('FR207_checklist_row2: Git and LLM but no projects shows 2/4 with item 3 highlighted', () => {
    const c = checklist(withGit, llm(1), [], [])
    expect(c.done).toBe(2)
    expect(c.highlighted).toBe('project')
    expect(c.complete).toBe(false)
  })

  it('FR207_checklist_row3: Git, LLM and 2 projects is 3/4 and complete (the optional platform is left)', () => {
    const c = checklist(withGit, llm(1), [], projects(2))
    expect(c.done).toBe(3)
    expect(c.highlighted).toBeNull()
    expect(c.complete).toBe(true)
    expect(c.items.find((i) => i.key === 'platform')).toMatchObject({ optional: true, done: false })
  })

  it('FR207_checklist_row4: no Git but LLM, a project and a platform is 3/4 with item 1 highlighted', () => {
    const c = checklist(noGit, llm(2), vercel, projects(1))
    expect(c.done).toBe(3)
    expect(c.highlighted).toBe('git')
    expect(c.complete).toBe(false)
  })

  it('checklist_git: a GitHub link, the GitHub App installed for the user, GitLab or Bitbucket each count', () => {
    const done = (git: GitConnectionsInput) => checklist(git, llm(0), [], []).items[0]?.done
    expect(done({ ...noGit, githubLinked: true })).toBe(true)
    expect(done({ ...noGit, github: { connected: true, installations: [] } })).toBe(true)
    expect(done({ ...noGit, github: { connected: false, installations: [{ id: 1 }] } })).toBe(true)
    expect(done({ ...noGit, github: { connected: false, installations: [] } })).toBe(false)
    expect(done({ ...noGit, bitbucket: [{ id: 'b' }] })).toBe(true)
  })

  it('checklist_membership: projects an Admin sees but isn’t a member of, and tracker connections, don’t count', () => {
    const c = checklist(withGit, llm(1), [{ provider: 'jira' }], [{ myRole: null }])
    expect(c.items.find((i) => i.key === 'project')?.done).toBe(false)
    expect(c.items.find((i) => i.key === 'platform')?.done).toBe(false)
  })

  it('checklist_actions: items 1, 2 and 4 go to Connections, item 3 to New project', () => {
    expect(checklist(noGit, llm(0), [], []).items.map((i) => i.href)).toEqual([
      '/connections',
      '/connections',
      '/projects/new',
      '/connections',
    ])
  })
})
